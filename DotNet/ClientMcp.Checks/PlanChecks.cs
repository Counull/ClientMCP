using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using ClientMcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// 验证真实反射链、去重与 HTTP/MCP 返回，不以提示文本或内部方法实现作为断言。
static class PlanChecks
{
    const string Session = "plan-check-session";
    static int checks;
    static readonly string FixtureType = typeof(PlanFixture).FullName;

    public static void Run()
    {
        checks = 0;
        CheckPlans();
        CheckTransport();
        Console.WriteLine("CLIENTMCP_PLAN_FAST_PATH_CHECKS_OK checks=" + checks);
    }

    static void CheckPlans()
    {
        PlanFixture.Current = new PlanCounter();
        var reflection = new ReflectionSession();
        var plans = new ReflectionPlanSession(reflection, Session);
        var request = Plan("execute", "increment", "once", MutationSteps(), 3);
        request["resultSteps"] = new JArray(2);
        var first = plans.Execute(request);
        Check(PlanFixture.Current.Value == 3 && (string)first["state"] == "completed", "单次准备执行真实对象链");
        Check(((JArray)first["results"]).Count == 1 && (int)first["results"][0]["value"] == 3 &&
            JToken.DeepEquals(first["resultSteps"], new JArray(2)), "返回所选步骤并保留原序号");
        var full = plans.Execute(Plan("result", operation: "once"));
        Check(((JArray)full["results"]).Count == 3 && full["resultSteps"] == null, "省略选择时返回原完整回执");
        first["results"][0]["value"] = 999;
        Check((int)plans.Execute(Plan("result", operation: "once"))["results"][2]["value"] == 3, "返回值不能修改缓存回执");
        request["resultSteps"] = new JArray(2, 0);
        var retry = plans.Execute(request);
        Check((bool)retry["cacheHit"] && PlanFixture.Current.Value == 3 &&
            JToken.DeepEquals(retry["resultSteps"], new JArray(2, 0)), "改变返回顺序仍复用原执行");
        var second = Plan("run", "increment", "twice", amount: 4);
        second["resultSteps"] = new JArray();
        var statusOnly = plans.Execute(second);
        Check(PlanFixture.Current.Value == 7 && (int)statusOnly["completedSteps"] == 3 &&
            ((JArray)statusOnly["results"]).Count == 0, "已有计划可复用并只返回状态");
        Check((int)plans.Execute(Plan("result", operation: "once"))["results"][2]["value"] == 3, "原回执保留执行当时的值");

        var invalid = new JToken[] { JValue.CreateNull(), new JValue("2"), new JArray(-1), new JArray(3),
            new JArray(0, 0), new JArray(1.5), new JArray("1"), new JArray(long.MaxValue) };
        foreach (var selection in invalid)
        {
            var bad = Plan("execute", "invalid", "bad", MutationSteps(), 100);
            bad["resultSteps"] = selection;
            Reject<ArgumentException>(() => plans.Execute(bad), "无效返回范围必须在写入前拒绝");
        }
        Check(PlanFixture.Current.Value == 7 && ((JArray)plans.Execute(Plan("list"))["plans"]).Count == 1,
            "无效 execute 不改变对象或登记新计划");
        var oldSession = Plan("execute", "old", "old", MutationSteps(), 100);
        oldSession["sessionId"] = "old-session";
        Reject<InvalidOperationException>(() => plans.Execute(oldSession), "旧会话不得准备或执行");
        Reject<InvalidOperationException>(() => plans.Execute(Plan("execute", "other", "once", MutationSteps(), 3)),
            "跨计划重用操作号必须拒绝");
        Reject<InvalidOperationException>(() => plans.Execute(Plan("execute", "increment", "once", MutationSteps(), 4)),
            "同编号不同参数必须拒绝");
        var changed = MutationSteps(); changed[2]["member"] = "Missing";
        Reject<InvalidOperationException>(() => plans.Execute(Plan("execute", "increment", "once", changed, 3)),
            "同名不同步骤必须拒绝");
        Check(((JArray)plans.Execute(Plan("list"))["plans"]).Count == 1, "拒绝的执行不占用计划容量");

        var failingSteps = MutationSteps();
        failingSteps[2] = Step("invoke", "target", Previous(0), "member", "Missing");
        failingSteps.Add(Step("invoke", "target", Previous(0), "member", "Add", "args", new JArray(100),
            "parameterTypes", new JArray("System.Int32")));
        var failing = Plan("execute", "failure", "failed", failingSteps, 2);
        failing["resultSteps"] = new JArray(3, 0);
        var failed = plans.Execute(failing);
        Check((string)failed["state"] == "failed" && (int)failed["failedStep"] == 2 &&
            (int)failed["completedSteps"] == 2 && PlanFixture.Current.Value == 9, "失败保留已执行写入并停止后续步骤");
        Check(JToken.DeepEquals(failed["resultSteps"], new JArray(0)) && failed["error"] != null,
            "所选未执行步骤不返回，错误与原序号不丢失");
        failing["resultSteps"] = new JArray();
        Check((bool)plans.Execute(failing)["cacheHit"] && PlanFixture.Current.Value == 9, "失败重试不重复部分写入");
        var failedFull = plans.Execute(Plan("result", operation: "failed"));
        Check(((JArray)failedFull["results"]).Count == 2 && failedFull["error"] != null, "失败的完整结果仍可查询");
        plans.Execute(Plan("release", "failure"));
        Reject<KeyNotFoundException>(() => plans.Execute(Plan("result", operation: "failed")), "释放结束回执保留");

        plans.Clear();
        var read = new JArray(Step("get", "type", FixtureType, "member", "Text"));
        plans.Execute(Plan("prepare", "limit", steps: read));
        for (int i = 0; i < 128; i++) plans.Execute(Plan("run", "limit", "limit-" + i));
        Reject<InvalidOperationException>(() => plans.Execute(Plan("execute", "overflow", "overflow", read)), "容量满拒绝新增执行");
        Check(((JArray)plans.Execute(Plan("list"))["plans"]).Count == 1, "回执容量满时不登记新计划");
        Check((bool)plans.Execute(Plan("execute", "limit", "limit-0", read))["cacheHit"], "容量满仍可重取旧回执");
        plans.Clear();
        reflection.Clear();
    }

    static void CheckTransport()
    {
        PlanFixture.Current = new PlanCounter();
        var info = Path.Combine(Path.GetTempPath(), "clientmcp-plan-" + Guid.NewGuid().ToString("N") + ".json");
        using var host = new ClientMcpHost(new RegisteredObjectHost());
        host.Start(new ClientMcpOptions { InfoPath = info, RequestTimeoutMilliseconds = 150 });
        var connection = JObject.Parse(File.ReadAllText(info));
        using var client = new HttpClient { BaseAddress = new Uri((string)connection["endpoint"]), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (string)connection["token"]);
        JObject lastEntry = null;
        int lastBytes = 0;
        JObject Send(JObject command, bool mcp = false, bool pump = true, bool delayQueue = false)
        {
            command["sessionId"] = connection["sessionId"].DeepClone();
            var payload = mcp ? new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = "clientmcp_reflect", ["arguments"] = command } } : command;
            var sending = client.PostAsync(mcp ? "/mcp" : "/reflect",
                new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"));
            var timer = Stopwatch.StartNew();
            if (delayQueue)
            {
                while ((int)host.GetConnectionStatus()["pendingRequests"] == 0 && !sending.IsCompleted && timer.ElapsedMilliseconds < 5000)
                    Thread.Sleep(1);
                Thread.Sleep(25);
            }
            while (!sending.IsCompleted && timer.ElapsedMilliseconds < 5000) { if (pump) host.Pump(); Thread.Sleep(1); }
            if (!sending.IsCompleted) throw new Exception("请求未在验证期限内完成");
            using var reply = sending.GetAwaiter().GetResult();
            var bytes = reply.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            lastBytes = bytes.Length;
            lastEntry = (JObject)((JArray)host.GetConnectionStatus()["recentRequests"]).Last;
            Check((int)lastEntry["responseBytes"] == bytes.Length, "记录 UTF-8 响应体实际字节数");
            Check((double)lastEntry["durationMs"] >= 0 && (double)lastEntry["queueMs"] >= 0, "请求与排队计时非负");
            return JObject.Parse(Encoding.UTF8.GetString(bytes));
        }

        var steps = new JArray(Enumerable.Range(0, 8).Select(_ => Step("get", "type", FixtureType, "member", "Text")));
        long before = (long)host.GetConnectionStatus()["reflectRequests"];
        Send(Plan("prepare", "baseline", steps: steps));
        Send(Plan("run", "baseline", "baseline-once"));
        int fullBytes = lastBytes;
        long baselineRequests = (long)host.GetConnectionStatus()["reflectRequests"] - before;
        before = (long)host.GetConnectionStatus()["reflectRequests"];
        var compactRequest = Plan("execute", "compact", "compact-once", steps);
        compactRequest["resultSteps"] = new JArray(7);
        var compact = Send(compactRequest, delayQueue: true);
        int compactBytes = lastBytes;
        long compactRequests = (long)host.GetConnectionStatus()["reflectRequests"] - before;
        Check((string)compact["result"]["state"] == "completed" && baselineRequests == 2 && compactRequests == 1,
            "同一读取链首次请求数从两次变为一次");
        Check(compactBytes < fullBytes / 2 && (string)compact["result"]["results"][0]["value"] == PlanFixture.Text,
            "指定返回结果减少报文且保留目标值");
        Check((double)lastEntry["queueMs"] >= 20 && (double)lastEntry["executionMs"] >= 0,
            "计时区分故意排队与实际执行");
        Console.WriteLine($"CLIENTMCP_PLAN_PAYLOAD requests={baselineRequests}->{compactRequests} responseBytes={fullBytes}->{compactBytes}");

        var mcpRequest = Plan("execute", "mcp", "mcp-once", MutationSteps(), 1);
        mcpRequest["resultSteps"] = new JArray(2);
        var mcp = Send(mcpRequest, mcp: true);
        Check(!(bool)mcp["result"]["isError"] && (int)mcp["result"]["structuredContent"]["results"][0]["value"] == 1,
            "MCP 单次执行与选择结果路由到真实核心");
        Check(JToken.DeepEquals(JObject.Parse((string)mcp["result"]["content"][0]["text"]), mcp["result"]["structuredContent"]),
            "MCP 文本与结构化回执一致");
        Send(mcpRequest, mcp: true);
        Check(PlanFixture.Current.Value == 1, "MCP 重试不重复写入");
        var failing = Plan("execute", "mcp-failed", "mcp-failed", new JArray(Step("get", "type", FixtureType, "member", "Missing")));
        failing["resultSteps"] = new JArray();
        var failure = Send(failing, mcp: true);
        Check((bool)failure["result"]["isError"] && failure["result"]["structuredContent"]["error"] != null &&
            !(bool)lastEntry["success"], "空结果仍保留 MCP 失败与活动记录失败标记");

        var timeout = Plan("execute", "queued", "queued-once", MutationSteps(), 5);
        Check(!(bool)Send(timeout, pump: false)["ok"] && lastEntry["executionMs"].Type == JTokenType.Null,
            "执行前超时没有伪造执行耗时");
        host.Pump();
        Check(PlanFixture.Current.Value == 1, "排队超时后不落地写入");
        Send(timeout);
        Check(PlanFixture.Current.Value == 6, "未开始的同编号可重新请求并执行一次");

        var slow = Plan("execute", "slow", "slow-once", new JArray(Step("invoke", "type", FixtureType,
            "member", "SlowAdd", "parameterTypes", new JArray())));
        Check(!(bool)Send(slow)["ok"] && lastEntry["executionMs"].Type == JTokenType.Null,
            "已开始后超时记录未完成执行而非零耗时");
        var recovered = Send(Plan("result", operation: "slow-once"));
        Check((string)recovered["result"]["state"] == "completed" && PlanFixture.Current.Value == 7,
            "已开始后超时可查询真实完成结果");
        Send(slow);
        Check(PlanFixture.Current.Value == 7, "已开始后超时重试不得重复写入");
        host.Dispose();
        Check(!File.Exists(info), "验证宿主关闭后清理连接凭据");
    }

    static JObject Plan(string action, string id = null, string operation = null, JArray steps = null, int? amount = null)
    {
        var value = Step("plan", "action", action, "sessionId", Session, "description", "验证计划快速执行");
        if (id != null) value["planId"] = id;
        if (operation != null) value["operationId"] = operation;
        if (steps != null) value["steps"] = steps.DeepClone();
        if (amount.HasValue) value["parameters"] = new JObject { ["amount"] = amount.Value };
        return value;
    }

    static JArray MutationSteps() => new JArray(
        Step("get", "type", FixtureType, "member", "Current"),
        Step("invoke", "target", Previous(0), "member", "Add", "args", new JArray(new JObject { ["$param"] = "amount" }),
            "parameterTypes", new JArray("System.Int32")),
        Step("get", "target", Previous(0), "member", "Value"));
    static JObject Previous(int step) => new JObject { ["$step"] = step, ["path"] = new JArray("value") };
    static JObject Step(string op, params object[] fields)
    {
        var result = new JObject { ["op"] = op };
        for (int i = 0; i < fields.Length; i += 2)
            result[(string)fields[i]] = fields[i + 1] is JToken token ? token.DeepClone() : JToken.FromObject(fields[i + 1]);
        return result;
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static void Reject<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { checks++; return; }
        throw new Exception(message);
    }
}

public sealed class PlanCounter
{
    public int Value;
    public void Add(int amount) => Value += amount;
}

public static class PlanFixture
{
    public static PlanCounter Current = new PlanCounter();
    public static string Text = new string('测', 1024);
    public static void SlowAdd() { Current.Value++; Thread.Sleep(350); }
}
