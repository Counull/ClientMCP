using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ClientMcp
{
    /// <summary>编译不得访问宿主对象；返回的同步委托只由宿主线程执行。</summary>
    public interface IScriptCompiler
    {
        Func<ScriptGlobals, object> Compile(string code);
    }

    public sealed class ScriptGlobals
    {
        readonly ReflectionSession reflection;
        public JObject Parameters { get; }
        public ScriptGlobals(ReflectionSession reflection, JObject parameters)
        { this.reflection = reflection; Parameters = parameters; }
        public T Get<T>(string handle) => (T)reflection.GetObject(handle);
        public T Root<T>(string name) => (T)reflection.GetRoot(name);
    }

    /// <summary>有界脚本与回执缓存；所有状态操作仅由宿主线程串行执行。</summary>
    internal sealed class ScriptSession
    {
        readonly ReflectionSession reflection;
        readonly string sessionId;
        readonly IScriptCompiler compiler;
        readonly SemaphoreSlim compiling = new SemaphoreSlim(2, 2);
        readonly Dictionary<string, Prepared> scripts = new Dictionary<string, Prepared>(StringComparer.Ordinal);
        readonly Dictionary<string, Operation> operations = new Dictionary<string, Operation>(StringComparer.Ordinal);
        public bool Enabled => compiler != null;
        public ScriptSession(ReflectionSession reflection, string sessionId, IScriptCompiler compiler)
        { this.reflection = reflection; this.sessionId = sessionId; this.compiler = compiler; }

        // 只做不可变编译，不访问 scripts / operations，网络线程可调用。
        public Func<ScriptGlobals, object> CompileIfNeeded(JObject command)
        {
            if ((string)command?["op"] != "script") return null;
            if (!Enabled) throw new InvalidOperationException("C# 脚本未启用；宿主须显式提供脚本编译器。");
            if ((string)command["action"] != "prepare") return null;
            Id(command, "scriptId");
            var code = Text(command, "code");
            if (code.Length > 65536) throw new ArgumentException("脚本不得超过 65536 字符。");
            if (!compiling.Wait(0)) throw new InvalidOperationException("脚本编译队列已满。");
            try { return compiler.Compile(code); } finally { compiling.Release(); }
        }

        public JObject Execute(JObject command, Func<ScriptGlobals, object> compiled)
        {
            if (!Enabled) throw new InvalidOperationException("C# 脚本未启用。");
            var action = Text(command, "action");
            if (action == "prepare")
            {
                var id = Id(command, "scriptId"); var code = Text(command, "code");
                if (scripts.TryGetValue(id, out var existing))
                {
                    if (existing.Code != code) throw new InvalidOperationException("scriptId 已存在且代码不同，请先释放或使用新编号。");
                    return new JObject { ["scriptId"] = id, ["sessionId"] = sessionId, ["cacheHit"] = true };
                }
                if (scripts.Count >= 32) throw new InvalidOperationException("脚本数量已达到 32，请显式释放。");
                if (compiled == null) throw new InvalidOperationException("缺少已编译脚本。");
                scripts.Add(id, new Prepared(code, compiled));
                return new JObject { ["scriptId"] = id, ["sessionId"] = sessionId, ["cacheHit"] = false };
            }
            if (action == "list") return new JObject { ["sessionId"] = sessionId, ["scripts"] = new JArray(scripts.Keys) };
            if (Text(command, "sessionId") != sessionId) throw new InvalidOperationException("sessionId 与当前会话不符。");
            if (action == "result")
            {
                var id = Id(command, "operationId");
                if (!operations.TryGetValue(id, out var previous)) throw new KeyNotFoundException("未找到操作回执。");
                return Receipt(previous, true);
            }
            var scriptId = Id(command, "scriptId");
            if (action == "release")
            {
                var removed = scripts.Remove(scriptId);
                var ids = new List<string>();
                foreach (var pair in operations) if (pair.Value.ScriptId == scriptId) ids.Add(pair.Key);
                foreach (var id in ids) operations.Remove(id);
                return new JObject { ["released"] = removed, ["sessionId"] = sessionId };
            }
            if (action != "run") throw new ArgumentException("script action 只能为 prepare/run/result/list/release。");
            var operationId = Id(command, "operationId");
            if (command["parameters"] != null && !(command["parameters"] is JObject)) throw new ArgumentException("parameters 必须是对象。");
            var parameters = (JObject)(command["parameters"]?.DeepClone() ?? new JObject());
            if (operations.TryGetValue(operationId, out var operation))
            {
                if (operation.ScriptId != scriptId || !JToken.DeepEquals(operation.Parameters, parameters))
                    throw new InvalidOperationException("operationId 已用于不同的脚本或参数。");
                return Receipt(operation, true);
            }
            if (!scripts.TryGetValue(scriptId, out var script)) throw new KeyNotFoundException("脚本未准备或已释放。");
            if (operations.Count >= 128) throw new InvalidOperationException("脚本回执数量已达到 128，请显式释放脚本。");
            operation = new Operation(scriptId, (JObject)parameters.DeepClone(), new JObject
            {
                ["sessionId"] = sessionId, ["scriptId"] = scriptId, ["operationId"] = operationId, ["state"] = "running"
            });
            operations.Add(operationId, operation);
            try
            {
                var value = script.Run(new ScriptGlobals(reflection, parameters));
                operation.Result["result"] = reflection.Encode(value);
                operation.Result["state"] = "completed";
            }
            catch (Exception error)
            {
                operation.Result["state"] = "failed";
                operation.Result["error"] = ClientMcpHost.Failure(error)["error"];
            }
            return Receipt(operation, false);
        }
        public void Clear() { scripts.Clear(); operations.Clear(); }
        static JObject Receipt(Operation operation, bool cacheHit)
        { var result = (JObject)operation.Result.DeepClone(); result["cacheHit"] = cacheHit; return result; }
        static string Text(JObject request, string name)
        {
            if (request[name]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)request[name]))
                throw new ArgumentException(name + " 必须为非空字符串。");
            return (string)request[name];
        }
        static string Id(JObject request, string name)
        {
            var value = Text(request, name);
            if (value.Length > 128) throw new ArgumentException(name + " 不得超过 128 字符。");
            foreach (char c in value) if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'))
                throw new ArgumentException(name + " 只支持字母、数字、连字符、下划线与点。");
            return value;
        }
        sealed class Prepared
        {
            public readonly string Code; public readonly Func<ScriptGlobals, object> Run;
            public Prepared(string code, Func<ScriptGlobals, object> run) { Code = code; Run = run; }
        }
        sealed class Operation
        {
            public readonly string ScriptId; public readonly JObject Parameters; public readonly JObject Result;
            public Operation(string scriptId, JObject parameters, JObject result)
            { ScriptId = scriptId; Parameters = parameters; Result = result; }
        }
    }
}
