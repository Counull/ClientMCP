using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClientMcp.Server;

/// <summary>单连接只保留一个接收循环，所有发送串行；心跳不进入 Unity 主线程执行队列。</summary>
internal sealed class RelayClient(WebSocket socket, RelayOptions options, RelayRegistry registry)
{
    readonly SemaphoreSlim sendGate = new(1, 1);
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    JObject identity;
    long lastPing = Stopwatch.GetTimestamp();
    public string InstanceId { get; private set; }
    public bool Ready;
    public int PendingCount;

    public JObject Describe()
    {
        var result = (JObject)identity.DeepClone();
        result.Remove("kind");
        result["connectedAtUtc"] = ConnectedAtUtc;
        return result;
    }

    public string ConnectedAtUtc { get; private set; }

    public async Task RunAsync(CancellationToken stopping)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        Task heartbeat = Task.CompletedTask;
        try
        {
            using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                helloTimeout.CancelAfter(options.HelloTimeoutMilliseconds);
                identity = await ReceiveAsync(helloTimeout.Token);
            }
            ValidateHello(identity);
            InstanceId = (string)identity["instanceId"];
            ConnectedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            if (!registry.Register(this)) throw new InvalidDataException("This runtime instance is already connected.");
            await SendAsync(new JObject { ["kind"] = "welcome" }, lifetime.Token);
            // welcome 完成后才允许发现/转发，防止并发 MCP 请求抢先成为客户端收到的第一帧。
            registry.MarkReady(this);
            heartbeat = WatchHeartbeatAsync(lifetime);
            while (!lifetime.IsCancellationRequested)
            {
                var message = await ReceiveAsync(lifetime.Token);
                if (message == null) break;
                switch ((string)message["kind"])
                {
                    case "ping":
                        Volatile.Write(ref lastPing, Stopwatch.GetTimestamp());
                        await SendAsync(new JObject { ["kind"] = "pong" }, lifetime.Token);
                        break;
                    case "response":
                        string requestId = ReadText(message, "requestId", 128);
                        var body = message["body"] as JObject;
                        if (body == null || body["ok"]?.Type != JTokenType.Boolean ||
                            ((bool)body["ok"] ? body["result"] is not JObject : body["error"] is not JObject))
                            throw new InvalidDataException("Invalid response body.");
                        registry.Complete(this, requestId, body);
                        break;
                    default: throw new InvalidDataException("Unknown client message kind.");
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is IOException || error is InvalidDataException || error is JsonException || error is DecoderFallbackException || error is ArgumentException) { }
        finally
        {
            lifetime.Cancel();
            registry.Disconnect(this);
            try { await heartbeat; } catch (OperationCanceledException) { }
            // Abort 释放发送或接收中的 I/O；不等待恶意或失联客户端完成关闭握手。
            socket.Abort();
            socket.Dispose();
        }
    }

    async Task WatchHeartbeatAsync(CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(Math.Min(1000, options.HeartbeatTimeoutMilliseconds), lifetime.Token);
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref lastPing)).TotalMilliseconds < options.HeartbeatTimeoutMilliseconds) continue;
                lifetime.Cancel();
                socket.Abort();
                break;
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task SendAsync(JObject message, CancellationToken cancellation)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
        if (bytes.Length > RelayOptions.MaxMessageBytes) throw new InvalidDataException("Relay message exceeds 1 MiB.");
        await sendGate.WaitAsync(cancellation);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellation); }
        finally { sendGate.Release(); }
    }

    async Task<JObject> ReceiveAsync(CancellationToken cancellation)
    {
        var buffer = new byte[8192];
        using var body = new MemoryStream();
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
            if (received.MessageType == WebSocketMessageType.Close) return null;
            if (received.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Only JSON text messages are supported.");
            if (body.Length + received.Count > RelayOptions.MaxMessageBytes) throw new InvalidDataException("Relay message exceeds 1 MiB.");
            body.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        return RelayApplication.ParseJson(StrictUtf8.GetString(body.GetBuffer(), 0, checked((int)body.Length)));
    }

    static void ValidateHello(JObject hello)
    {
        if (hello == null || (string)hello["kind"] != "hello") throw new InvalidDataException("First message must be hello.");
        string instanceId = ReadText(hello, "instanceId", 128);
        string sessionId = ReadText(hello, "sessionId", 128);
        if (instanceId != sessionId) throw new InvalidDataException("instanceId must equal the Runtime sessionId.");
        ReadText(hello, "name", 200);
        ReadText(hello, "unityVersion", 100);
        if (hello["isEditor"]?.Type != JTokenType.Boolean || hello["processId"]?.Type != JTokenType.Integer ||
            !int.TryParse(hello["processId"].ToString(), out int processId) || processId <= 0)
            throw new InvalidDataException("Hello requires isEditor and processId.");
        // 实例列表只发布协议定义的信息，不回显客户端附带的任意字段。
        foreach (var property in hello.Properties().Where(property => !new[] { "kind", "instanceId", "sessionId", "name", "unityVersion", "isEditor", "processId" }.Contains(property.Name)).ToArray())
            property.Remove();
    }

    static string ReadText(JObject value, string field, int maximum)
    {
        if (value[field]?.Type != JTokenType.String) throw new InvalidDataException("Missing text: " + field);
        string text = (string)value[field];
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl)) throw new InvalidDataException("Invalid text: " + field);
        return text;
    }
}
