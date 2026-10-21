using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using ClientMcp;
using ClientMcp.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// 验证真实 UPM 出站传输与 Kestrel 路由；执行委托使用独立值，不能替代 Unity 反射验收。
const string DeveloperToken = "developer-test-token-000000000000000000000000";
const string ClientToken = "runtime-test-token-00000000000000000000000000";
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
string baseUrl = "http://127.0.0.1:" + port;
string socketUrl = "ws://127.0.0.1:" + port + "/client";
var app = RelayApplication.Build(new[] { "--Logging:LogLevel:Default=Warning" }, new RelayOptions
{
    Url = baseUrl, DeveloperToken = DeveloperToken, ClientToken = ClientToken,
    RequestTimeoutMilliseconds = 500, HelloTimeoutMilliseconds = 1000
});
await app.StartAsync();
using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DeveloperToken);
int assertions = 0;
int serial = 0;
var executionCounts = new ConcurrentDictionary<string, int>();
RelayConnection first = null;
RelayConnection second = null;
try
{
    var unauthorized = await SendRaw("{}", token: "wrong");
    Check(unauthorized.Status == 401, "developer authentication");
    Check((await SendRaw("{}", token: ClientToken)).Status == 401, "client token cannot call MCP");
    Check((await SendRaw("{}", origin: "https://untrusted.example")).Status == 403, "browser Origin rejection");
    Check((await SendRaw("{}", host: "untrusted.example")).Status == 400, "Host rejection");
    Check((await SendRaw("{\"x\":1,\"x\":2}")).Status == 400, "duplicate property rejection");
    Check((await SendRaw("{\"x\":" + new string('[', 70) + "1" + new string(']', 70) + "}")).Status == 400, "JSON depth limit");
    Check((await SendRaw(new string(' ', RelayOptions.MaxMessageBytes + 1))).Status == 413, "HTTP body limit");
    Check((await SendRaw("{\"jsonrpc\":{},\"method\":\"ping\",\"id\":1}")).Status == 400, "malformed JSON-RPC rejected without 500");
    var initialize = await Rpc("initialize", new JObject { ["protocolVersion"] = "2025-11-25" });
    Check((string)initialize["result"]?["serverInfo"]?["name"] == "ClientMCP Relay", "shared MCP initialization");
    var tools = (JArray)(await Rpc("tools/list"))["result"]["tools"];
    Check(tools.Count == 4, "shared tools plus instance discovery");
    Check(tools.Take(2).All(tool => ((JArray)tool["inputSchema"]["required"]).Any(value => (string)value == "instanceId")), "explicit instance schema");
    var initialized = await SendRaw("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
    Check(initialized.Status == 202 && initialized.Body.Length == 0, "notification has no JSON-RPC response");

    first = Connect("transport-a");
    second = Connect("transport-b");
    await WaitFor(async () => (int)(await Instances())["count"] == 2, "two real UPM outbound clients registered");
    var a = await Call("clientmcp_status", new JObject { ["instanceId"] = "transport-a" });
    var b = await Call("clientmcp_status", new JObject { ["instanceId"] = "transport-b" });
    Check((string)a["result"]["structuredContent"]["instanceId"] == "transport-a" &&
          (string)b["result"]["structuredContent"]["instanceId"] == "transport-b", "explicit routing isolates instances");
    Check((int)(await Call("clientmcp_status", new JObject()))["error"]["code"] == -32602, "missing instance is rejected");
    var offline = await Call("clientmcp_status", new JObject { ["instanceId"] = "missing" });
    Check((bool)offline["result"]["isError"] && (string)offline["result"]["structuredContent"]["error"]["type"] == "InstanceOffline", "offline result is a tool error");
    var scriptReply = await Call("clientmcp_script", new JObject { ["instanceId"] = "transport-a", ["action"] = "list", ["description"] = "验证脚本路由" });
    Check((string)scriptReply["result"]["structuredContent"]["operation"] == "script", "script tool forwards the script operation");
    var error = await Reflect("transport-a", "error");
    Check((bool)error["result"]["isError"] && (string)error["result"]["structuredContent"]["error"]["type"] == "TestFailure", "runtime failure is preserved as tool error");

    var concurrent = await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Reflect(index % 2 == 0 ? "transport-a" : "transport-b", "echo", index.ToString())));
    Check(concurrent.Select((value, index) => (string)value["result"]["structuredContent"]["value"] == index.ToString()).All(value => value), "concurrent request IDs route each reply");
    var timeout = await Reflect("transport-a", "slow", "timeout-1");
    Check((bool)timeout["result"]["isError"] && (string)timeout["result"]["structuredContent"]["error"]["type"] == "RelayTimeout", "timeout reports ambiguous execution");
    await Task.Delay(700);
    Check(executionCounts.GetValueOrDefault("timeout-1") == 1, "timeout never replays request");
    var afterTimeout = await Reflect("transport-a", "echo", "after-timeout");
    Check((string)afterTimeout["result"]["structuredContent"]["value"] == "after-timeout", "late reply cannot contaminate later request");

    var inFlight = Reflect("transport-b", "slow", "disconnect-1");
    await WaitFor(() => Task.FromResult(executionCounts.ContainsKey("disconnect-1")), "in-flight call reached client");
    second.Dispose(); second = null;
    var disconnected = await inFlight;
    Check((bool)disconnected["result"]["isError"] && (string)disconnected["result"]["structuredContent"]["error"]["type"] == "RelayDisconnected", "disconnect cleans pending calls");
    await WaitFor(async () => (int)(await Instances())["count"] == 1, "disconnected instance removed");
    second = Connect("transport-b");
    await WaitFor(async () => (int)(await Instances())["count"] == 2, "same runtime session can reconnect");

    await RejectSocket(async socket => await socket.SendAsync(Encoding.UTF8.GetBytes("{}"), WebSocketMessageType.Binary, true, CancellationToken.None), "binary frame rejected");
    await RejectSocket(async socket => await socket.SendAsync(Encoding.UTF8.GetBytes("{\"kind\":\"hello\",\"kind\":\"hello\"}"), WebSocketMessageType.Text, true, CancellationToken.None), "duplicate WS properties rejected");
    await RejectSocket(async socket =>
    {
        var hello = Hello("huge-pid");
        hello["processId"] = JToken.Parse("9999999999999999999999999999999999999999999");
        await socket.SendAsync(Encoding.UTF8.GetBytes(hello.ToString(Formatting.None)), WebSocketMessageType.Text, true, CancellationToken.None);
    }, "oversized integer metadata rejected");
    await RejectSocket(async socket => await socket.SendAsync(new byte[RelayOptions.MaxMessageBytes + 1], WebSocketMessageType.Text, true, CancellationToken.None), "WS frame limit");
    await RejectSocket(_ => Task.CompletedTask, "hello deadline");
    await RejectSocket(async socket => await socket.SendAsync(Encoding.UTF8.GetBytes(Hello("transport-a").ToString(Formatting.None)), WebSocketMessageType.Text, true, CancellationToken.None), "duplicate instance rejected without replacing original");
    Check((int)(await Instances())["count"] == 2, "malformed sockets do not affect registered clients");

    using (var invalidRole = new ClientWebSocket())
    {
        invalidRole.Options.SetRequestHeader("Authorization", "Bearer " + DeveloperToken);
        bool rejected = false;
        try { await invalidRole.ConnectAsync(new Uri(socketUrl), CancellationToken.None); }
        catch (WebSocketException) { rejected = true; }
        Check(rejected, "developer token cannot open game connection");
    }
    for (int index = 0; index < 8; index++)
    {
        using var racing = await OpenSocket();
        string instance = "handshake-" + index;
        await racing.SendAsync(Encoding.UTF8.GetBytes(Hello(instance).ToString(Formatting.None)), WebSocketMessageType.Text, true, CancellationToken.None);
        var routing = Call("clientmcp_status", new JObject { ["instanceId"] = instance });
        using var deadline = new CancellationTokenSource(2000);
        var frame = new byte[4096];
        var message = await racing.ReceiveAsync(new ArraySegment<byte>(frame), deadline.Token);
        Check((string)JObject.Parse(Encoding.UTF8.GetString(frame, 0, message.Count))["kind"] == "welcome", "welcome precedes concurrent request " + index);
        racing.Abort();
        await routing;
    }
    using (var blocked = await OpenSocket())
    {
        await blocked.SendAsync(Encoding.UTF8.GetBytes(Hello("blocked").ToString(Formatting.None)), WebSocketMessageType.Text, true, CancellationToken.None);
        await blocked.ReceiveAsync(new ArraySegment<byte>(new byte[4096]), CancellationToken.None);
        var overload = await Task.WhenAll(Enumerable.Range(0, 72).Select(_ => Call("clientmcp_status", new JObject { ["instanceId"] = "blocked" })));
        Check(overload.Any(value => (string)value["result"]?["structuredContent"]?["error"]?["type"] == "RelayBusy"), "per-instance pending limit rejects before send");
        Check(overload.All(value => (bool)value["result"]["isError"]), "unanswered calls finish by bounded timeout or limit");
        blocked.Abort();
    }

    await CheckHeartbeatExpiry();
    Console.WriteLine("PASS: " + assertions + " transport assertions; real Unity reflection is verified separately.");
}
finally
{
    first?.Dispose();
    second?.Dispose();
    await app.StopAsync();
    await app.DisposeAsync();
}

RelayConnection Connect(string id) => new(socketUrl, ClientToken, Hello(id), (command, cancellation) =>
{
    string operation = (string)command["op"];
    string value = (string)command["value"];
    if (value != null) executionCounts.AddOrUpdate(value, 1, (_, count) => count + 1);
    if (operation == "slow") Thread.Sleep(1000);
    if (operation == "error") return new JObject { ["ok"] = false, ["error"] = new JObject { ["type"] = "TestFailure", ["message"] = "Expected test error." } };
    return new JObject { ["ok"] = true, ["result"] = new JObject { ["instanceId"] = id, ["value"] = value, ["sessionId"] = id, ["operation"] = operation } };
});

JObject Hello(string id) => new()
{
    ["kind"] = "hello", ["instanceId"] = id, ["sessionId"] = id,
    ["name"] = "transport-test", ["unityVersion"] = "transport-only", ["isEditor"] = false, ["processId"] = Environment.ProcessId
};

void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException("FAIL: " + name);
    assertions++;
    Console.WriteLine("PASS " + name);
}

async Task WaitFor(Func<Task<bool>> predicate, string name, int milliseconds = 5000)
{
    var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
    while (DateTime.UtcNow < until)
    {
        if (await predicate()) { Check(true, name); return; }
        await Task.Delay(30);
    }
    Check(false, name);
}

async Task<(int Status, string Body)> SendRaw(string body, string token = null, string origin = null, string host = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (origin != null) request.Headers.Add("Origin", origin);
    if (host != null) request.Headers.Host = host;
    using var reply = await http.SendAsync(request);
    return ((int)reply.StatusCode, await reply.Content.ReadAsStringAsync());
}

async Task<JObject> Rpc(string method, JObject parameters = null)
{
    var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref serial), ["method"] = method };
    if (parameters != null) request["params"] = parameters;
    var reply = await SendRaw(request.ToString(Formatting.None));
    if (reply.Status != 200) throw new InvalidOperationException("Unexpected HTTP status: " + reply.Status + " " + reply.Body);
    return JObject.Parse(reply.Body);
}

Task<JObject> Call(string name, JObject arguments) => Rpc("tools/call", new JObject { ["name"] = name, ["arguments"] = arguments });
async Task<JObject> Instances() => (JObject)(await Call("clientmcp_instances", new JObject()))["result"]["structuredContent"];
Task<JObject> Reflect(string instance, string op, string value = null) => Call("clientmcp_reflect", new JObject { ["instanceId"] = instance, ["op"] = op, ["description"] = "验证中转传输", ["value"] = value });

async Task<ClientWebSocket> OpenSocket()
{
    var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Authorization", "Bearer " + ClientToken);
    await socket.ConnectAsync(new Uri(socketUrl), CancellationToken.None);
    return socket;
}

async Task RejectSocket(Func<ClientWebSocket, Task> send, string name)
{
    using var socket = await OpenSocket();
    using var deadline = new CancellationTokenSource(4000);
    bool closed = false;
    try
    {
        await send(socket);
        var frame = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), deadline.Token);
        closed = frame.MessageType == WebSocketMessageType.Close;
    }
    catch (WebSocketException) { closed = true; }
    Check(closed, name);
}

async Task CheckHeartbeatExpiry()
{
    // 缩短第二个网关的心跳限时，避免改变真实 UPM 客户端固定的 10 秒发包节奏。
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    int heartbeatPort = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    var heartbeatApp = RelayApplication.Build(new[] { "--Logging:LogLevel:Default=Warning" }, new RelayOptions
    {
        Url = "http://127.0.0.1:" + heartbeatPort, DeveloperToken = DeveloperToken, ClientToken = ClientToken,
        HeartbeatTimeoutMilliseconds = 300
    });
    await heartbeatApp.StartAsync();
    try
    {
        using var stale = new ClientWebSocket();
        stale.Options.SetRequestHeader("Authorization", "Bearer " + ClientToken);
        await stale.ConnectAsync(new Uri("ws://127.0.0.1:" + heartbeatPort + "/client"), CancellationToken.None);
        await stale.SendAsync(Encoding.UTF8.GetBytes(Hello("stale").ToString(Formatting.None)), WebSocketMessageType.Text, true, CancellationToken.None);
        using var deadline = new CancellationTokenSource(3000);
        await stale.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), deadline.Token); // welcome
        bool closed = false;
        try
        {
            var frame = await stale.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), deadline.Token);
            closed = frame.MessageType == WebSocketMessageType.Close;
        }
        catch (WebSocketException) { closed = true; }
        Check(closed, "application heartbeat expires idle client");
    }
    finally { await heartbeatApp.StopAsync(); await heartbeatApp.DisposeAsync(); }
}
