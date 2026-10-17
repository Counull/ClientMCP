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
            if (request == null || request["jsonrpc"]?.Type != JTokenType.String || (string)request["jsonrpc"] != "2.0" || request["method"]?.Type != JTokenType.String)
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
                if (request["params"] != null && !(request["params"] is JObject))
                    throw new ArgumentException("params must be an object.");
                JObject result;
                switch (operation)
                {
                    case "initialize":
                        if (request["params"]?["protocolVersion"] != null && request["params"]["protocolVersion"].Type != JTokenType.String)
                            throw new ArgumentException("protocolVersion must be a string.");
                        string requested = (string)request["params"]?["protocolVersion"];
                        result = new JObject
                        {
                            ["protocolVersion"] = SupportsVersion(requested) ? requested : ProtocolVersion,
                            ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                            ["serverInfo"] = new JObject { ["name"] = "ClientMCP", ["version"] = "0.1.0-preview.5" },
                            ["instructions"] = "每次反射操作必须提供简短中文 description 说明意图，供运行时活动记录使用。研究反射路径前，先用 op=plan、action=list 查看已有计划；首次 prepare 后复用步骤，通过 run.parameters 传入本次参数。新的操作意图使用新的 operationId；重试沿用原 operationId 或用 result 查询回执。计划缺失或引用失效时只恢复必要步骤和对象入口，不缓存运行值或跨会话句柄。写入或调用前先检查成员元数据。计划仅在主线程同一帧执行有限短时步骤，失败立即停止且不回滚；不得依赖 UnityEditor、动态编译、定时器或阻塞方法。调用宿主已有的非阻塞方法只能表示已发起，回执不代表异步工作已经完成。"
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
                    ["description"] = "通用运行时反射：types/find/inspect/get/set/create/invoke/release，以及可复用的 plan。每次调用必须提供简短中文description说明意图，显示在活动记录和Unity Console中，不要包含凭据。先plan list再研究，prepare一次后run复用步骤；新意图新operationId，重试原ID或查result，引用失效时轻量恢复，不缓存运行值或跨会话句柄。计划同帧短时顺序执行，失败停止无回滚，不依赖UnityEditor或动态编译；非阻塞宿主方法只发起而不等待异步完成。普通反射实例用target句柄，静态用type；字段默认kind=field，属性须kind=property；对象参数用{$ref:句柄}。先inspect获取成员与parameterTypes，不自动调用getter。",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object", ["required"] = new JArray("op", "description"),
                        ["properties"] = new JObject
                        {
                            ["op"] = new JObject { ["type"] = "string", ["enum"] = new JArray("types", "find", "inspect", "get", "set", "create", "invoke", "release", "plan") },
                            ["description"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200, ["description"] = "必填的简短中文操作说明，用于活动记录和Unity Console；去除首尾空白后为1至200字符，不能包含控制字符或凭据。" },
                            ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray("prepare", "run", "result", "list", "release"), ["description"] = "op=plan 时的操作：准备、执行、查回执、列出或释放计划。" },
                            ["planId"] = new JObject { ["type"] = "string", ["description"] = "当前会话内的计划标识；prepare、run、release 使用。" },
                            ["operationId"] = new JObject { ["type"] = "string", ["description"] = "run/result 的操作意图标识；新意图使用新值，重试必须沿用原值。" },
                            ["sessionId"] = new JObject { ["type"] = "string", ["description"] = "run/result/release 必须匹配当前 clientmcp_status 返回的会话标识。" },
                            ["parameters"] = new JObject { ["type"] = "object", ["description"] = "run 的本次命名参数；省略时为空对象，不缓存本次运行值。", ["additionalProperties"] = true },
                            ["steps"] = new JObject
                            {
                                ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 64,
                                ["description"] = "prepare 的反射步骤；参数可用 {$param:名称} 或 {$step:序号,path:[路径]} 引用。path 可省略或为字符串、非负整数混合数组，不允许硬编码 $ref。",
                                ["items"] = new JObject
                                {
                                    ["type"] = "object", ["required"] = new JArray("op"),
                                    ["properties"] = new JObject { ["op"] = new JObject { ["type"] = "string", ["enum"] = new JArray("types", "find", "inspect", "get", "set", "create", "invoke", "release") } },
                                    ["additionalProperties"] = true
                                }
                            },
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
            try { value = execute(command); failed = (value?["state"]?.Type == JTokenType.String && (string)value["state"] == "failed") || (bool?)value?["ok"] == false; }
            catch (Exception error) { failed = true; value = Failure(error); }
            return new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = value.ToString(Formatting.None) }),
                ["structuredContent"] = value, ["isError"] = failed
            };
        }

        static JObject Failure(Exception error)
        {
            error = error.GetBaseException();
            return new JObject { ["ok"] = false, ["error"] = new JObject { ["type"] = error.GetType().FullName, ["message"] = error.Message } };
        }
        static JObject Error(JToken id, int code, string message) => new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
    }
}
