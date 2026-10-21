using System.Net;
using System.Text;
using ClientMcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClientMcp.Server;

/// <summary>认证在路由前完成；MCP 封装与 Runtime 共用，网关仅增加实例定位与请求转发。</summary>
public static class RelayApplication
{
    public static WebApplication Build(string[] args, RelayOptions options = null)
    {
        options ??= RelayOptions.FromEnvironment();
        options.Validate();
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(options.Url);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = RelayOptions.MaxMessageBytes);
        var app = builder.Build();
        var registry = new RelayRegistry(options);
        var origin = new Uri(options.Url);
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Use(async (context, next) =>
        {
            // 原生 MCP 与游戏连接不发送 Origin。拒绝所有浏览器 Origin，避免环境令牌被跨站调用。
            if (context.Request.Headers.ContainsKey("Origin")) { await WriteAsync(context, HttpReply.Error(403, "Browser origins are not allowed.")); return; }
            if (!string.Equals(context.Request.Host.Host.Trim('[', ']'), origin.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) ||
                context.Request.Host.Port.GetValueOrDefault(origin.IsDefaultPort ? (origin.Scheme == "https" ? 443 : 80) : origin.Port) != origin.Port)
            { await WriteAsync(context, HttpReply.Error(400, "Unexpected Host.")); return; }
            if (!context.Request.IsHttps && (context.Connection.RemoteIpAddress == null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress)))
            { await WriteAsync(context, HttpReply.Error(403, "Remote access requires HTTPS.")); return; }
            string expected;
            if (context.Request.Path == "/client") expected = options.ClientToken;
            else if (context.Request.Path == "/mcp") expected = options.DeveloperToken;
            else { context.Response.StatusCode = 404; return; }
            string authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || !RelayOptions.TokenMatches(authorization[7..], expected))
            { context.Response.Headers.WWWAuthenticate = "Bearer"; await WriteAsync(context, HttpReply.Error(401, "Unauthorized.")); return; }
            context.Response.Headers.CacheControl = "no-store";
            await next();
        });
        app.Map("/client", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { await WriteAsync(context, HttpReply.Error(400, "WebSocket upgrade required.")); return; }
            if (!registry.ReserveConnection()) { await WriteAsync(context, HttpReply.Error(503, "Connection limit reached.")); return; }
            try
            {
                var socket = await context.WebSockets.AcceptWebSocketAsync();
                var client = new RelayClient(socket, options, registry);
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
                await client.RunAsync(lifetime.Token);
            }
            finally { registry.ReleaseConnection(); }
        });
        app.Map("/mcp", async context =>
        {
            if (context.Request.Method != "POST") { await WriteAsync(context, HttpReply.Error(405, "Use POST with a JSON-RPC message; standalone SSE is not provided.")); return; }
            if (context.Request.ContentType == null || !context.Request.ContentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            { await WriteAsync(context, HttpReply.Error(415, "Content-Type must be application/json.")); return; }
            string protocol = context.Request.Headers["MCP-Protocol-Version"].ToString();
            if (protocol.Length != 0 && !McpEndpoint.SupportsVersion(protocol))
            { await WriteAsync(context, HttpReply.Error(400, "Unsupported MCP-Protocol-Version.")); return; }
            try
            {
                var request = await ReadJsonAsync(context.Request, context.RequestAborted);
                await WriteAsync(context, await HandleMcpAsync(request, registry, context.RequestAborted));
            }
            catch (Exception error) when (error is JsonException || error is DecoderFallbackException || error is InvalidDataException)
            { await WriteAsync(context, HttpReply.Error(400, "Invalid JSON object or JSON limit exceeded.")); }
            catch (BadHttpRequestException error)
            {
                // 413 可能在读取正文前返回。明确关闭连接，防止连接池复用尚有超限正文的 HTTP/1 连接。
                if (error.StatusCode == 413 && context.Request.Protocol.StartsWith("HTTP/1.", StringComparison.Ordinal))
                    context.Response.Headers.Connection = "close";
                await WriteAsync(context, HttpReply.Error(error.StatusCode, "HTTP request limit exceeded."));
            }
        });
        return app;
    }

    static async Task<HttpReply> HandleMcpAsync(JObject request, RelayRegistry registry, CancellationToken cancellation)
    {
        // 无效 JSON-RPC、初始化与普通协议方法保持共用端点的行为，不单独实现另一份 MCP。
        if (request?["jsonrpc"]?.Type != JTokenType.String || (string)request["jsonrpc"] != "2.0" || request["method"]?.Type != JTokenType.String)
            return HttpReply.Json(400, new JObject { ["jsonrpc"] = "2.0", ["id"] = request?["id"]?.DeepClone(), ["error"] = new JObject { ["code"] = -32600, ["message"] = "Invalid JSON-RPC request." } });
        string method = request?["method"]?.Type == JTokenType.String ? (string)request["method"] : null;
        bool validRequest = (string)request?["jsonrpc"] == "2.0" && request?["id"] != null;
        if (validRequest && method == "tools/call")
        {
            var parameters = request["params"] as JObject;
            string tool = parameters?["name"]?.Type == JTokenType.String ? (string)parameters["name"] : null;
            var arguments = parameters?["arguments"] as JObject;
            if (tool == "clientmcp_instances")
            {
                if (arguments != null && arguments.Count != 0) return InvalidArguments(request, "clientmcp_instances accepts no arguments.");
                return ToolReply(request, registry.ListInstances(), false);
            }
            if (tool is "clientmcp_reflect" or "clientmcp_status" or "clientmcp_script")
            {
                if (arguments?["instanceId"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)arguments["instanceId"]) ||
                    ((string)arguments["instanceId"]).Length > 128)
                    return InvalidArguments(request, "Select an online instanceId from clientmcp_instances for each call.");
                if (tool == "clientmcp_status" && arguments.Properties().Any(property => property.Name != "instanceId"))
                    return InvalidArguments(request, "clientmcp_status accepts only instanceId.");
                string instanceId = (string)arguments["instanceId"];
                var command = tool == "clientmcp_status" ? new JObject { ["op"] = "status" } : (JObject)arguments.DeepClone();
                command.Remove("instanceId");
                if (tool == "clientmcp_script") command["op"] = "script";
                var body = await registry.ForwardAsync(instanceId, command, cancellation);
                var forwarded = (JObject)request.DeepClone();
                forwarded["params"]["arguments"] = command;
                return McpEndpoint.Handle("POST", forwarded, _ => (bool?)body["ok"] == true ? (JObject)body["result"] : body);
            }
        }
        var reply = McpEndpoint.Handle("POST", request, _ => throw new ArgumentException("Use an explicitly selected runtime instance."));
        if (reply.Body?["result"] is JObject result)
        {
            if (method == "initialize")
            {
                result["serverInfo"]["name"] = "ClientMCP Relay";
                result["instructions"] = "先通过 clientmcp_instances 选择在线实例，每次 reflect/status 显式提供 instanceId；句柄、planId、sessionId 只属于该实例，断线或超时不代表操作撤销，中转不自动重发。" + (string)result["instructions"];
            }
            if (method == "tools/list")
            {
                var tools = (JArray)result["tools"];
                foreach (JObject tool in tools)
                {
                    var schema = (JObject)tool["inputSchema"];
                    ((JObject)schema["properties"])["instanceId"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128, ["description"] = "clientmcp_instances 返回的在线实例标识。每次明确指定，不共享隐式选中状态。" };
                    if (schema["required"] is not JArray required) { required = new JArray(); schema["required"] = required; }
                    required.Add("instanceId");
                }
                tools.Add(new JObject
                {
                    ["name"] = "clientmcp_instances", ["description"] = "列出此开发者令牌授权的在线运行实例；后续 reflect/status 每次使用所选 instanceId。",
                    ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false }
                });
            }
        }
        return reply;
    }

    static HttpReply InvalidArguments(JObject request, string message) => HttpReply.Json(200, new JObject
    {
        ["jsonrpc"] = "2.0", ["id"] = request["id"].DeepClone(), ["error"] = new JObject { ["code"] = -32602, ["message"] = message }
    });

    static HttpReply ToolReply(JObject request, JObject value, bool failed) => HttpReply.Json(200, new JObject
    {
        ["jsonrpc"] = "2.0", ["id"] = request["id"].DeepClone(),
        ["result"] = new JObject
        {
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = value.ToString(Formatting.None) }),
            ["structuredContent"] = value, ["isError"] = failed
        }
    });

    static async Task<JObject> ReadJsonAsync(HttpRequest request, CancellationToken cancellation)
    {
        if (request.ContentLength > RelayOptions.MaxMessageBytes) throw new BadHttpRequestException("Too large.", 413);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, cancellation)) != 0)
        {
            if (bytes.Length + count > RelayOptions.MaxMessageBytes) throw new BadHttpRequestException("Too large.", 413);
            bytes.Write(buffer, 0, count);
        }
        return ParseJson(new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length)));
    }

    internal static JObject ParseJson(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new JsonReaderException("Trailing JSON content.");
        return result;
    }

    static async Task WriteAsync(HttpContext context, HttpReply reply)
    {
        context.Response.StatusCode = reply.Status;
        if (reply.Body == null) return;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(reply.Body.ToString(Formatting.None), context.RequestAborted);
    }
}
