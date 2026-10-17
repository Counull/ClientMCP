using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClientMcp
{
    /// <summary>将标准 MCP 工具调用映射到同一套反射元操作，不另建业务命令或执行器。</summary>
    internal static class McpEndpoint
    {
        const string ProtocolVersion = "2025-11-25";
        public static bool SupportsVersion(string version) => version == ProtocolVersion || version == "2025-06-18" || version == "2025-03-26";

        public static HttpReply Handle(string method, JObject request, Func<JObject, JObject> execute)
        {
            if (method != "POST") return HttpReply.Error(405, "This endpoint uses JSON responses; no standalone SSE stream.");
            if (request == null || (string)request["jsonrpc"] != "2.0" || request["method"]?.Type != JTokenType.String)
                return HttpReply.Json(400, Error(request?["id"], -32600, "Invalid JSON-RPC request."));
            string operation = (string)request["method"];
            JToken id = request["id"];
            if (id == null)
            {
                if (operation == "notifications/initialized") return HttpReply.Json(202, null);
                // 当前不承诺跨 HTTP 请求撤销。明确拒绝，避免把已接受通知误解为写入已取消。
                if (operation == "notifications/cancelled") return HttpReply.Error(400, "Cancellation is not supported; inspect runtime state before retrying a mutation.");
                return HttpReply.Json(400, Error(null, -32600, "Unsupported notification."));
            }
            try
            {
                JObject result;
                switch (operation)
                {
                    case "initialize":
                        string requested = (string)request["params"]?["protocolVersion"];
                        result = new JObject
                        {
                            ["protocolVersion"] = SupportsVersion(requested) ? requested : ProtocolVersion,
                            ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                            ["serverInfo"] = new JObject { ["name"] = "ClientMCP", ["version"] = "0.1.0-preview.3" },
                            ["instructions"] = "Use reflection primitives on the running instance. Every reflection operation must include a short Chinese description of its intent for the runtime activity log. Get member metadata before writes or invocation. Operations run on Unity's main thread; do not call blocking methods."
                        };
                        break;
                    case "ping": result = new JObject(); break;
                    case "tools/list": result = ListTools(); break;
                    case "tools/call": result = CallTool(request["params"] as JObject, execute); break;
                    default: return HttpReply.Json(200, Error(id, -32601, "Unknown method: " + operation));
                }
                return HttpReply.Json(200, new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
            }
            catch (ArgumentException error) { return HttpReply.Json(200, Error(id, -32602, error.Message)); }
        }

        static JObject ListTools() => new JObject
        {
            ["tools"] = new JArray
            {
                new JObject
                {
                    ["name"] = "clientmcp_reflect",
                    ["description"] = "通用运行时反射：types/find/inspect/get/set/create/invoke/release。每次调用必须提供简短中文description说明操作意图，显示在运行时活动记录和Unity Console中，不要包含凭据。实例用target句柄，静态用type；字段默认kind=field，属性须kind=property；对象参数用{$ref:句柄}。先inspect获取成员与parameterTypes，不自动调用getter。",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object", ["required"] = new JArray("op", "description"),
                        ["properties"] = new JObject
                        {
                            ["op"] = new JObject { ["type"] = "string", ["enum"] = new JArray("types", "find", "inspect", "get", "set", "create", "invoke", "release") },
                            ["description"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200, ["description"] = "必填的简短中文操作说明，用于活动记录和Unity Console；去除首尾空白后为1至200字符，不能包含控制字符或凭据。" },
                            ["target"] = new JObject { ["type"] = "string" }, ["type"] = new JObject { ["type"] = "string" },
                            ["member"] = new JObject { ["type"] = "string" }, ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("field", "property") },
                            ["value"] = new JObject(), ["args"] = new JObject { ["type"] = "array" },
                            ["parameterTypes"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                            ["declaringType"] = new JObject { ["type"] = "string" }, ["query"] = new JObject { ["type"] = "string" },
                            ["scope"] = new JObject { ["type"] = "string", ["enum"] = new JArray("scene", "loaded") },
                            ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }, ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 200 },
                            ["targets"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } }, ["all"] = new JObject { ["type"] = "boolean" }
                        },
                        ["additionalProperties"] = false
                    }
                },
                new JObject { ["name"] = "clientmcp_status", ["description"] = "读取当前运行实例、Unity版本与帧号。", ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false } }
            }
        };

        static JObject CallTool(JObject parameters, Func<JObject, JObject> execute)
        {
            string name = (string)parameters?["name"];
            JObject command;
            if (name == "clientmcp_status") command = new JObject { ["op"] = "status" };
            else if (name == "clientmcp_reflect") command = parameters["arguments"] as JObject ?? throw new ArgumentException("arguments must be an object.");
            else throw new ArgumentException("Unknown tool: " + name);
            JObject value;
            bool failed = false;
            try { value = execute(command); }
            catch (Exception error) { failed = true; value = ClientMcpRuntime.Failure(error); }
            return new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = value.ToString(Formatting.None) }),
                ["structuredContent"] = value, ["isError"] = failed
            };
        }

        static JObject Error(JToken id, int code, string message) => new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
    }
}
