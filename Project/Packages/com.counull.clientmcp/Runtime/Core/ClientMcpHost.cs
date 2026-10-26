using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClientMcp
{
    /// <summary>通用宿主。创建线程定期 Pump；网络线程只编译、排队和等待回执。</summary>
    public sealed class ClientMcpHost : IDisposable
    {
        const int MaxPendingRequests = 64;
        const int MaxRecentRequests = 16;
        readonly ConcurrentQueue<PendingRequest> pending = new ConcurrentQueue<PendingRequest>();
        readonly ReflectionSession reflection;
        readonly Func<JObject> hostStatus;
        readonly Action<string, bool> log;
        readonly int ownerThread;
        ScriptSession scripts;
        readonly object lifecycleGate = new object();
        readonly object connectionGate = new object();
        readonly Queue<JObject> recentRequests = new Queue<JObject>();
        readonly HashSet<string> relayTokens = new HashSet<string>(StringComparer.Ordinal);
        LoopbackHttpServer server;
        RelayConnection relay;
        ReflectionPlanSession plans;
        string sessionId;
        string infoPath;
        string accessToken;
        int requestTimeout;
        int pendingCount;
        int activeRequests;
        long mcpRequests;
        long reflectRequests;
        long healthRequests;
        string lastRequestUtc = "";
        volatile bool stopped;

        public bool IsRunning => !stopped && server != null;
        public JObject GetConnectionStatus() => ConnectionStatus();
        public ClientMcpHost(IReflectionHost objects, Func<JObject> hostStatus = null, Action<string, bool> log = null)
        {
            reflection = new ReflectionSession(objects ?? throw new ArgumentNullException(nameof(objects)));
            this.hostStatus = hostStatus ?? (() => new JObject { ["hostKind"] = "dotnet" });
            this.log = log ?? ((message, warning) => { });
            ownerThread = Thread.CurrentThread.ManagedThreadId;
        }
        public void Start(ClientMcpOptions options = null)
        {
            AssertOwner();
            if (IsRunning) return;
            if (stopped) throw new ObjectDisposedException(nameof(ClientMcpHost));
            options = options ?? new ClientMcpOptions();
            if (options.Port < 0 || options.Port > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
            if (options.RequestTimeoutMilliseconds < 100) throw new ArgumentOutOfRangeException(nameof(options.RequestTimeoutMilliseconds));
            try { Initialize(options); } catch { Shutdown(); throw; }
        }
        public void ConnectRelay(string url, string token, string instanceName = null)
        {
            AssertOwner();
            if (!IsRunning) throw new InvalidOperationException("Start ClientMCP before connecting a relay.");
            ConnectRelayCore(url, token, instanceName);
        }
        public void DisconnectRelay()
        {
            AssertOwner();
            RelayConnection previous;
            lock (connectionGate) { previous = relay; relay = null; }
            previous?.Dispose();
        }
        void AssertOwner()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("ClientMCP 必须由创建它的宿主线程启动、Pump 和释放。");
        }

        void ConnectRelayCore(string url, string token, string instanceName)
        {
            // 注册数据先在主线程取值；后台运输层只处理这个不可变快照。
            var hello = new JObject
            {
                ["kind"] = "hello", ["instanceId"] = sessionId, ["sessionId"] = sessionId,
                ["name"] = string.IsNullOrWhiteSpace(instanceName) ? ((string)hostStatus()["name"] ?? "ClientMCP") : instanceName.Trim(),
                ["unityVersion"] = hostStatus()["unityVersion"] ?? "n/a", ["isEditor"] = hostStatus()["isEditor"] ?? false,
                ["processId"] = Process.GetCurrentProcess().Id
            };
            if (((string)hello["name"]).Length > 200) throw new ArgumentException("Relay instance name exceeds 200 characters.", nameof(instanceName));
            foreach (char character in (string)hello["name"])
                if (char.IsControl(character)) throw new ArgumentException("Relay instance name contains control characters.", nameof(instanceName));
            // 先构造并校验新连接，配置无效时保留旧连接。令牌不写入连接文件或状态。
            RelayConnection previous;
            lock (connectionGate)
            {
                var next = new RelayConnection(url, token, hello, (command, cancellation) => HandleHttp("POST", "/reflect", command, cancellation).Body);
                relayTokens.Add(token);
                previous = relay;
                relay = next;
            }
            previous?.Dispose();
        }

        void Initialize(ClientMcpOptions options)
        {
            sessionId = Guid.NewGuid().ToString("N");
            plans = new ReflectionPlanSession(reflection, sessionId);
            scripts = new ScriptSession(reflection, sessionId, options.ScriptCompiler);
            requestTimeout = options.RequestTimeoutMilliseconds;
            int pid = Process.GetCurrentProcess().Id;
            infoPath = Path.GetFullPath(options.InfoPath ?? Path.Combine(Path.GetTempPath(), "clientmcp-" + pid + ".json"));
            Directory.CreateDirectory(Path.GetDirectoryName(infoPath));
            string token;
            using (var random = RandomNumberGenerator.Create())
            {
                var bytes = new byte[32]; random.GetBytes(bytes);
                token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
            accessToken = token;
            // 一个本机入口同时供脚本和 MCP 使用；令牌只写入用户选择的本地文件，不打印。
            server = new LoopbackHttpServer(options.Port, token, HandleHttp);
            File.WriteAllText(infoPath, new JObject
            {
                ["endpoint"] = server.Endpoint, ["token"] = token, ["sessionId"] = sessionId, ["processId"] = pid
            }.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
            log("CLIENTMCP_READY endpoint=" + server.Endpoint + " info=" + infoPath, false);
            if (!string.IsNullOrWhiteSpace(options.RelayUrl)) ConnectRelayCore(options.RelayUrl, options.RelayToken, options.InstanceName);
        }

        HttpReply HandleHttp(string method, string path, JObject body) => HandleHttp(method, path, body, CancellationToken.None);

        HttpReply HandleHttp(string method, string path, JObject body, CancellationToken cancellation)
        {
            // 仅通过 HTTP 认证且报文解析完成的请求才会进入这里；Editor 刷新窗口不会增加计数。
            bool tracked = RecordRequestStarted(path);
            bool success = false;
            var metrics = new RequestMetrics();
            HttpReply reply = null;
            try
            {
                reply = DispatchHttp(method, path, body, cancellation, metrics);
                success = reply.Status < 400 && reply.Body?["error"] == null &&
                    (bool?)reply.Body?["ok"] != false && (bool?)reply.Body?["result"]?["isError"] != true &&
                    !IsFailedPlanReceipt(reply.Body?["result"] as JObject);
                return reply;
            }
            finally
            {
                if (tracked) RecordRequestCompleted(path, DescribeOperation(path, body), RequestDescription(path, body),
                    success, metrics, reply?.SerializedBody.Length);
            }
        }

        HttpReply DispatchHttp(string method, string path, JObject body, CancellationToken cancellation, RequestMetrics metrics)
        {
            if (path == "/mcp") return McpEndpoint.Handle(method, body, command => EnqueueAndWait(command, path, cancellation, metrics));
            if (path == "/health" && method == "GET") return HttpReply.Json(200, Success(EnqueueAndWait(new JObject { ["op"] = "status" }, path, cancellation, metrics)));
            if (path != "/reflect") return HttpReply.Error(404, "Unknown endpoint.");
            if (method != "POST") return HttpReply.Error(405, "Use POST.");
            try { return HttpReply.Json(200, Success(EnqueueAndWait(body, path, cancellation, metrics))); }
            catch (Exception error) { return HttpReply.Json(200, Failure(error)); }
        }

        bool RecordRequestStarted(string path)
        {
            lock (connectionGate)
            {
                if (stopped) return false;
                activeRequests++;
                if (path == "/mcp") mcpRequests++;
                else if (path == "/reflect") reflectRequests++;
                else if (path == "/health") healthRequests++;
                lastRequestUtc = DateTime.UtcNow.ToString("O");
                return true;
            }
        }

        void RecordRequestCompleted(string path, string operation, string description, bool success,
            RequestMetrics metrics, int? responseBytes)
        {
            var entry = metrics.Snapshot(responseBytes);
            entry["timeUtc"] = DateTime.UtcNow.ToString("O");
            entry["path"] = path == "/mcp" || path == "/reflect" || path == "/health" ? path : "(unknown)";
            entry["operation"] = operation;
            entry["description"] = description;
            entry["success"] = success;
            lock (connectionGate)
            {
                // 停止后旧接收线程可能仍在退出，不能把旧会话活动写回已经清空的状态。
                if (stopped) return;
                activeRequests--;
                if (recentRequests.Count == MaxRecentRequests) recentRequests.Dequeue();
                recentRequests.Enqueue(entry);
            }
        }

        public static JObject EmptyConnectionStatus() => new JObject
        {
            ["isRunning"] = false, ["endpoint"] = "", ["infoPath"] = "", ["activeRequests"] = 0,
            ["pendingRequests"] = 0, ["mcpRequests"] = 0L, ["reflectRequests"] = 0L,
            ["healthRequests"] = 0L, ["lastRequestUtc"] = "", ["recentRequests"] = new JArray(),
            ["relay"] = new JObject { ["enabled"] = false, ["state"] = "disabled", ["instanceId"] = "", ["lastConnectedUtc"] = "" }
        };

        JObject ConnectionStatus()
        {
            lock (connectionGate)
            {
                JObject snapshot = EmptyConnectionStatus();
                if (stopped) return snapshot;
                snapshot["isRunning"] = server != null;
                snapshot["endpoint"] = server?.Endpoint ?? "";
                snapshot["infoPath"] = infoPath ?? "";
                snapshot["activeRequests"] = activeRequests;
                snapshot["pendingRequests"] = Volatile.Read(ref pendingCount);
                snapshot["mcpRequests"] = mcpRequests;
                snapshot["reflectRequests"] = reflectRequests;
                snapshot["healthRequests"] = healthRequests;
                snapshot["lastRequestUtc"] = lastRequestUtc;
                snapshot["relay"] = relay?.GetStatus() ?? new JObject
                {
                    ["enabled"] = false, ["state"] = "disabled", ["instanceId"] = sessionId ?? "", ["lastConnectedUtc"] = ""
                };
                var entries = (JArray)snapshot["recentRequests"];
                foreach (JObject request in recentRequests) entries.Add(request.DeepClone());
                return snapshot;
            }
        }

        static string DescribeOperation(string path, JObject body)
        {
            if (path == "/health") return "health";
            if (path == "/reflect") return DescribeReflection(body);
            if (path != "/mcp") return "unknown endpoint";
            string operation = body?["method"]?.Type == JTokenType.String ? (string)body["method"] : "";
            switch (operation)
            {
                case "initialize": case "ping": case "tools/list":
                case "notifications/initialized": case "notifications/cancelled": return operation;
                case "tools/call":
                    var parameters = body["params"] as JObject;
                    string tool = parameters?["name"]?.Type == JTokenType.String ? (string)parameters["name"] : "";
                    if (tool == "clientmcp_status") return "tools/call clientmcp_status";
                    if ((tool == "clientmcp_reflect" || tool == "clientmcp_script")) return "tools/call clientmcp_reflect " + DescribeReflection(parameters["arguments"] as JObject);
                    return "tools/call unknown tool";
                default: return "unknown MCP method";
            }
        }

        static string DescribeReflection(JObject command)
        {
            string operation = command?["op"]?.Type == JTokenType.String ? (string)command["op"] : "";
            switch (operation)
            {
                case "types": case "find": case "inspect": case "get": case "set":
                case "create": case "invoke": case "release": case "status": case "script": break;
                case "plan":
                    string action = command["action"]?.Type == JTokenType.String ? (string)command["action"] : "";
                    switch (action)
                    {
                        case "prepare": case "execute": case "run": case "result": case "list": case "release": return "plan " + action;
                        default: return "plan unknown action";
                    }
                default: return "unknown reflection operation";
            }
            // 摘要只保留固定的协议操作名；类型、成员和参数均来自请求，不复制到活动记录。
            return operation;
        }

        string RequestDescription(string path, JObject body)
        {
            if (path == "/health") return "检查运行实例状态";
            if (path == "/reflect") return DisplayDescription(body);
            if (path != "/mcp") return "拒绝未知接口请求";
            string operation = body?["method"]?.Type == JTokenType.String ? (string)body["method"] : "";
            switch (operation)
            {
                case "initialize": return "协商 MCP 连接协议";
                case "ping": return "检查 MCP 接口响应";
                case "tools/list": return "读取 MCP 工具列表";
                case "notifications/initialized": return "完成 MCP 初始化";
                case "notifications/cancelled": return "请求取消 MCP 操作";
                case "tools/call":
                    var parameters = body["params"] as JObject;
                    string tool = parameters?["name"]?.Type == JTokenType.String ? (string)parameters["name"] : "";
                    if (tool == "clientmcp_status") return "读取运行实例状态";
                    return (tool == "clientmcp_reflect" || tool == "clientmcp_script") ? DisplayDescription(parameters["arguments"] as JObject) : "拒绝未知 MCP 工具";
                default: return "拒绝未知 MCP 方法";
            }
        }

        string DisplayDescription(JObject command)
        {
            try { return OperationDescription(command); }
            catch (ArgumentException) { return "拒绝缺失或无效的操作说明"; }
        }

        string OperationDescription(JObject command)
        {
            if (command?["op"]?.Type == JTokenType.String && (string)command["op"] == "status") return "读取运行实例状态";
            if (command?["description"]?.Type != JTokenType.String)
                throw new ArgumentException("description 必须提供非空的操作说明字符串。");
            string description = (string)command["description"];
            foreach (char character in description)
                if (char.IsControl(character) || character == '\u2028' || character == '\u2029')
                    throw new ArgumentException("description 不能包含控制或换行字符。");
            description = description.Trim();
            if (description.Length == 0 || description.Length > 200)
                throw new ArgumentException("description 去除首尾空白后必须为 1 至 200 个字符。");
            // 说明是客户端主动提供的文本，不从参数生成；会话令牌和 Console 富文本标记不直接显示。
            string token = accessToken;
            if (!string.IsNullOrEmpty(token)) description = description.Replace(token, "[REDACTED]");
            lock (connectionGate)
                foreach (string secret in relayTokens) description = description.Replace(secret, "[REDACTED]");
            return description.Replace("<", "＜").Replace(">", "＞");
        }

        JObject EnqueueAndWait(JObject command, string path, CancellationToken cancellation, RequestMetrics metrics)
        {
            // MCP 和原始 HTTP 共用此校验，不能仅依赖工具 schema；无效说明在执行前即被拒绝。
            string description = OperationDescription(command);
            // 编译发生在请求线程；生成的委托只在 Pump 中执行，不读取宿主对象。
            var compiled = scripts.CompileIfNeeded(command);
            PendingRequest request;
            // 入队和停止检查共同受保护，防止关闭清队后又进入新的写请求。
            lock (lifecycleGate)
            {
                if (stopped) throw new ObjectDisposedException(nameof(ClientMcpHost));
                cancellation.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref pendingCount) > MaxPendingRequests)
                {
                    Interlocked.Decrement(ref pendingCount);
                    throw new InvalidOperationException("The request queue is full.");
                }
                request = new PendingRequest((JObject)command.DeepClone(), path, description, cancellation, compiled, metrics);
                metrics.Queued();
                pending.Enqueue(request);
            }
            try
            {
                if (!request.Completion.Task.Wait(requestTimeout, cancellation))
                {
                    bool cancelled = request.CancelBeforeExecution();
                    throw new TimeoutException(cancelled ? "Timed out before execution; the command was cancelled." :
                        "Timed out after execution started; the command may have taken effect. Read the state before retrying.");
                }
                return request.Completion.Task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                bool cancelled = request.CancelBeforeExecution();
                throw new OperationCanceledException(cancelled ? "Relay disconnected before execution; the command was cancelled." :
                    "Relay disconnected after execution started; the command may have taken effect. Query the operation receipt before retrying.", cancellation);
            }
            catch (AggregateException error) { throw error.GetBaseException(); }
            finally { request.Cancellation.Dispose(); }
        }

        public void Pump()
        {
            AssertOwner();
            if (pending.IsEmpty) return;
            var timer = Stopwatch.StartNew();
            int executed = 0;
            // 不暂停游戏，也不遍历业务对象；有请求时才处理，每帧最多八项和约两毫秒。
            // 单个被调用方法不能被此预算中断，因此调用方应选择有限、短时的操作。
            while (!stopped && executed < 8 && timer.Elapsed.TotalMilliseconds < 2 && pending.TryDequeue(out var request))
            {
                Interlocked.Decrement(ref pendingCount);
                if (!request.TryBegin()) continue;
                request.Metrics?.BeginExecution();
                executed++;
                bool isStatus = request.Command["op"]?.Type == JTokenType.String && (string)request.Command["op"] == "status";
                string logPrefix = "[ClientMCP][" + request.Path + "] ";
                try
                {
                    // 统一在主线程记录；先展示意图，再执行反射，结果不包含参数或返回值。
                    if (!isStatus) log(logPrefix + "开始：" + request.Description, false);
                    JObject result;
                    if (isStatus) result = Status();
                    else if (request.Command["op"]?.Type == JTokenType.String && (string)request.Command["op"] == "plan")
                    {
                        // 计划会保存说明用于复用与回执；只覆盖其说明，普通反射和计划参数的原值都不改写。
                        var command = (JObject)request.Command.DeepClone();
                        command["description"] = request.Description;
                        result = plans.Execute(command);
                    }
                    else if ((string)request.Command["op"] == "script") result = scripts.Execute(request.Command, request.Compiled);
                    else result = reflection.Execute(request.Command);
                    if (!isStatus)
                    {
                        string action = request.Command["action"]?.Type == JTokenType.String ? (string)request.Command["action"] : "";
                        bool originalReceipt = request.Command["op"]?.Type == JTokenType.String && ((string)request.Command["op"] == "plan" || (string)request.Command["op"] == "script") &&
                            (action == "execute" || action == "run" || action == "result") && result?["cacheHit"]?.Type == JTokenType.Boolean && (bool)result["cacheHit"];
                        string receiptNotice = originalReceipt ? "（返回原回执，未重复执行）" : "";
                        if (IsFailedPlanReceipt(result)) log(logPrefix + "失败" + receiptNotice + "：" + request.Description, true);
                        else log(logPrefix + "成功" + receiptNotice + "：" + request.Description, false);
                    }
                    request.Metrics?.CompleteExecution();
                    request.Completion.TrySetResult(result);
                }
                catch (Exception error)
                {
                    if (!isStatus) log(logPrefix + "失败：" + request.Description, true);
                    request.Metrics?.CompleteExecution();
                    request.Completion.TrySetException(error);
                }
            }
        }

        JObject Status()
        {
            var status = (JObject)hostStatus().DeepClone();
            status["sessionId"] = sessionId;
            status["connection"] = ConnectionStatus();
            status["scriptsEnabled"] = scripts.Enabled;
            return status;
        }

        internal static JObject Success(JObject result) => new JObject { ["ok"] = true, ["result"] = result };
        // 已完成的失败回执仍要完整返回，不能抛异常丢掉失败前的步骤结果或重复请求信息。
        internal static bool IsFailedPlanReceipt(JObject result) => result?["state"]?.Type == JTokenType.String && (string)result["state"] == "failed";
        internal static JObject Failure(Exception error)
        {
            error = error.GetBaseException();
            return new JObject { ["ok"] = false, ["error"] = new JObject { ["type"] = error.GetType().FullName, ["message"] = error.Message } };
        }

        public void Dispose() { AssertOwner(); Shutdown(); }

        void Shutdown()
        {
            lock (lifecycleGate)
            {
                if (stopped) return;
                stopped = true;
            }
            lock (connectionGate)
            {
                activeRequests = 0;
                mcpRequests = reflectRequests = healthRequests = 0;
                lastRequestUtc = "";
                recentRequests.Clear();
            }
            relay?.Dispose();
            relay = null;
            server?.Dispose();
            while (pending.TryDequeue(out var request))
            {
                Interlocked.Decrement(ref pendingCount);
                request.Completion.TrySetException(new ObjectDisposedException(nameof(ClientMcpHost)));
            }
            plans?.Clear();
            scripts?.Clear();
            reflection.Clear();
            accessToken = null;
            lock (connectionGate) relayTokens.Clear();
            // 只删除属于本会话的凭据文件，避免删掉另一实例后来写入的文件。
            try
            {
                if (File.Exists(infoPath) && (string)JObject.Parse(File.ReadAllText(infoPath))["sessionId"] == sessionId) File.Delete(infoPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }

        // 只记录单次请求的时间与大小；跨线程发布时间戳，回包后保存固定快照，不暴露参数或结果。
        sealed class RequestMetrics
        {
            readonly long received = Stopwatch.GetTimestamp();
            long queued;
            long started;
            long completed;
            public void Queued() => Interlocked.Exchange(ref queued, Stopwatch.GetTimestamp());
            public void BeginExecution() => Interlocked.Exchange(ref started, Stopwatch.GetTimestamp());
            public void CompleteExecution() => Interlocked.Exchange(ref completed, Stopwatch.GetTimestamp());
            public JObject Snapshot(int? responseBytes)
            {
                var now = Stopwatch.GetTimestamp();
                var queuedAt = Interlocked.Read(ref queued);
                var startedAt = Interlocked.Read(ref started);
                var completedAt = Interlocked.Read(ref completed);
                return new JObject
                {
                    ["durationMs"] = Milliseconds(received, now),
                    ["queueMs"] = queuedAt == 0 ? JValue.CreateNull() : new JValue(Milliseconds(queuedAt,
                        startedAt == 0 ? now : Math.Min(startedAt, now))),
                    // 未开始或响应时仍在执行不能伪装为执行耗时为零；真实结果仍以操作回执为准。
                    ["executionMs"] = startedAt == 0 || completedAt == 0 || completedAt > now
                        ? JValue.CreateNull() : new JValue(Milliseconds(startedAt, completedAt)),
                    ["responseBytes"] = responseBytes.HasValue ? new JValue(responseBytes.Value) : JValue.CreateNull()
                };
            }
            static double Milliseconds(long begin, long end) => Math.Max(0, end - begin) * 1000.0 / Stopwatch.Frequency;
        }

        sealed class PendingRequest
        {
            int state;
            public readonly JObject Command;
            public readonly Func<ScriptGlobals, object> Compiled;
            public readonly string Path;
            public readonly string Description;
            public readonly RequestMetrics Metrics;
            readonly CancellationToken cancellation;
            public readonly CancellationTokenRegistration Cancellation;
            public readonly TaskCompletionSource<JObject> Completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            public PendingRequest(JObject command, string path, string description, CancellationToken cancellation,
                Func<ScriptGlobals, object> compiled = null, RequestMetrics metrics = null)
            {
                Metrics = metrics;
                Compiled = compiled;
                Command = command;
                Path = path;
                Description = description;
                this.cancellation = cancellation;
                // 与 TryBegin 竞争同一个状态，断线先赢时本帧也不能开始执行排队操作。
                Cancellation = cancellation.Register(() => CancelBeforeExecution());
            }
            public bool TryBegin()
            {
                if (cancellation.IsCancellationRequested) { CancelBeforeExecution(); return false; }
                return Interlocked.CompareExchange(ref state, 1, 0) == 0;
            }
            public bool CancelBeforeExecution() => Interlocked.CompareExchange(ref state, 2, 0) != 1;
        }
    }
}
