using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClientMcp;
using Newtonsoft.Json.Linq;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var info = Path.Combine(Path.GetTempPath(), "clientmcp-check-" + Guid.NewGuid().ToString("N") + ".json");
var roots = new RegisteredObjectHost(value => !(value is Counter counter) || counter.Alive);
var counter = new Counter(); roots.Register("counter", counter);
var logs = new List<string>();
using var host = new ClientMcpHost(roots, log: (message, _) => logs.Add(message));
host.Start(new ClientMcpOptions { InfoPath = info, RequestTimeoutMilliseconds = 500, ScriptCompiler = new RoslynScriptCompiler() });
var connection = JObject.Parse(File.ReadAllText(info));
var sid = (string)connection["sessionId"];
using var client = new HttpClient { BaseAddress = new Uri((string)connection["endpoint"]), Timeout = TimeSpan.FromSeconds(30) };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (string)connection["token"]);
JObject Call(JObject command, bool pump = true)
{
    command["description"] = "验证通用宿主行为";
    var sending = client.PostAsync("/reflect", new StringContent(command.ToString(), Encoding.UTF8, "application/json"));
    while (!sending.IsCompleted) { if (pump) host.Pump(); Thread.Sleep(1); }
    var response = sending.GetAwaiter().GetResult();
    return JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
}
JObject Script(string action, params object[] pairs)
{
    var request = new JObject { ["op"] = "script", ["action"] = action, ["sessionId"] = sid };
    for (int i = 0; i < pairs.Length; i += 2) request[(string)pairs[i]] = pairs[i+1] is JToken token ? token : JToken.FromObject(pairs[i+1]);
    return Call(request);
}
JObject Run(string id, string operation, JObject parameters = null) => Script("run", "scriptId", id, "operationId", operation, "parameters", parameters ?? new JObject());
Check((bool)Call(new JObject { ["op"] = "status" })["result"]["scriptsEnabled"], "脚本启用状态");
var found = Call(new JObject { ["op"] = "find", ["type"] = typeof(Counter).FullName });
var handle = (string)found["result"]["items"][0]["$ref"];
Check(handle != null, ".NET 注册根对象可查找");
Check((bool)Script("prepare", "scriptId", "add", "code", "var c = Root<Counter>(\"counter\"); c.Value += (int)Parameters[\"amount\"]; c.LastThread = System.Threading.Thread.CurrentThread.ManagedThreadId; return c.Value;")["ok"], "准备脚本");
var parameters = new JObject { ["amount"] = 3 };
var first = Run("add", "once", parameters);
Check((int)first["result"]["result"]["value"] == 3 && counter.Value == 3, "脚本实际改变对象并返回值");
Check(counter.LastThread == Environment.CurrentManagedThreadId, "执行位于宿主线程");
Check((bool)Run("add", "once", parameters)["result"]["cacheHit"] && counter.Value == 3, "重试不重复写入");
Check(!(bool)Run("add", "once", new JObject { ["amount"] = 9 })["ok"] && counter.Value == 3, "同编号参数冲突拒绝");
Check((string)Run("add", "twice", parameters)["result"]["state"] == "completed" && counter.Value == 6, "新编号重新执行");
Check((bool)Script("result", "operationId", "once")["result"]["cacheHit"], "查询回执");
Check(!(bool)Script("prepare", "scriptId", "broken", "code", "this is invalid C#;")["ok"], "编译错误返回失败");
Check(!(bool)Script("prepare", "scriptId", "async", "code", "await System.Threading.Tasks.Task.Delay(1);")["ok"], "拒绝异步脚本");
Check((bool)Script("prepare", "scriptId", "failure", "code", "Root<Counter>(\"counter\").Value++; throw new InvalidOperationException(\"fixture failure\");")["ok"], "准备失败夹具");
var failed = Run("failure", "failed");
Check((string)failed["result"]["state"] == "failed" && counter.Value == 7, "保留失败及已发生修改");
Check((bool)Run("failure", "failed")["result"]["cacheHit"] && counter.Value == 7, "失败去重");
Check((bool)Script("prepare", "scriptId", "handle", "code", "return Get<Counter>((string)Parameters[\"handle\"]);")["ok"], "准备句柄脚本");
var objectResult = Run("handle", "object", new JObject { ["handle"] = handle });
Check((string)objectResult["result"]["result"]["value"]["$ref"] == handle, "脚本与反射共用句柄");
Check(!(bool)Call(new JObject { ["op"] = "script", ["action"] = "run", ["sessionId"] = "old", ["scriptId"] = "add", ["operationId"] = "old" })["ok"], "拒绝旧会话");
// 排队超时不能在后续 Pump 落地。
var timed = new JObject { ["op"] = "set", ["target"] = handle, ["member"] = "Value", ["value"] = 99 };
Check(!(bool)Call(timed, false)["ok"], "排队超时失败"); host.Pump(); Check(counter.Value == 7, "超时队列不落地");
// 反射计划与脚本共用同一对象。
Check((bool)Call(new JObject { ["op"] = "plan", ["action"] = "prepare", ["planId"] = "read", ["steps"] = new JArray(new JObject { ["op"] = "get", ["target"] = new JObject { ["$param"] = "target" }, ["member"] = "Value" }) })["ok"], "准备原反射计划");
var plan = Call(new JObject { ["op"] = "plan", ["action"] = "run", ["planId"] = "read", ["operationId"] = "p1", ["sessionId"] = sid, ["parameters"] = new JObject { ["target"] = new JObject { ["$ref"] = handle } } });
Check((int)plan["result"]["results"][0]["value"] == 7, "原反射计划兼容");
counter.Alive = false;
Check(!(bool)Call(new JObject { ["op"] = "get", ["target"] = handle, ["member"] = "Value" })["ok"], "失效对象句柄拒绝");
counter.Alive = true;
Check(!logs.Any(line => line.Contains("c.Value") || line.Contains((string)connection["token"])), "日志不记录代码和令牌");
Check((bool)Script("release", "scriptId", "add")["result"]["released"], "显式释放脚本");
Check(!(bool)Script("result", "operationId", "once")["ok"], "释放结束去重回执");
// MCP 入口同样执行及标记错误。
var mcpTask = client.PostAsync("/mcp", new StringContent(new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JObject { ["name"] = "clientmcp_script", ["arguments"] = new JObject { ["action"] = "run", ["scriptId"] = "failure", ["operationId"] = "failed", ["sessionId"] = sid, ["description"] = "验证脚本回执" } } }.ToString(), Encoding.UTF8, "application/json"));
while (!mcpTask.IsCompleted) { host.Pump(); Thread.Sleep(1); }
var mcp = JObject.Parse(mcpTask.Result.Content.ReadAsStringAsync().Result);
Check((bool)mcp["result"]["isError"] && counter.Value == 7, "MCP 失败回执不重复执行");
host.Dispose(); Check(!File.Exists(info), "停止删除所属凭据");
using var disabled = new ClientMcpHost(new RegisteredObjectHost());
disabled.Start(new ClientMcpOptions { InfoPath = info });
connection = JObject.Parse(File.ReadAllText(info));
using var disabledClient = new HttpClient(); disabledClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (string)connection["token"]);
var denied = disabledClient.PostAsync((string)connection["endpoint"] + "/reflect", new StringContent("{\"op\":\"script\",\"action\":\"list\",\"description\":\"验证默认关闭\"}",Encoding.UTF8,"application/json")).Result;
Check(!(bool)JObject.Parse(denied.Content.ReadAsStringAsync().Result)["ok"], "默认拒绝脚本");
var threadInfo = info + ".thread";
var threadCounter = new Counter();
using (var threaded = new ClientMcpThreadHost(objects => objects.Register("counter", threadCounter),
    new ClientMcpOptions { InfoPath = threadInfo, RequestTimeoutMilliseconds = 200, ScriptCompiler = new RoslynScriptCompiler() }))
{
    threaded.Ready.GetAwaiter().GetResult();
    var threadConnection = JObject.Parse(File.ReadAllText(threadInfo));
    using var threadClient = new HttpClient { BaseAddress = new Uri((string)threadConnection["endpoint"]), Timeout = TimeSpan.FromSeconds(30) };
    threadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (string)threadConnection["token"]);
    JObject ThreadCall(JObject command)
    {
        command["description"] = "验证专用线程适配";
        var reply = threadClient.PostAsync("/reflect", new StringContent(command.ToString(), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        return JObject.Parse(reply.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }
    Check((string)ThreadCall(new JObject { ["op"] = "status" })["result"]["hostKind"] == "dotnet", ".NET 线程 Adapter 可用");
    Check((bool)ThreadCall(new JObject { ["op"] = "script", ["action"] = "prepare", ["scriptId"] = "slow", ["code"] = "var c = Root<Counter>(\"counter\"); c.Value++; c.LastThread = System.Threading.Thread.CurrentThread.ManagedThreadId; System.Threading.Thread.Sleep(600); return c.Value;" })["ok"], "线程 Adapter 准备脚本");
    var runSlow = new JObject { ["op"] = "script", ["action"] = "run", ["scriptId"] = "slow", ["operationId"] = "slow-once", ["sessionId"] = threadConnection["sessionId"] };
    Check(!(bool)ThreadCall(runSlow)["ok"], "已开始的脚本超时明确返回");
    Thread.Sleep(650);
    var completed = ThreadCall(new JObject { ["op"] = "script", ["action"] = "result", ["operationId"] = "slow-once", ["sessionId"] = threadConnection["sessionId"] });
    Check((string)completed["result"]["state"] == "completed" && threadCounter.Value == 1, "超时后可查询真实完成回执");
    Check((bool)ThreadCall(runSlow)["result"]["cacheHit"] && threadCounter.Value == 1, "超时重试不重复写入");
    Check(threadCounter.LastThread != Environment.CurrentManagedThreadId, "专用诊断线程实际执行");
}
Check(!File.Exists(threadInfo), "线程 Adapter 停止清理凭据");
Console.WriteLine("CLIENTMCP_CORE_CHECKS_OK checks=" + checks);

public sealed class Counter { public int Value; public int LastThread; public bool Alive = true; }
