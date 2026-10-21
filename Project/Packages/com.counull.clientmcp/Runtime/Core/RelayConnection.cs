using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClientMcp
{
    /// <summary>
    /// 仅负责出站中转连接。调用方在主线程准备注册快照；网络线程不读取任何 Unity API。
    /// 断线取消本代尚未执行的请求，不重发写操作；反射会话及计划仍由 Runtime 生命周期持有。
    /// </summary>
    internal sealed class RelayConnection : IDisposable
    {
        const int MaxMessageBytes = 1024 * 1024;
        const int MaxRequests = 64;
        const int ConnectTimeoutMilliseconds = 10000;
        const int SendTimeoutMilliseconds = 5000;
        readonly Uri endpoint;
        readonly string token;
        readonly JObject hello;
        readonly Func<JObject, CancellationToken, JObject> execute;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly SemaphoreSlim requestSlots = new SemaphoreSlim(MaxRequests, MaxRequests);
        readonly object gate = new object();
        ClientWebSocket activeSocket;
        string state = "connecting";
        string lastConnectedUtc = "";
        bool stopped;

        public RelayConnection(string url, string token, JObject hello, Func<JObject, CancellationToken, JObject> execute)
        {
            endpoint = ValidateEndpoint(url);
            if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
                throw new ArgumentException("A relay Bearer token is required.", nameof(token));
            foreach (char character in token)
                if (char.IsControl(character)) throw new ArgumentException("The relay token contains invalid characters.", nameof(token));
            this.token = token;
            this.hello = (JObject)(hello ?? throw new ArgumentNullException(nameof(hello))).DeepClone();
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            if ((string)this.hello["kind"] != "hello" || string.IsNullOrWhiteSpace((string)this.hello["instanceId"]))
                throw new ArgumentException("A relay instance registration is required.", nameof(hello));
            new Thread(Run) { IsBackground = true, Name = "ClientMCP relay" }.Start();
        }

        internal static Uri ValidateEndpoint(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != "wss" && !(uri.Scheme == "ws" && uri.IsLoopback)) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Relay URL must use wss, or ws on loopback, without credentials, query or fragment.", nameof(url));
            return uri;
        }

        public JObject GetStatus()
        {
            lock (gate) return new JObject
            {
                ["enabled"] = !stopped, ["state"] = state,
                ["instanceId"] = (string)hello["instanceId"], ["lastConnectedUtc"] = lastConnectedUtc
            };
        }

        void Run()
        {
            int retry = 0;
            int[] delays = { 1000, 2000, 5000, 10000 };
            while (!lifetime.IsCancellationRequested)
            {
                using (var socket = new ClientWebSocket())
                using (var generation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                using (var sendGate = new SemaphoreSlim(1, 1))
                {
                    lock (gate)
                    {
                        if (stopped) return;
                        activeSocket = socket;
                        state = "connecting";
                    }
                    try
                    {
                        // 使用平台 TLS 校验；令牌仅通过握手头传递，不进入 URL、状态或异常日志。
                        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
                        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(generation.Token))
                        {
                            connect.CancelAfter(ConnectTimeoutMilliseconds);
                            socket.ConnectAsync(endpoint, connect.Token).GetAwaiter().GetResult();
                        }
                        Send(socket, sendGate, hello, generation.Token);
                        Receive(socket, sendGate, generation);
                        retry = 0;
                    }
                    catch (Exception) { /* 只以固定状态报告断开，不复制可能包含凭据的网络异常。 */ }
                    finally
                    {
                        generation.Cancel();
                        Abort(socket);
                        lock (gate)
                        {
                            if (state == "connected") retry = 0;
                            if (ReferenceEquals(activeSocket, socket)) activeSocket = null;
                            if (!stopped) state = "retrying";
                        }
                    }
                }
                // 只在后台等待，可由 Dispose 立即唤醒；重连不创建新的 Runtime 会话。
                if (lifetime.Token.WaitHandle.WaitOne(delays[Math.Min(retry++, delays.Length - 1)])) return;
            }
        }

        void Receive(ClientWebSocket socket, SemaphoreSlim sendGate, CancellationTokenSource generation)
        {
            var buffer = new byte[8192];
            var inFlight = new HashSet<string>(StringComparer.Ordinal);
            DateTime received = DateTime.UtcNow;
            DateTime ping = received;
            bool welcomed = false;
            using (var message = new MemoryStream())
            {
                while (!generation.IsCancellationRequested)
                {
                    DateTime beforeReceive = DateTime.UtcNow;
                    if (welcomed && (beforeReceive - ping).TotalSeconds >= 10)
                    {
                        Send(socket, sendGate, new JObject { ["kind"] = "ping" }, generation.Token);
                        ping = beforeReceive;
                    }
                    Task<WebSocketReceiveResult> receive = socket.ReceiveAsync(new ArraySegment<byte>(buffer), generation.Token);
                    while (!receive.Wait(500, generation.Token))
                    {
                        DateTime now = DateTime.UtcNow;
                        if ((now - received).TotalSeconds > (welcomed ? 45 : 10)) throw new TimeoutException("Relay response timed out.");
                        if (welcomed && (now - ping).TotalSeconds >= 10)
                        {
                            Send(socket, sendGate, new JObject { ["kind"] = "ping" }, generation.Token);
                            ping = now;
                        }
                    }
                    WebSocketReceiveResult part = receive.GetAwaiter().GetResult();
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > MaxMessageBytes)
                        throw new InvalidDataException("Invalid relay message.");
                    message.Write(buffer, 0, part.Count);
                    if (!part.EndOfMessage) continue;
                    JObject frame = Parse(message.ToArray());
                    message.SetLength(0);
                    received = DateTime.UtcNow;
                    string kind = frame["kind"]?.Type == JTokenType.String ? (string)frame["kind"] : "";
                    if (!welcomed)
                    {
                        if (kind != "welcome") throw new InvalidDataException("Relay registration was not accepted.");
                        welcomed = true;
                        lock (gate)
                        {
                            if (!stopped) { state = "connected"; lastConnectedUtc = received.ToString("O"); }
                        }
                        continue;
                    }
                    if (kind == "pong") continue;
                    string requestId = frame["requestId"]?.Type == JTokenType.String ? (string)frame["requestId"] : "";
                    if (kind != "request" || requestId.Length == 0 || requestId.Length > 128 || !(frame["command"] is JObject command))
                        throw new InvalidDataException("Invalid relay request.");
                    lock (inFlight)
                        if (!inFlight.Add(requestId)) throw new InvalidDataException("Duplicate relay request.");
                    if (!requestSlots.Wait(0))
                    {
                        lock (inFlight) inFlight.Remove(requestId);
                        SendResponse(socket, sendGate, requestId, Error("InvalidOperationException", "The relay request queue is full."), generation.Token);
                        continue;
                    }
                    // 有界工作项等待 Runtime 主线程队列；接收线程继续处理心跳与关闭。
                    CancellationToken cancellation = generation.Token;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            cancellation.ThrowIfCancellationRequested();
                            JObject body;
                            try { body = execute(command, cancellation); }
                            catch (Exception error)
                            {
                                error = error.GetBaseException();
                                body = Error(error.GetType().FullName, "Relay execution failed; query the runtime receipt before retrying.");
                            }
                            SendResponse(socket, sendGate, requestId, body, cancellation);
                        }
                        catch (Exception) { Abort(socket); }
                        finally
                        {
                            lock (inFlight) inFlight.Remove(requestId);
                            requestSlots.Release();
                        }
                    });
                }
            }
        }

        static JObject Parse(byte[] bytes)
        {
            using (var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(bytes)))
                { MaxDepth = 64, DateParseHandling = DateParseHandling.None })
            {
                JObject result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("Only one relay object is accepted.");
                return result;
            }
        }

        static JObject Error(string type, string message) => new JObject
        {
            ["ok"] = false, ["error"] = new JObject { ["type"] = type, ["message"] = message }
        };

        static void SendResponse(ClientWebSocket socket, SemaphoreSlim sendGate, string requestId, JObject body, CancellationToken cancellation) =>
            Send(socket, sendGate, new JObject { ["kind"] = "response", ["requestId"] = requestId, ["body"] = body }, cancellation);

        static void Send(ClientWebSocket socket, SemaphoreSlim sendGate, JObject frame, CancellationToken cancellation)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(frame.ToString(Formatting.None));
            if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("Relay response exceeds one MiB.");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(SendTimeoutMilliseconds);
                sendGate.Wait(timeout.Token);
                try { socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult(); }
                finally { sendGate.Release(); }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                state = "stopped";
                lifetime.Cancel();
                if (activeSocket != null) Abort(activeSocket);
            }
            // 不 Join 网络线程，也不等待已进入宿主的方法；取消会唤醒网络及尚未执行的请求。
        }

        static void Abort(ClientWebSocket socket)
        {
            try { socket.Abort(); }
            catch (ObjectDisposedException) { }
        }
    }
}
