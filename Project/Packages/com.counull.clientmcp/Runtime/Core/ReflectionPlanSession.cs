using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClientMcp
{
    /// <summary>
    /// 在同一运行会话中缓存通用反射步骤，并以 operationId 去重实际执行。
    /// 所有入口仅在主线程调用；步骤顺序执行、不让出主线程，也不回滚已发生的修改。
    /// </summary>
    public sealed class ReflectionPlanSession
    {
        private const int MaxPlans = 32;
        private const int MaxOperations = 128;
        private const int MaxSteps = 64;
        private static readonly HashSet<string> ReflectionOperations = new HashSet<string>(StringComparer.Ordinal)
        {
            "types", "find", "inspect", "get", "set", "create", "invoke", "release"
        };

        private readonly ReflectionSession _reflection;
        private readonly string _sessionId;
        private readonly Dictionary<string, Plan> _plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        private readonly Dictionary<string, Operation> _operations = new Dictionary<string, Operation>(StringComparer.Ordinal);

        public ReflectionPlanSession(ReflectionSession reflection, string sessionId)
        {
            _reflection = reflection ?? throw new ArgumentNullException(nameof(reflection));
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("sessionId 不能为空。", nameof(sessionId));
            _sessionId = sessionId;
        }

        /// <summary>prepare 只缓存定义；execute 准备并执行，run 复用已有计划。</summary>
        public JObject Execute(JObject request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (RequiredString(request, "op") != "plan")
                throw new ArgumentException("反射计划入口只接受 op: plan。");
            switch (RequiredString(request, "action"))
            {
                case "prepare": return Prepare(request);
                case "run": return Run(request);
                case "execute": return Run(request, true);
                case "result": return Result(request);
                case "list": return new JObject
                {
                    ["sessionId"] = _sessionId,
                    ["plans"] = new JArray(_plans.Values.Select(plan => Describe(plan, false)))
                };
                case "release": return Release(request);
                default: throw new ArgumentException("未知计划 action；可用值：prepare、execute、run、result、list、release。");
            }
        }

        /// <summary>显式清空会结束计划与操作号的去重保证；不释放核心对象句柄。</summary>
        public void Clear()
        {
            _plans.Clear();
            _operations.Clear();
        }

        private JObject Prepare(JObject request)
        {
            var plan = ReadPlan(request);
            var cached = _plans.ContainsKey(plan.Id);
            if (!cached) _plans.Add(plan.Id, plan);
            return Describe(plan, cached);
        }

        // 校验定义但暂不登记；execute 的参数、返回范围和操作号也通过后才保留计划。
        private Plan ReadPlan(JObject request)
        {
            var planId = ReadId(request, "planId");
            if (request["description"]?.Type != JTokenType.String)
                throw new ArgumentException("description 必须是字符串。");
            var description = (string)request["description"];
            if (!(request["steps"] is JArray steps) || steps.Count < 1 || steps.Count > MaxSteps)
                throw new ArgumentException("steps 必须是包含 1 到 64 项的数组。");
            var parameters = new List<string>();
            for (var index = 0; index < steps.Count; index++)
            {
                if (!(steps[index] is JObject step) || !ReflectionOperations.Contains(RequiredString(step, "op")))
                    throw new ArgumentException("每个步骤必须是已有八种反射操作之一，不能调用其他计划。");
                ValidateTemplate(step, index, parameters);
                ValidateReferenceSlots(step);
            }
            var definition = new JObject { ["description"] = description, ["steps"] = steps.DeepClone() };
            if (_plans.TryGetValue(planId, out var existing))
            {
                if (!JToken.DeepEquals(existing.Definition, definition))
                    throw new InvalidOperationException("planId 已存在且定义不同；请先显式 release，或使用新的 planId。");
                return existing;
            }
            if (_plans.Count >= MaxPlans)
                throw new InvalidOperationException("计划数量已达到 32；请显式 release 不再使用的计划。");
            return new Plan(planId, definition, parameters.ToArray());
        }

        private JObject Run(JObject request, bool prepare = false)
        {
            RequireSession(request);
            var planId = ReadId(request, "planId");
            var operationId = ReadId(request, "operationId");
            var plan = prepare ? ReadPlan(request) : FindPlan(planId);
            var resultSteps = ReadResultSteps(request, ((JArray)plan.Definition["steps"]).Count);
            if (request["parameters"] != null && !(request["parameters"] is JObject))
                throw new ArgumentException("parameters 必须是 JSON 对象。");
            var parameters = (JObject)(request["parameters"]?.DeepClone() ?? new JObject());
            foreach (var name in plan.Parameters)
            {
                if (parameters.Property(name) == null)
                    throw new ArgumentException("缺少计划参数：" + name);
            }
            foreach (var property in parameters.Properties())
            {
                if (!plan.Parameters.Contains(property.Name, StringComparer.Ordinal))
                    throw new ArgumentException("未知计划参数：" + property.Name);
            }
            if (_operations.TryGetValue(operationId, out var previous))
            {
                if (previous.PlanId != planId || !JToken.DeepEquals(previous.Parameters, parameters))
                    throw new InvalidOperationException("operationId 已用于不同计划或参数，拒绝重复执行。");
                return Receipt(previous, true, resultSteps);
            }
            if (_operations.Count >= MaxOperations)
                throw new InvalidOperationException("操作记录已达到 128；请显式 release 计划及其记录后再执行。");
            if (prepare && !_plans.ContainsKey(planId)) _plans.Add(planId, plan);

            // 首步前保留操作号。即使中途失败，重试相同操作号也只返回收据，绝不重做前面的写入。
            var results = new JArray();
            var receipt = new JObject
            {
                ["sessionId"] = _sessionId, ["planId"] = planId, ["operationId"] = operationId,
                ["state"] = "failed", ["cacheHit"] = false, ["completedSteps"] = 0,
                ["results"] = results, ["failedStep"] = 0,
                ["error"] = new JObject { ["type"] = "System.InvalidOperationException", ["message"] = "执行尚未完成，禁止重复执行此操作号。" }
            };
            var operation = new Operation(planId, parameters, receipt);
            _operations.Add(operationId, operation);
            var steps = (JArray)plan.Definition["steps"];
            for (var index = 0; index < steps.Count; index++)
            {
                try
                {
                    var command = (JObject)Expand(steps[index], parameters, results);
                    results.Add(_reflection.Execute(command).DeepClone());
                    receipt["completedSteps"] = results.Count;
                }
                catch (Exception exception)
                {
                    var cause = exception.GetBaseException();
                    receipt["failedStep"] = index;
                    receipt["error"] = new JObject { ["type"] = cause.GetType().FullName, ["message"] = cause.Message };
                    return Receipt(operation, false, resultSteps);
                }
            }
            receipt["state"] = "completed";
            receipt.Remove("failedStep");
            receipt.Remove("error");
            return Receipt(operation, false, resultSteps);
        }

        private JObject Result(JObject request)
        {
            RequireSession(request);
            if (!_operations.TryGetValue(ReadId(request, "operationId"), out var operation))
                throw new KeyNotFoundException("没有该 operationId 的操作记录。");
            var plan = FindPlan(operation.PlanId);
            return Receipt(operation, true, ReadResultSteps(request, ((JArray)plan.Definition["steps"]).Count));
        }

        private JObject Release(JObject request)
        {
            RequireSession(request);
            var planId = ReadId(request, "planId");
            var released = _plans.Remove(planId);
            var operationIds = _operations.Where(pair => pair.Value.PlanId == planId).Select(pair => pair.Key).ToArray();
            foreach (var operationId in operationIds)
                _operations.Remove(operationId);
            // 这里只删除计划和收据。对象仍由核心句柄会话持有，其销毁或 release 必须另行明确请求。
            return new JObject { ["released"] = released, ["removedOperations"] = operationIds.Length };
        }

        private static void ValidateTemplate(JToken token, int stepIndex, List<string> parameters)
        {
            if (token is JObject data)
            {
                if (data.Property("$ref") != null)
                    throw new ArgumentException("计划定义不能硬编码 $ref；请使用 $param 或前序 $step。");
                if (data.Property("$param") != null)
                {
                    if (data.Count != 1)
                        throw new ArgumentException("$param 占位符只能包含 $param 一个字段。");
                    var name = RequiredString(data, "$param");
                    if (!parameters.Contains(name, StringComparer.Ordinal))
                        parameters.Add(name);
                    return;
                }
                if (data.Property("$step") != null)
                {
                    if (data.Properties().Any(property => property.Name != "$step" && property.Name != "path"))
                        throw new ArgumentException("$step 占位符只能包含 $step 与可选 path。");
                    var index = NonNegativeIndex(data["$step"], "$step");
                    if (index >= stepIndex)
                        throw new ArgumentException("$step 必须引用已经执行完毕的前序步骤。");
                    if (data.Property("path") != null)
                    {
                        if (!(data["path"] is JArray path))
                            throw new ArgumentException("path 必须是由字符串或非负整数组成的数组。");
                        foreach (var part in path)
                        {
                            if (part.Type != JTokenType.String)
                                NonNegativeIndex(part, "path 下标");
                        }
                    }
                    return;
                }
                foreach (var property in data.Properties())
                    ValidateTemplate(property.Value, stepIndex, parameters);
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                    ValidateTemplate(item, stepIndex, parameters);
            }
        }

        private static void ValidateReferenceSlots(JObject step)
        {
            if (step.Property("target") != null && !IsPlaceholder(step["target"]))
                throw new ArgumentException("target 必须使用 $param 或前序 $step，不能硬编码对象句柄。");
            if (step.Property("targets") == null || IsPlaceholder(step["targets"]))
                return;
            if (!(step["targets"] is JArray targets) || targets.Any(target => !IsPlaceholder(target)))
                throw new ArgumentException("targets 必须是占位符或占位符数组，不能硬编码对象句柄。");
        }

        private static bool IsPlaceholder(JToken token)
        {
            return token is JObject data && (data.Property("$param") != null || data.Property("$step") != null);
        }

        private static JToken Expand(JToken token, JObject parameters, JArray results)
        {
            if (token is JObject data)
            {
                // 参数已经是本次调用的字面量；复制后立即返回，绝不把参数内容再次解释为计划表达式。
                if (data.Property("$param") != null)
                    return parameters[(string)data["$param"]].DeepClone();
                if (data.Property("$step") != null)
                {
                    JToken value = results[(int)data["$step"]];
                    foreach (var part in data["path"] as JArray ?? new JArray())
                    {
                        if (part.Type == JTokenType.String && value is JObject item && item.TryGetValue((string)part, out var member))
                            value = member;
                        else if (part.Type == JTokenType.Integer && value is JArray array && (int)part < array.Count)
                            value = array[(int)part];
                        else
                            throw new ArgumentException("$step 的 path 无法在前序结果中定位成员或数组元素。");
                    }
                    return value.DeepClone();
                }
                var expanded = new JObject();
                foreach (var property in data.Properties())
                    expanded[property.Name] = Expand(property.Value, parameters, results);
                return expanded;
            }
            if (token is JArray values)
                return new JArray(values.Select(value => Expand(value, parameters, results)));
            return token.DeepClone();
        }

        private JObject Describe(Plan plan, bool cacheHit)
        {
            return new JObject
            {
                ["sessionId"] = _sessionId, ["planId"] = plan.Id,
                ["description"] = plan.Definition["description"].DeepClone(),
                ["stepCount"] = ((JArray)plan.Definition["steps"]).Count,
                ["parameters"] = new JArray(plan.Parameters), ["cacheHit"] = cacheHit
            };
        }

        private static int[] ReadResultSteps(JObject request, int stepCount)
        {
            if (request.Property("resultSteps") == null) return null;
            if (!(request["resultSteps"] is JArray values) || values.Count > MaxSteps)
                throw new ArgumentException("resultSteps 必须是最多 64 个步骤序号的数组；省略返回全部，空数组只返回状态。");
            var indices = new int[values.Count];
            var seen = new HashSet<int>();
            for (var index = 0; index < values.Count; index++)
            {
                var step = NonNegativeIndex(values[index], "resultSteps 序号");
                if (step >= stepCount || !seen.Add(step))
                    throw new ArgumentException("resultSteps 序号必须在计划范围内且不能重复。");
                indices[index] = step;
            }
            return indices;
        }

        private static JObject Receipt(Operation operation, bool cacheHit, int[] resultSteps)
        {
            // 完整结果仍保留供步骤引用和后续查询；仅复制调用方需要的结果，避免先全量复制再裁剪。
            var result = new JObject();
            foreach (var property in operation.Receipt.Properties())
                if (property.Name != "results") result[property.Name] = property.Value.DeepClone();
            var results = (JArray)operation.Receipt["results"];
            if (resultSteps == null) result["results"] = results.DeepClone();
            else
            {
                var returned = new JArray();
                var indices = new JArray();
                foreach (var step in resultSteps)
                {
                    // 失败时尚未产生的结果不返回；failedStep 与 completedSteps 保留原步骤序号。
                    if (step >= results.Count) continue;
                    indices.Add(step);
                    returned.Add(results[step].DeepClone());
                }
                result["results"] = returned;
                result["resultSteps"] = indices;
            }
            result["cacheHit"] = cacheHit;
            return result;
        }

        private Plan FindPlan(string planId)
        {
            if (!_plans.TryGetValue(planId, out var plan))
                throw new KeyNotFoundException("未找到 planId；请先 prepare。");
            return plan;
        }

        private void RequireSession(JObject request)
        {
            if (RequiredString(request, "sessionId") != _sessionId)
                throw new InvalidOperationException("sessionId 与当前运行会话不一致，拒绝执行。");
        }

        private static string RequiredString(JObject data, string name)
        {
            if (data[name]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)data[name]))
                throw new ArgumentException(name + " 必须是非空字符串。");
            return (string)data[name];
        }

        private static string ReadId(JObject data, string name)
        {
            var raw = RequiredString(data, name);
            var id = raw.Trim();
            if (id.Length > 128 || raw.Any(char.IsControl))
                throw new ArgumentException(name + " 必须是 1 到 128 个可读字符，不能含控制字符。");
            return id;
        }

        private static int NonNegativeIndex(JToken token, string name)
        {
            if (token?.Type != JTokenType.Integer || !int.TryParse(token.ToString(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var index))
                throw new ArgumentException(name + " 必须是不超过 Int32 上限的非负整数。");
            return index;
        }

        private sealed class Plan
        {
            public readonly string Id;
            public readonly JObject Definition;
            public readonly string[] Parameters;
            public Plan(string id, JObject definition, string[] parameters)
            {
                Id = id; Definition = definition; Parameters = parameters;
            }
        }

        private sealed class Operation
        {
            public readonly string PlanId;
            public readonly JObject Parameters;
            public readonly JObject Receipt;
            public Operation(string planId, JObject parameters, JObject receipt)
            {
                PlanId = planId; Parameters = parameters; Receipt = receipt;
            }
        }
    }
}
