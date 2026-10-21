using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClientMcp.Development
{
    /// <summary>只验证通用计划的可观察行为，不依赖消费项目或 Editor 临时编译代码。</summary>
    public static class PlanValidation
    {
        static int checks;
        const string SessionId = "plan-validation-session";

        public static void RunChecks()
        {
            checks = 0;
            var reflection = new ReflectionSession(new UnityReflectionHost());
            var plans = new ReflectionPlanSession(reflection, SessionId);
            try
            {
                var root = reflection.Execute(Step("create", "type", "System.Text.StringBuilder",
                    "parameterTypes", new JArray()))["value"];
                var steps = new JArray(
                    Step("invoke", "target", Parameter("root"), "member", "Append",
                        "args", new JArray(Parameter("text")), "parameterTypes", new JArray("System.String")),
                    Step("get", "target", Parameter("root"), "member", "Length", "kind", "property"));
                var prepareRequest = Step("plan", "action", "prepare", "planId", "append", "steps", steps,
                    "description", "验证通用反射计划行为");
                var prepared = plans.Execute(prepareRequest);
                Check((string)prepared["sessionId"] == SessionId && (int)prepared["stepCount"] == 2,
                    "准备应返回当前会话和步骤数量");
                Check(Length(reflection, root) == 0, "准备不能执行反射或改变对象");
                Check((bool)Run(plans, "prepare", "planId", "append", "steps", steps)["cacheHit"],
                    "相同定义重复准备应复用原计划");
                Expect<InvalidOperationException>(() => Run(plans, "prepare", "planId", "append",
                    "steps", new JArray(Step("types"))), "同名不同定义不得覆盖");
                prepareRequest["steps"][0]["member"] = "Clear";
                var parameters = new JObject { ["root"] = root.DeepClone(), ["text"] = "x" };
                var first = Execute(plans, "append", "first", parameters);
                Check((string)first["state"] == "completed" && !(bool)first["cacheHit"] && Length(reflection, root) == 1,
                    "首次运行应使用准备时的快照并实际执行");
                Check((int)first["completedSteps"] == 2 && (int)first["results"][1]["value"] == 1,
                    "回执应保留每步结果");
                first["results"][1]["value"] = 999;
                var retry = Execute(plans, "append", "first", parameters);
                Check((bool)retry["cacheHit"] && Length(reflection, root) == 1 && (int)retry["results"][1]["value"] == 1,
                    "同编号重试不应重复写入，外部修改回执不得污染缓存");
                Execute(plans, "append", "second", parameters);
                Check(Length(reflection, root) == 2, "新编号必须真正再执行一次");
                var different = (JObject)parameters.DeepClone();
                different["text"] = "y";
                Expect<InvalidOperationException>(() => Execute(plans, "append", "first", different),
                    "同编号不同参数必须拒绝");
                Check(Length(reflection, root) == 2, "冲突参数不能产生额外写入");
                Expect<InvalidOperationException>(() => Run(plans, "run", "planId", "append", "operationId", "wrong-session",
                    "sessionId", "old-session", "parameters", parameters), "旧会话不能执行计划");
                Expect<ArgumentException>(() => Execute(plans, "append", "missing-parameter", new JObject()),
                    "缺少必需参数应在执行前拒绝");
                var extra = (JObject)parameters.DeepClone();
                extra["typo"] = 1;
                Expect<ArgumentException>(() => Execute(plans, "append", "extra-parameter", extra),
                    "拼错的额外参数不能静默忽略");

                Run(plans, "prepare", "planId", "local-values", "steps", new JArray(
                    Step("create", "type", "System.Text.StringBuilder", "args", new JArray(Parameter("prefix")),
                        "parameterTypes", new JArray("System.String")),
                    Step("invoke", "target", Previous(0, "value"), "member", "Append",
                        "args", new JArray(Parameter("suffix")), "parameterTypes", new JArray("System.String")),
                    Step("get", "target", Previous(0, "value"), "member", "Length", "kind", "property")));
                var values1 = Execute(plans, "local-values", "local-1", new JObject { ["prefix"] = "a", ["suffix"] = "b" });
                var values2 = Execute(plans, "local-values", "local-2", new JObject { ["prefix"] = "a", ["suffix"] = "bc" });
                Check((int)values1["results"][2]["value"] == 2 && (int)values2["results"][2]["value"] == 3,
                    "步骤引用应取得本次结果，参数值不能缓存进计划");
                Check(!JToken.DeepEquals(values1["results"][0]["value"], values2["results"][0]["value"]),
                    "不同执行应创建不同对象");

                Run(plans, "prepare", "planId", "partial-failure", "steps", new JArray(
                    Step("invoke", "target", Parameter("root"), "member", "Append", "args", new JArray("!"),
                        "parameterTypes", new JArray("System.String")),
                    Step("invoke", "target", Parameter("root"), "member", "MissingPlanMethod"),
                    Step("invoke", "target", Parameter("root"), "member", "Clear")));
                var failedParameters = new JObject { ["root"] = root.DeepClone() };
                var failed = Execute(plans, "partial-failure", "failed", failedParameters);
                Check((string)failed["state"] == "failed" && (int)failed["completedSteps"] == 1 && (int)failed["failedStep"] == 1,
                    "失败回执应定位失败步骤并停止后续执行");
                Check(Length(reflection, root) == 3, "失败不能伪装成自动回滚或继续执行");
                Check((bool)Execute(plans, "partial-failure", "failed", failedParameters)["cacheHit"] && Length(reflection, root) == 3,
                    "失败编号重试不能重复前面已完成的写入");
                Check((string)Run(plans, "result", "operationId", "failed", "sessionId", SessionId)["state"] == "failed",
                    "查询必须保留原失败结果");

                Expect<ArgumentException>(() => Run(plans, "prepare", "planId", "nested",
                    "steps", new JArray(Step("plan"))), "计划不能递归调用计划");
                Expect<ArgumentException>(() => Run(plans, "prepare", "planId", "forward",
                    "steps", new JArray(Step("get", "target", Previous(0), "member", "Length"))),
                    "步骤引用只能指向已经完成的前序步骤");
                Expect<ArgumentException>(() => Run(plans, "prepare", "planId", "old-handle",
                    "steps", new JArray(Step("get", "target", root, "member", "Length"))),
                    "计划定义不能持有当前对象句柄");
                var listed = Run(plans, "list");
                Check(((JArray)listed["plans"]).Count == 3, "列表应提供已准备的计划入口");
                Run(plans, "release", "planId", "append", "sessionId", SessionId);
                Expect<KeyNotFoundException>(() => Run(plans, "result", "operationId", "first", "sessionId", SessionId),
                    "显式释放计划应同时释放该计划的结果记录");
                plans.Clear();
                Check(((JArray)Run(plans, "list")["plans"]).Count == 0, "停止会话应清空计划");

                CheckLimits(reflection);
                Debug.Log("CLIENTMCP_PLAN_CHECKS_OK count=" + checks);
            }
            finally { plans.Clear(); reflection.Clear(); }
        }

        static void CheckLimits(ReflectionSession reflection)
        {
            var plans = new ReflectionPlanSession(reflection, SessionId);
            var steps = new JArray(Step("get", "type", "UnityEngine.Time", "member", "frameCount", "kind", "property"));
            var tooManySteps = new JArray();
            for (int index = 0; index < 65; index++) tooManySteps.Add(steps[0].DeepClone());
            Expect<ArgumentException>(() => Run(plans, "prepare", "planId", "too-many-steps", "steps", tooManySteps),
                "同帧步骤必须有数量上限");
            for (int index = 0; index < 32; index++)
                Run(plans, "prepare", "planId", "limit-" + index, "steps", steps.DeepClone());
            Expect<InvalidOperationException>(() => Run(plans, "prepare", "planId", "overflow", "steps", steps.DeepClone()),
                "计划容量满时应拒绝，不能静默淘汰");
            for (int index = 0; index < 128; index++)
                Execute(plans, "limit-0", "limit-operation-" + index, new JObject());
            Expect<InvalidOperationException>(() => Execute(plans, "limit-0", "overflow", new JObject()),
                "去重记录容量满时应拒绝，不能淘汰旧编号后重做");
            Check((bool)Execute(plans, "limit-0", "limit-operation-0", new JObject())["cacheHit"],
                "容量满时旧编号仍应返回原结果");
            plans.Clear();
        }

        static JObject Execute(ReflectionPlanSession plans, string planId, string operationId, JObject parameters) =>
            Run(plans, "run", "planId", planId, "operationId", operationId, "sessionId", SessionId, "parameters", parameters);

        static JObject Run(ReflectionPlanSession plans, string action, params object[] fields)
        {
            var request = Step("plan", fields);
            request["action"] = action;
            request["description"] = "验证通用反射计划行为";
            return plans.Execute(request);
        }

        static JObject Step(string operation, params object[] fields)
        {
            var request = new JObject { ["op"] = operation };
            for (int index = 0; index < fields.Length; index += 2)
                request[(string)fields[index]] = fields[index + 1] is JToken token ? token.DeepClone() : JToken.FromObject(fields[index + 1]);
            return request;
        }

        static JObject Parameter(string name) => new JObject { ["$param"] = name };
        static JObject Previous(int index, params object[] path) => new JObject { ["$step"] = index, ["path"] = new JArray(path) };
        static int Length(ReflectionSession reflection, JToken target) => (int)reflection.Execute(
            Step("get", "target", target, "member", "Length", "kind", "property"))["value"];

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        static void Expect<TException>(Action action, string message) where TException : Exception
        {
            try { action(); }
            catch (TException) { checks++; return; }
            throw new InvalidOperationException(message);
        }
    }
}
