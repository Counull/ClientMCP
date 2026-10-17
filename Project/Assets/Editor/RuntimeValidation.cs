using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace ClientMcp.Development
{
    /// <summary>开发工程内的反射验收和构建入口；不注入消费工程或公开包。</summary>
    public static class RuntimeValidation
    {
        private static int _checks;

        public static void RunChecks()
        {
            _checks = 0;
            var session = new ReflectionSession();
            var owner = new GameObject("ClientMCP Validation Probe");
            owner.AddComponent<RuntimeProbe>();
            var previousShared = RuntimeProbe.ProbeValue.Shared;
            try
            {
                var valueType = typeof(RuntimeProbe.ProbeValue).FullName;
                var first = Run(session, "create", "type", valueType, "args", new JArray(11),
                    "parameterTypes", new JArray("System.Int32"))["value"];
                var metadata = Run(session, "inspect", "target", first, "limit", 200);
                Check(((JArray)metadata["items"]).Any(item => (string)item["name"] == "SideEffectValue"),
                    "inspect 应列出属性元数据");
                Check((int)Run(session, "get", "target", first, "member", "GetterReads")["value"] == 0,
                    "inspect 不得执行 getter");
                Check((int)Run(session, "get", "target", first, "member", "_baseValue")["value"] == 7,
                    "应读取私有继承字段");
                Run(session, "set", "target", first, "member", "_baseValue", "value", 19,
                    "declaringType", typeof(RuntimeProbe.ProbeBaseValue).FullName);
                Check((int)Run(session, "invoke", "target", first, "member", "ReadBaseValue")["value"] == 19,
                    "应通过 declaringType 写入私有基类字段");
                Run(session, "set", "target", first, "member", "_value", "value", 23);
                Check((int)Run(session, "get", "target", first, "member", "_value")["value"] == 23,
                    "应读写私有实例字段");
                Run(session, "set", "type", valueType, "member", "Shared", "kind", "property", "value", 47);
                Check((int)Run(session, "get", "type", valueType, "member", "Shared", "kind", "property")["value"] == 47,
                    "应读写静态属性");
                Check((int)Run(session, "get", "target", first, "member", "SideEffectValue", "kind", "property")["value"] == 23,
                    "显式读取属性应取得值");
                Check((int)Run(session, "get", "target", first, "member", "GetterReads")["value"] == 1,
                    "显式 get 应仅执行一次 getter");

                var second = Run(session, "create", "type", valueType, "args", new JArray(32))["value"];
                Check((int)Run(session, "invoke", "target", first, "member", "Accept", "args", new JArray(second.DeepClone()))["value"] == 32,
                    "构造结果的句柄应可用作方法参数");
                Expect<AmbiguousMatchException>(() => Run(session, "invoke", "target", first,
                    "member", "Echo", "args", new JArray(9)), "同参数数量重载必须显式选择");
                Check((string)Run(session, "invoke", "target", first, "member", "Echo", "args", new JArray(9),
                    "parameterTypes", new JArray("System.Int32"))["value"] == "int:9", "应选择整数重载");
                Check((string)Run(session, "invoke", "target", first, "member", "Echo", "args", new JArray("x"),
                    "parameterTypes", new JArray("System.String"))["value"] == "string:x", "应选择字符串重载");

                var vector = Run(session, "get", "target", first, "member", "Position");
                Check((bool)vector["isValueTypeCopy"], "结构值应明确标为副本");
                Run(session, "set", "target", vector["value"], "member", "x", "value", 9);
                var original = Run(session, "get", "target", first, "member", "Position")["value"];
                Check((float)Run(session, "get", "target", original, "member", "x")["value"] == 1,
                    "修改装箱副本不应自动改变父字段");
                Run(session, "set", "target", first, "member", "Position", "value", vector["value"]);
                var assigned = Run(session, "get", "target", first, "member", "Position")["value"];
                Check((float)Run(session, "get", "target", assigned, "member", "x")["value"] == 9,
                    "显式回写后父字段应改变");

                try
                {
                    Run(session, "invoke", "target", first, "member", "Fail");
                    throw new Exception("Fail 应抛出异常");
                }
                catch (InvalidOperationException exception)
                {
                    Check(exception.Message == "probe-root-cause", "应保留调用异常的真实根因");
                }
                Run(session, "release", "targets", new JArray(second.DeepClone()));
                Expect<KeyNotFoundException>(() => Run(session, "inspect", "target", second), "已释放句柄不得继续使用");

                var found = (JArray)Run(session, "find", "type", typeof(RuntimeProbe).FullName)["items"];
                var runtime = found.Single(item => (string)item["name"] == owner.name);
                Check(runtime["$ref"] != null, "应能定位场景中的 Unity 对象");
                UnityObject.DestroyImmediate(owner);
                Expect<InvalidOperationException>(() => Run(session, "inspect", "target", runtime["$ref"]),
                    "已销毁 Unity 对象的句柄不得继续使用");
                Debug.Log("CLIENTMCP_REFLECTION_CHECKS_OK count=" + _checks);
            }
            finally
            {
                RuntimeProbe.ProbeValue.Shared = previousShared;
                session.Clear();
                if (owner != null)
                    UnityObject.DestroyImmediate(owner);
            }
        }

        public static void BuildPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(arguments, "--clientmcp-output");
            if (index >= 0 && (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("-")))
                throw new ArgumentException("--clientmcp-output 后需要可执行文件路径");
            var root = Path.GetDirectoryName(Application.dataPath);
            var output = Path.GetFullPath(index >= 0 ? arguments[index + 1] :
                Path.Combine(root, "..", ".scratch", "validation-player", "ClientMCP.exe"));
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var previousBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            var previousStripping = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Standalone);
            try
            {
                // 只在本次验收构建临时选择 Mono；通过 Editor API 设置并在结束后恢复。
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
                PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, ManagedStrippingLevel.Disabled);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Scenes/DiagnosticsSandbox.unity" },
                    locationPathName = output,
                    target = BuildTarget.StandaloneWindows64,
                    subtarget = (int)StandaloneBuildSubtarget.Player,
                    options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("运行时验收构建失败：" + report.summary.result);
                Debug.Log("CLIENTMCP_PLAYER_BUILD_OK path=" + output);
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, previousBackend);
                PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Standalone, previousStripping);
            }
        }

        private static JObject Run(ReflectionSession session, string operation, params object[] fields)
        {
            var request = new JObject { ["op"] = operation };
            for (var index = 0; index < fields.Length; index += 2)
                request[(string)fields[index]] = fields[index + 1] is JToken token ? token.DeepClone() :
                    JToken.FromObject(fields[index + 1]);
            return session.Execute(request);
        }

        private static void Check(bool condition, string reason)
        {
            if (!condition)
                throw new InvalidOperationException(reason);
            _checks++;
        }

        private static void Expect<TException>(Action action, string reason) where TException : Exception
        {
            try { action(); }
            catch (TException) { _checks++; return; }
            throw new InvalidOperationException(reason);
        }
    }
}
