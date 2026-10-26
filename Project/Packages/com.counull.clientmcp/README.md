# ClientMCP UPM 包

版本：`0.1.0-preview.7`。提供通用反射元操作、可复用操作计划与显式启用的本机和中转接入，不包含消费项目业务语义。

- 最低版本声明：Unity **2022.3**；开发工程：Unity **6000.5.9f1**。已执行的验证以仓库 `doc/status.md` 为准。
- 本包作为源码包交付，由宿主 Unity 工程编译；无需预先生成 DLL。
- 包内运行时代码放在 `Runtime/`，不得依赖宿主工程的 `Assets/` 或 `UnityEditor`。
- 外部脚本组合类型查找、对象定位、成员查看、字段或属性读写、构造及方法调用；不要求宿主注册业务命令。
- JSON 使用 Unity 官方 `com.unity.nuget.newtonsoft-json`；没有外部调试器或动态 C# 编译器依赖。
- IL2CPP 与其他平台需要单独验收，反射可见性取决于实际构建的类型与成员保留。

## 启用

Editor：勾选 **Tools → ClientMCP → Enable For Play Mode** 后进入 Play。该开关只保存在当前 Editor 会话。

打开 **Tools → ClientMCP → Connection Status**，可查看当前 Editor Play 实例的监听状态、MCP 地址、连接文件位置、各入口请求计数，以及最近 16 条已完成请求的操作说明和结果。窗口也可启停接入；操作与菜单共用本次 Editor 会话的启用开关。窗口只读本地状态，不会增加请求计数，不显示令牌。记录仅包含已通过认证并进入处理器的请求；HTTP 按请求通信，监听状态不等于 AI 持续在线。该窗口不连接或监视另一个独立 Player。

Player：启动时传入 `--clientmcp --clientmcp-info <本地连接文件.json>`。默认只绑定 `127.0.0.1` 的随机端口，令牌随机生成并写入本地连接文件；不显式启用时不监听。

宿主也可在主线程调用 `ClientMcpRuntime.Start(new ClientMcpOptions { InfoPath = ... })`，结束时调用 `ClientMcpRuntime.Stop()`。没有指定文件时，连接信息写到系统临时目录的 `clientmcp-<进程ID>.json`。

### 中转连接

宿主可在 `ClientMcpOptions` 中提供 `RelayUrl`（`wss://<主机>/client`）、`RelayToken`、`InstanceName`，或在 Runtime 已启动后调用 `ClientMcpRuntime.ConnectRelay(url, token, instanceName)`。`DisconnectRelay()` 只断开中转，不清理当前反射会话；`Stop()` 结束整个 Runtime。`ws` 只允许 loopback 本地验证，不忽略 TLS 证书错误。

Player 显式带 `--clientmcp` 时，也可从私有环境变量 `CLIENTMCP_RELAY_URL`、`CLIENTMCP_RELAY_TOKEN`、`CLIENTMCP_INSTANCE_NAME` 读取中转配置；令牌不要放入命令行、URL 或版本库。中转断线自动重连，但不自动重发反射请求；同一 Runtime 重连后计划仍然有效。

服务端启动及 MCP 实例选择见仓库[连接说明](https://github.com/Counull/ClientMCP/blob/main/doc/remote-diagnostics.md)。

连接文件是凭据，不应提交或公开。`/reflect` 接收外部脚本的通用 JSON 请求；`/mcp` 支持 MCP 初始化与工具发现/调用；两者均要求 Bearer 令牌。完整脚本客户端与调用说明位于仓库的 `Tools/`。

反射请求必须包含 `description`：去首尾空白后为 1～200 字符且不含控制字符。调用方用它解释当前操作，Unity Console 在实际执行前显示说明，并记录成功或失败；不自动展开调用参数或返回值。MCP 工具参数和原始 JSON 必须填写；Python 方法省略时会提供通用中文说明，也可用 `description=` 明确操作目的。

## 操作计划与复用规则

优先复用当前上下文或消费工程中已验证的计划、脚本入口；已知当前会话计划时直接 `run`，未知是否有可用计划时才 `op=plan, action=list`。首次无可复用步骤且 Agent 可读取消费工程源码时，先定向阅读相关定义和必要调用点，确认类型、对象入口、成员路径、参数类型与调用条件，信息足够即停止；已知结构和签名无需例行 `types/inspect`。源码不可用、信息不足或版本不匹配时才做最小反射探测。

当前对象、句柄和必要状态仍从目标实例读取；已知的连续读取可合并为计划。写入或调用前通过源码或 `inspect` 明确签名和副作用；依赖状态判断的写入应先读取并判断，再执行，计划没有条件分支。首次验证成功后保存步骤；计划缺失或会话、对象失效时只做必要恢复，不重复探测或临时编译辅助类。具体步骤与快捷脚本由消费工程保存，本包只执行通用元操作。

`prepare` 只校验并缓存 1～64 步；首次已知当前 `sessionId` 且步骤确定时，可用 `execute` 携带步骤、计划名、操作编号和参数，一次准备并执行，之后用 `run` 复用。`execute/run` 都在同一主线程回调顺序执行。`{$param:名称}` 使用本次参数，`{$step:序号,path:[路径]}` 引用前序结果，禁止在计划定义中硬编码运行对象句柄。每次新意图用新操作编号，超时重试沿用原编号或调用 `result`；原失败结果也保留，避免重复执行之前已成功的步骤。

`execute/run/result` 可用 `resultSteps` 指定返回步骤，省略返回全部，空数组只返回状态和错误。返回的 `results` 与 `resultSteps` 逐项对应，失败时只包含已完成的所选步骤；完整回执仍保留，改变返回范围不会重新执行。选择不释放中间句柄。`execute` 重用同名计划时要求步骤和说明一致；原 `prepare → run` 保持兼容。

状态接口的 `connection.recentRequests` 新增 `durationMs`、`queueMs`、`executionMs`、`responseBytes`，用于区分运行时处理、排队、执行与响应体大小；未开始或响应时执行未完成的 `executionMs` 为 `null`。这些指标不包含 Agent 思考和网络收发时间，完整口径见使用说明。

计划同帧执行不代表事务回滚；失败会停止后续步骤并返回 `state=failed`。`completed` 仅表示反射步骤已经返回，不表示宿主异步任务完成。没有内置定时器、循环或动态 C# 编译。每个 Runtime 会话最多 32 计划、128 操作记录，容量满时拒绝新增；停止或域重载时清空。显式 `release` 计划会删除其结果记录并结束去重保证，不释放核心对象句柄。

Python 封装、回执格式与完整示例见仓库 [Tools/README.md](https://github.com/Counull/ClientMCP/blob/main/Tools/README.md#优先复用已验证的操作计划)。消费工程的 AI 约定应引用本使用规则，并只补充本项目私有脚本入口。

开发快照安装地址：

```text
https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#main
```

完整说明见 [GitHub 仓库](https://github.com/Counull/ClientMCP)。许可证见包内 `LICENSE.md`。

## 通用核心与可选 C# 脚本

Unity Runtime 是共享核心的宿主 Adapter。纯 .NET 可直接引用本仓库 DotNet 项目；对象查找使用已注册根对象。可选 Roslyn 模块默认关闭，需构建部署并显式提供 ScriptCompiler，或为 Mono Player 添加 --clientmcp-scripts。脚本支持参数、共享句柄和执行回执；执行任意受信任代码，不是沙箱，超时不能强制终止已经开始的脚本，IL2CPP 不支持。

直接创建反射会话的 Unity 使用方须使用 `new ReflectionSession(new UnityReflectionHost())`，无参构造用于 .NET 根对象宿主。完整安装、宿主线程、协议与容量限制见[双端核心说明](https://github.com/Counull/ClientMCP/blob/main/doc/architecture-and-release.md#双端通用核心与-c-脚本)。
