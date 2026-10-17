using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ClientMcp
{
    /// <summary>本机接入选项。默认随机端口和令牌；启用入口由宿主或开发者明确调用。</summary>
    public sealed class ClientMcpOptions
    {
        public int Port;
        public string InfoPath;
        public int RequestTimeoutMilliseconds = 10000;
    }

    /// <summary>
    /// 接收线程只负责网络；Update 在主线程顺序执行反射请求。
    /// 调用顺序：显式 Start → 外部请求入队 → Update 执行 → 返回结果；默认不监听端口。
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    public sealed class ClientMcpRuntime : MonoBehaviour
    {
        const int MaxPendingRequests = 64;
        static ClientMcpRuntime current;
        readonly ConcurrentQueue<PendingRequest> pending = new ConcurrentQueue<PendingRequest>();
        readonly ReflectionSession reflection = new ReflectionSession();
        readonly object lifecycleGate = new object();
        LoopbackHttpServer server;
        string sessionId;
        string infoPath;
        int requestTimeout;
        int pendingCount;
        volatile bool stopped;

        public static bool IsRunning => current != null && !current.stopped;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetRuntime()
        {
            // 关闭 Domain Reload 时也不能把上次 Play 的连接和对象引用带入下一次。
            if (current != null) current.Shutdown();
            current = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void StartFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "--clientmcp") < 0) return;
            var options = new ClientMcpOptions();
            for (int index = 0; index < args.Length - 1; index++)
            {
                if (args[index] == "--clientmcp-info") options.InfoPath = args[index + 1];
                if (args[index] == "--clientmcp-port") options.Port = int.Parse(args[index + 1]);
            }
            Start(options);
        }

        /// <summary>必须在游戏主线程、Play Mode 或 Player 中调用；不依赖 UnityEditor。</summary>
        public static void Start(ClientMcpOptions options = null)
        {
            if (!Application.isPlaying) throw new InvalidOperationException("ClientMCP requires a running instance.");
            if (IsRunning) return;
            options = options ?? new ClientMcpOptions();
            if (options.Port < 0 || options.Port > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
            if (options.RequestTimeoutMilliseconds < 100) throw new ArgumentOutOfRangeException(nameof(options.RequestTimeoutMilliseconds));
            var host = new GameObject("ClientMCP Runtime");
            DontDestroyOnLoad(host);
            var runtime = host.AddComponent<ClientMcpRuntime>();
            current = runtime;
            try { runtime.Initialize(options); }
            catch { Destroy(host); current = null; throw; }
        }

        public static void Stop()
        {
            if (current == null) return;
            current.Shutdown();
            Destroy(current.gameObject);
            current = null;
        }

        void Initialize(ClientMcpOptions options)
        {
            sessionId = Guid.NewGuid().ToString("N");
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
            // 一个本机入口同时供脚本和 MCP 使用；令牌只写入用户选择的本地文件，不打印。
            server = new LoopbackHttpServer(options.Port, token, HandleHttp);
            File.WriteAllText(infoPath, new JObject
            {
                ["endpoint"] = server.Endpoint, ["token"] = token, ["sessionId"] = sessionId, ["processId"] = pid
            }.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
            Debug.Log("CLIENTMCP_READY endpoint=" + server.Endpoint + " info=" + infoPath);
        }

        HttpReply HandleHttp(string method, string path, JObject body)
        {
            if (path == "/mcp") return McpEndpoint.Handle(method, body, EnqueueAndWait);
            if (path == "/health" && method == "GET") return HttpReply.Json(200, Success(EnqueueAndWait(new JObject { ["op"] = "status" })));
            if (path != "/reflect") return HttpReply.Error(404, "Unknown endpoint.");
            if (method != "POST") return HttpReply.Error(405, "Use POST.");
            try { return HttpReply.Json(200, Success(EnqueueAndWait(body))); }
            catch (Exception error) { return HttpReply.Json(200, Failure(error)); }
        }

        JObject EnqueueAndWait(JObject command)
        {
            PendingRequest request;
            // 入队和停止检查共同受保护，防止关闭清队后又进入新的写请求。
            lock (lifecycleGate)
            {
                if (stopped) throw new ObjectDisposedException(nameof(ClientMcpRuntime));
                if (Interlocked.Increment(ref pendingCount) > MaxPendingRequests)
                {
                    Interlocked.Decrement(ref pendingCount);
                    throw new InvalidOperationException("The request queue is full.");
                }
                request = new PendingRequest(command);
                pending.Enqueue(request);
            }
            try
            {
                if (!request.Completion.Task.Wait(requestTimeout))
                {
                    bool cancelled = request.CancelBeforeExecution();
                    throw new TimeoutException(cancelled ? "Timed out before execution; the command was cancelled." :
                        "Timed out after execution started; the command may have taken effect. Read the state before retrying.");
                }
                return request.Completion.Task.GetAwaiter().GetResult();
            }
            catch (AggregateException error) { throw error.GetBaseException(); }
        }

        void Update()
        {
            if (pending.IsEmpty) return;
            var timer = Stopwatch.StartNew();
            int executed = 0;
            // 不暂停游戏，也不遍历业务对象；有请求时才处理，每帧最多八项和约两毫秒。
            // 单个被调用方法不能被此预算中断，因此调用方应选择有限、短时的操作。
            while (!stopped && executed < 8 && timer.Elapsed.TotalMilliseconds < 2 && pending.TryDequeue(out var request))
            {
                Interlocked.Decrement(ref pendingCount);
                if (!request.TryBegin()) continue;
                executed++;
                try { request.Completion.TrySetResult((string)request.Command["op"] == "status" ? Status() : reflection.Execute(request.Command)); }
                catch (Exception error) { request.Completion.TrySetException(error); }
            }
        }

        JObject Status() => new JObject
        {
            ["sessionId"] = sessionId, ["frameCount"] = Time.frameCount,
            ["unityVersion"] = Application.unityVersion, ["isEditor"] = Application.isEditor,
            ["isPlaying"] = Application.isPlaying
        };

        internal static JObject Success(JObject result) => new JObject { ["ok"] = true, ["result"] = result };
        internal static JObject Failure(Exception error)
        {
            error = error.GetBaseException();
            return new JObject { ["ok"] = false, ["error"] = new JObject { ["type"] = error.GetType().FullName, ["message"] = error.Message } };
        }

        void OnDestroy() { Shutdown(); if (current == this) current = null; }
        void OnApplicationQuit() => Shutdown();

        void Shutdown()
        {
            lock (lifecycleGate)
            {
                if (stopped) return;
                stopped = true;
            }
            server?.Dispose();
            while (pending.TryDequeue(out var request))
            {
                Interlocked.Decrement(ref pendingCount);
                request.Completion.TrySetException(new ObjectDisposedException(nameof(ClientMcpRuntime)));
            }
            reflection.Clear();
            // 只删除属于本会话的凭据文件，避免删掉另一实例后来写入的文件。
            try
            {
                if (File.Exists(infoPath) && (string)JObject.Parse(File.ReadAllText(infoPath))["sessionId"] == sessionId) File.Delete(infoPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }

        sealed class PendingRequest
        {
            int state;
            public readonly JObject Command;
            public readonly TaskCompletionSource<JObject> Completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            public PendingRequest(JObject command) { Command = command; }
            public bool TryBegin() => Interlocked.CompareExchange(ref state, 1, 0) == 0;
            public bool CancelBeforeExecution() => Interlocked.CompareExchange(ref state, 2, 0) == 0;
        }
    }
}
