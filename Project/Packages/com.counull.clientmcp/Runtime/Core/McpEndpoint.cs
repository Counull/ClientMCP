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
                            ["serverInfo"] = new JObject { ["name"] = "ClientMCP", ["version"] = "0.1.0-preview.6" },
                            ["instructions"] = "优先复用当前上下文或消费工程中已验证的计划、脚本入口；未知是否有可用计划时，用 op=plan、action=list 查询，已知当前会话计划可直接 run。首次操作且无可复用步骤时，若 Agent 可读取消费工程源码，先用自己的文件工具定向阅读相关定义和必要调用点，确认完整类型名、对象入口、成员路径、parameterTypes 和调用条件，信息足够即停止。源码已明确的结构和签名无需例行 types/inspect；源码不可用、信息不足或与运行版本不符时，才对相关类型和成员做最小反射探测。" +
                                "当前对象、句柄和必要状态仍从目标实例读取，不缓存运行值或跨会话句柄。已知的连续定位和读取步骤可合并为 plan；需要根据状态判断是否写入时，先读取并判断，再执行写入，plan 不提供条件分支。写入或调用前通过源码或 inspect 明确签名和副作用，并检查必要的当前状态。首次验证后保存步骤，后续复用；失效时只恢复必要步骤和对象入口。" +
                                "每次反射操作必须提供简短中文 description 说明意图，不含凭据。prepare 后通过 run.parameters 传入本次参数；新意图使用新 operationId，超时重试沿用原编号或用 result 查询回执。计划仅在主线程同一帧执行有限短时步骤，失败停止且不回滚，不依赖 UnityEditor、动态编译、定时器或阻塞方法。宿主非阻塞方法的回执仅表示已发起，不表示异步工作完成。"
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
                    ["description"] = "通用运行时反射：types/find/inspect/get/set/create/invoke/release，以及可复用的 plan。优先复用已验证入口；未知是否有计划时才 plan list，已知当前会话计划直接 run。首次无可复用步骤且可读消费工程源码时，先用文件工具定向读源码，确认类型、对象入口、成员路径、parameterTypes 和调用条件，信息足够即停止；已知结构无需例行 types/inspect，仅在源码不可用、信息不足或版本不符时最小探测。" +
                        "当前对象和必要状态仍从目标实例读取；已知连续读取可合并 plan，依赖状态的写入须先读取判断再执行，plan 无条件分支。写入或调用前通过源码或 inspect 明确签名和副作用。每次提供不含凭据的简短中文 description。prepare 后复用步骤，新意图新 operationId，超时重试原编号或查 result；失效时轻量恢复，不缓存运行值或跨会话句柄。计划同帧短时执行，失败停止无回滚，不依赖 UnityEditor 或动态编译；非阻塞方法回执不代表异步完成。实例用 target，静态用 type；字段默认 kind=field，属性须 kind=property；对象参数用 {$ref:句柄}；inspect 不调用 getter。",
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
                            ["scope"] = new JObject { ["type"] = "string", ["enum"] = new JArray("scene", "loaded", "roots") },
                            ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }, ["limit"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 200 },
                            ["targets"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } }, ["all"] = new JObject { ["type"] = "boolean" }
                        },
                        ["additionalProperties"] = false
                    }
                },
                new JObject
                {
                    ["name"] = "clientmcp_script",
                    ["description"] = "受信任 C# 同步脚本；仅宿主显式启用后可用。prepare 编译并缓存，run 使用新 operationId 执行，重试原编号或 result 查回执。脚本非沙箱，超时不能终止已开始执行，不支持 await/async，不自动回滚。Parameters 提供本次参数，Get<T>(句柄) 获取对象，.NET 可用 Root<T>(名称)。",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object", ["required"] = new JArray("action", "description"),
                        ["properties"] = new JObject
                        {
                            ["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray("prepare", "run", "result", "list", "release") },
                            ["description"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200 },
                            ["scriptId"] = new JObject { ["type"] = "string" },
                            ["operationId"] = new JObject { ["type"] = "string" },
                            ["sessionId"] = new JObject { ["type"] = "string" },
                            ["code"] = new JObject { ["type"] = "string", ["maxLength"] = 65536 },
                            ["parameters"] = new JObject { ["type"] = "object", ["additionalProperties"] = true }
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
            else if (name == "clientmcp_script")
            {
                command = (JObject)(parameters["arguments"] as JObject ?? throw new ArgumentException("arguments must be an object.")).DeepClone();
                command["op"] = "script";
            }
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
