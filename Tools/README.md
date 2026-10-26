# 外部脚本客户端

`clientmcp.py` 只依赖 Python 3 标准库，无需安装 SDK。脚本通过本机 HTTP 接口发送通用反射操作，可以在 Editor 的 Play Mode 或支持的独立运行程序中使用。

## Agent 获取上下文的顺序

1. 优先复用当前上下文或消费工程中已验证的计划、脚本入口；已知计划属于当前会话时直接执行，未知是否有可用计划时才 `list_plans()`，不在每次操作前重复列举。
2. 首次操作且无可复用步骤时，若 Agent 可读取消费工程源码，先用自己的文件工具定向阅读相关定义和必要调用点，确认完整类型名、对象入口、成员路径、参数类型及调用条件。信息足够构造操作即停止，不扫描整个工程。
3. 对源码已明确的结构和签名，直接使用对应 `type`、`member`、`declaringType`、`parameterTypes`，无需例行 `types → inspect`。当前对象、句柄和影响本次操作的状态仍从目标运行实例读取。
4. 已知的连续对象定位和读取可以合并到现有 `plan`；已确认当前会话时，首次用 `execute_plan()` 一次准备并执行，之后用 `run_plan()` 复用。用 `result_steps` 只返回需要的步骤，减少往返和输出。需要根据读取结果判断是否写入时，先完成读取和判断，再发起写入；计划没有条件分支，不会因业务状态不满足而自动跳过后续写入。
5. 源码不可访问、信息不足或与运行版本不匹配时，才对相关类型和成员做最小反射探测；首次验证成功后按下方规则保存步骤。源码读取由 Agent 完成，ClientMCP 不提供源码访问接口。

写入或调用前，通过源码或 `inspect` 明确成员签名和副作用，并检查必要的当前状态。源码可读不表示与运行包完全一致；发现类型、成员或签名不匹配时只核对相关路径，不以反复全量探测作为默认恢复方式。超时或执行结果未知时，仍先沿用原操作编号重试或查回执。

## 连接一个运行实例

运行时必须显式启用 ClientMCP。通过启动参数启用时，建议同时用 `--clientmcp-info <绝对路径>` 指定私有连接文件：

```text
<程序> --clientmcp --clientmcp-info <私有连接文件路径>
```

也可以在消费工程中调用 `ClientMcpRuntime.Start(options)`；具体启用代码见包的使用说明。Editor 中需要进入 Play Mode，反射操作在游戏继续运行时执行。

运行时只监听本机地址，自动分配端口并生成随机 token。连接文件包含 `endpoint`、`token`、`sessionId`，应放在版本控制以外的私有位置。不指定路径时，文件为系统临时目录中的 `clientmcp-<进程ID>.json`。客户端要求明确选择文件，不会猜测或自动选择其他实例。

```powershell
python Tools/clientmcp.py --info <私有连接文件路径> --health
# 已知类型与属性时直接读取，无需先 types/inspect。
'{"op":"get","description":"检查当前游戏速度","type":"UnityEngine.Time","member":"timeScale","kind":"property"}' |
    python Tools/clientmcp.py --info <私有连接文件路径> -
```

还可以显式传入 `--endpoint http://127.0.0.1:<端口> --token <token>`。日常使用连接文件，避免把 token 留在命令历史中。`--timeout` 设置请求超时秒数，默认 10 秒。无参数运行显示帮助；成功输出 `result` JSON，失败在标准错误输出原因并返回退出码 1。

## 在 Python 中组合操作

把 `Tools` 加入脚本模块搜索路径，或把脚本保存在 `clientmcp.py` 同目录，即可直接导入：

```python
from clientmcp import ClientMcp

client = ClientMcp(info="<私有连接文件路径>")
print(client.health())

# 本例已明确类型和属性；未知签名时再定向查源码或 inspect。
# 默认读取字段；属性需要显式 kind="property"，因为 getter 可能执行代码。
before = client.get(type="UnityEngine.Time", member="timeScale", kind="property")
client.set(type="UnityEngine.Time", member="timeScale", kind="property", value=0.75,
           description="临时降低游戏速度，便于观察运行状态")
print(client.get(type="UnityEngine.Time", member="timeScale", kind="property"))
client.set(type="UnityEngine.Time", member="timeScale", kind="property", value=before["value"])

# 创建普通 .NET 对象并显式指定重载；对象引用仅在本次运行会话中有效。
text = client.create("System.Text.StringBuilder", ["hello"], parameter_types=["System.String"])
client.invoke(text, "Append", [" world"], parameter_types=["System.String"])
print(client.invoke(text, "ToString", parameter_types=[]))
client.release(text)
```

以上示例使用通用 Unity/.NET 类型，不要求消费工程提供专用调试接口。`create` 是调用普通 .NET 构造函数；Unity 对象应通过现有 Unity API 的静态方法或已定位实例的方法创建、挂接和销毁。

所有反射请求必须包含字符串 `description`，去首尾空白后长度为 1～200 字符且不含控制字符。它用于解释当前操作，实际执行前会显示在 Unity Console，随后记录成功或失败；状态窗口保留最近完成请求的说明。仅记录主动提供的说明，不自动展开参数或返回值。MCP 的 `clientmcp_reflect` 工具参数同样要求此字段；缺少说明或说明无效时不执行反射。

Python 各反射方法和 `request` 都接受可选的 `description=`。省略时客户端提供“读取成员”等通用中文说明；要说明操作目的，请显式填写。显式传入的空字符串或 `None` 不会被默认说明覆盖，仍交给运行时校验。CLI 原始 JSON 必须自行包含 `description`。

Unity 中可通过 **Tools → ClientMCP → Connection Status** 查看当前 Editor Play 实例的监听入口与请求记录。外部 `health()` 及 MCP `clientmcp_status` 的原结果新增 `connection`，包含分类请求计数、排队数量与最近 16 条请求。健康请求读取快照时会包含自身正在处理的状态；记录不含认证前被拒绝的请求，也不表示有持久在线的 AI 客户端。

`connection.recentRequests` 的每条记录还包含 `durationMs`、`queueMs`、`executionMs` 和 `responseBytes`：分别是运行时接收已解析请求至响应体序列化结束的时间、等待宿主执行的时间、宿主实际执行的时间，以及 UTF-8 JSON 响应体字节数。毫秒使用单调时钟；未入队时 `queueMs=null`，未开始或响应时仍在执行则 `executionMs=null`，不能解释为耗时为零或操作已撤销。无法生成响应体时大小为 `null`；正常空响应为 0。本机发送复用计量时的序列化数据。

这些记录可通过状态接口或 `GetConnectionStatus()` 获取，沿用现有请求计数与最近 16 条上限。`durationMs` 不包含网络收发与 Agent 思考；中转请求的大小指 Runtime 返回的 JSON，不含网关外层封装。请求记录只保存数字和操作摘要，不保存参数或结果。比较同一任务时分别看请求数、这些指标和调用方测得的总等待时间。

所有方法保留服务返回的 `result`，不会把 `get`、`create`、`invoke` 的 `{value, type}` 自动拆开。`value` 中的对象引用格式为 `{"$ref":"o1"}`。实例成员的 `target` 可以传句柄字符串、引用描述，也可以直接传这些方法的返回对象；静态成员传 `type=`。参数中的引用直接使用 `{"$ref":"o1"}`，不要把整个返回对象放进 `args`。

```python
objects = client.find("UnityEngine.Transform", scope="scene", limit=20)
print(objects)

# 从 find 的结果中选取本次目标句柄；已知 name 是属性，可直接读取。
# o1 仅为格式示例，实际应替换成所选目标的当前句柄。
print(client.get("o1", "name", kind="property"))
```

句柄示例 `o1` 仅说明调用格式，实际应使用当前响应中的句柄。

## 操作对应关系

| Python 方法 | 请求字段 | 用途 |
| --- | --- | --- |
| `health()` | `GET /health` | 运行实例、帧数、Unity 版本和 Editor 标识 |
| `types(query="", offset=0, limit=100)` | `op=types` | 查询已加载类型 |
| `find(type, scope="scene", offset=0, limit=100)` | `op=find` | 查找场景对象；`loaded` 包含已加载资源 |
| `inspect(target=None, type=None)` | `op=inspect` | 查看成员元数据，不执行属性 getter |
| `get(target=None, member=None, type=None, kind="field", declaring_type=None)` | `op=get` | 读取明确指定的字段或属性 |
| `set(target=None, member=None, value=None, type=None, kind="field", declaring_type=None)` | `op=set` | 写入明确指定的字段或属性 |
| `create(type, args=None, parameter_types=None)` | `op=create` | 调用构造函数 |
| `invoke(target=None, member=None, args=None, type=None, parameter_types=None, declaring_type=None)` | `op=invoke` | 调用实例或静态方法 |
| `release(targets=None, all=False)` | `op=release` | 释放一个、多个或所有桥接引用 |
| `request(op, **kwargs)` | `POST /reflect` | 使用协议字段名发送原始请求 |

## 优先复用已验证的操作计划

首次操作按上方顺序获取必要上下文，保存验证过的通用反射步骤。后续相同意图已知当前会话入口时直接 `run_plan()`，未知时才 `list_plans()` 查找；只有缺少计划或会话、引用失效时，才加载本地保存的步骤并做必要恢复，不重复读文档、查源码或临时编译辅助类。具体步骤、参数和本地脚本保存在消费工程；插件只认识反射元操作。

缓存的是步骤，不是当前运行值。计划中不能硬编码 `o1` 或 `{"$ref":"o1"}`；通过本次参数或前序步骤引用对象。游戏重启、换局或场景切换后应重新定位所需对象。计划和回执只在当前 Runtime 会话存在，停止接入或域重载后需要重新准备；客户端可以把步骤 JSON 保存在自己的项目里。

- **再执行一次**：复用计划，提供新的 `operation_id`，真正执行新操作。
- **请求超时或查询结果**：使用原编号重试，或调用 `plan_result()`；同编号、同计划与同参数返回原回执，不重复写入。不要在结果未知时自动生成新编号。
- **执行完成**：`state="completed"` 只表示反射步骤已返回；若调用的是宿主已有异步方法，不表示异步任务或演示已经完成。分别说明准备耗时、请求往返和实际业务完成，不能混为一个耗时。

### 准备与执行

以下计划每次创建一个文本对象、追加本次参数、读回长度并释放临时句柄，不依赖 Editor 或动态 C# 编译器。当前会话可来自已选实例的连接文件；没有时先读取一次状态。后续请求复用这个已确认的会话标识：

```python
steps = [
    {"op": "create", "type": "System.Text.StringBuilder", "args": [], "parameterTypes": []},
    {"op": "invoke", "target": {"$step": 0, "path": ["value"]}, "member": "Append",
     "args": [{"$param": "text"}], "parameterTypes": ["System.String"]},
    {"op": "get", "target": {"$step": 0, "path": ["value"]}, "member": "Length", "kind": "property"},
    {"op": "release", "targets": [{"$step": 0, "path": ["value"]}]},
]
session_id = client.session_id or client.health()["sessionId"]
first = client.execute_plan("append-text-v1", "operation-1", steps, session_id=session_id,
                            parameters={"text": "hello"}, result_steps=[2],
                            description="构造文本并检查长度")
assert first["state"] == "completed"
assert first["resultSteps"] == [2]
print(first["results"][0]["value"])  # 所选第 2 步的结果，长度为 5。

# 同编号重试只取旧回执，不再创建对象。
retry = client.run_plan("append-text-v1", "operation-1", session_id=session_id,
                        parameters={"text": "hello"}, result_steps=[],
                        description="确认上次操作结果，避免重复执行")
assert retry["cacheHit"] is True
# 需要排查时再查询完整回执，不会重新执行。
# full = client.plan_result("operation-1", session_id=session_id)

# 新编号代表明确的新执行，使用本次参数生成新对象。
second = client.run_plan("append-text-v1", "operation-2", session_id=session_id,
                         parameters={"text": "world"}, result_steps=[2],
                         description="再次执行文本构造检查")
assert second["state"] == "completed"

# 计划内已释放临时对象句柄；不再需要查询或重试时再释放计划和回执。
client.release_plan("append-text-v1", session_id=session_id)
```

`{"$param":"名称"}` 以本次 `parameters` 的同名值整体替换，参数内容按字面量传递，不再解释其中的占位符。`{"$step":0,"path":["value"]}` 从第 0 步的结果取值；编号从 0 开始，只能引用前序步骤。`path` 可省略，或为字符串与非负整数混合的数组，例如 `["items",0]`，不支持点路径表达式。缺少参数或多传参数都在执行前拒绝。

| Python 方法 | 必需信息 | 用途 |
| --- | --- | --- |
| `prepare_plan(plan_id, steps, description=...)` | 计划标识与步骤 | 只校验、复制定义并返回当前 `sessionId`、参数名和步骤数，不执行 |
| `execute_plan(plan_id, operation_id, steps, session_id=..., parameters=..., result_steps=...)` | 步骤、计划、操作编号与会话 | 一次请求校验、缓存并执行；原编号重试取原回执 |
| `run_plan(plan_id, operation_id, session_id=..., parameters=..., result_steps=...)` | 计划、操作编号与会话 | 一次主线程调用顺序执行，或返回相同编号的原回执 |
| `plan_result(operation_id, session_id=..., result_steps=...)` | 操作编号与会话 | 查询原执行结果，包含失败回执 |
| `list_plans()` | 当前连接 | 列出已准备计划的说明、参数名和步骤数 |
| `release_plan(plan_id, session_id=...)` | 计划与会话 | 删除该计划和对应操作回执，结束其去重保证 |

这些方法都支持 `description=`。原始 HTTP 与 MCP 使用同一对象格式：`op="plan"`，`action` 为 `prepare/execute/run/result/list/release`；Python 的下划线字段对应 `planId`、`operationId`、`sessionId`、`resultSteps`。MCP 仍调用 `clientmcp_reflect`，无需新增业务工具。仅需准备、不应立即执行时仍用 `prepare_plan()`；原 `prepare → run` 调用保持兼容。`execute` 和结果选择需要 Runtime 版本 `0.1.0-preview.7` 或更新版本；中转的工具发现也需更新到同版共享定义。

### 选择返回结果

`execute/run/result` 可提供 `resultSteps`（Python 为 `result_steps`）：省略返回完整 `results`，`[]` 只返回状态和错误，`[2,0]` 按指定顺序返回第 2、0 步。序号从 0 开始，必须为计划范围内不重复的整数；无效选择在执行前拒绝。返回的 `resultSteps` 与 `results` 逐项对应；失败时未执行的所选步骤不返回，`failedStep` 和 `completedSteps` 始终使用原计划序号。

选择只影响响应内容，不影响执行步骤、前序结果引用或对象句柄的生命周期。完整回执仍按原容量保留；同一操作编号可以改变返回范围，也可省略选择查询完整结果，均不会重新执行。这个机制减少响应大小与复制量，不减少回执缓存占用；需要释放的中间句柄应在计划内显式释放，或查询完整回执后处理。

### 执行语义与限制

每个计划含 1～64 步，只能组合现有八种反射操作。所有步骤在一次主线程回调中顺序执行，不主动跨帧；失败停止后续步骤，已经发生的修改保留，**不提供事务回滚**。计划不含定时器、循环或动态代码编译；调用阻塞方法仍会卡住主线程，应保持步骤短时有限。

回执包含 `state`、`cacheHit`、`completedSteps` 与 `results`；失败时额外包含从 0 开始的 `failedStep` 和 `error`。失败回执同样缓存，重试不会重做先前成功的步骤。`/reflect` 成功接收计划请求后可用 `ok=true` 返回 `state="failed"`，Python 保留该回执，调用方必须检查 `state`；MCP 对应返回 `isError=true` 并保留结构化回执。Console 与请求历史也标记失败。

同名计划只能以完全相同的步骤与说明重复准备；`execute` 也遵循这一规则，重试时需保留原步骤与说明，或改用原 `run/result` 查回执。不同定义必须使用新计划名，或明确释放旧计划。同一 `operationId` 在整个会话内唯一，换计划或参数重用会被拒绝。`execute/run/result/release` 必须提供已确认的当前 `sessionId`；`execute` 的会话、定义、参数、返回范围及容量校验通过后才登记新计划并开始执行。

每个会话最多 32 个计划和 128 个操作记录，容量满时拒绝新建，不自动淘汰旧编号。显式释放计划会同时删除其回执，之后原编号不再受到去重保护；请在确认不再重试后释放。计划释放和回执命中都不会销毁对象；结果中的对象句柄仍由原反射会话管理。

`declaring_type` 对应请求中的 `declaringType`，用于明确成员声明类型；`parameter_types` 对应 `parameterTypes`，用于明确方法或构造函数重载。省略 `parameter_types` 时，运行时仅按参数数量选择；有多个同参数数量候选时必须补全类型，不根据 JSON 值猜测重载。实例目标与静态类型只能指定一个。

`release` 仅移除 ClientMCP 持有的引用，不会销毁 Unity 对象，也不会调用 `Dispose`。值类型读取结果是副本；修改副本后需显式写回所属字段或属性。

反射只能访问当前程序实际保留的类型和成员。构建裁剪、AOT 和平台接口支持会影响结果；当前不宣称 IL2CPP 已通过验证。运行时执行的 getter、setter、构造函数和方法具有原代码的副作用，脚本应通过源码或 `inspect` 明确签名和副作用，再按当前实例状态操作。

## 错误与本地契约测试

Python 脚本可统一捕获 `ClientMcpError`，也可以区分 `ClientMcpConnectionError`、`ClientMcpProtocolError` 和 `ClientMcpRemoteError`。远端错误保留 `error_type`、`message`、`http_status`，不会把错误伪装成空数据。

```powershell
python -m unittest discover -s Tools/tests -v
```

这些测试使用本机模拟 HTTP 服务，验证请求、认证错误、异常、JSON 与超时处理；不等同于 Unity 或发布包验证。

## 可选 C# 脚本

仅宿主显式启用且部署 Roslyn 模块后可用；脚本与反射共享认证和句柄。Unity Mono 与 .NET 使用同一协议，IL2CPP 不支持。

```python
client.prepare_script("read-name", 'return Get<UnityEngine.GameObject>((string)Parameters["object"]).name;',
                      description="准备读取对象名称的脚本")
receipt = client.run_script("read-name", "read-name-001", session_id=client.session_id,
                            parameters={"object": object_handle}, description="读取本次对象名称")
client.script_result("read-name-001", session_id=client.session_id)
client.list_scripts()
client.release_script("read-name", session_id=client.session_id)
```

直接 HTTP 使用 `op=script`；MCP 使用 `clientmcp_script`，中转仍须每次带 instanceId。action 为 prepare/run/result/list/release；所有请求提供 description。run 返回 state、cacheHit 及 result；result.value 为标量或对象句柄。新意图使用新 operationId，成功和失败的原编号重试均不重做。释放脚本会结束所属操作的去重保证。

脚本是受信任同进程代码，限定同步短操作，不支持 await/async。超时或断线不表示已经开始的脚本被终止；查原回执后再决定下一步，不直接换编号重做。完整容量、线程和安装规则见[宿主说明](../doc/architecture-and-release.md#双端通用核心与-c-脚本)。
