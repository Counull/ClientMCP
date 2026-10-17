# ClientMCP UPM 包

版本：`0.1.0-preview.4`。提供通用反射元操作、可复用操作计划与显式启用的本机接入，不包含消费项目业务语义。

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

连接文件是凭据，不应提交或公开。`/reflect` 接收外部脚本的通用 JSON 请求；`/mcp` 支持 MCP 初始化与工具发现/调用；两者均要求 Bearer 令牌。完整脚本客户端与调用说明位于仓库的 `Tools/`。

反射请求必须包含 `description`：去首尾空白后为 1～200 字符且不含控制字符。调用方用它解释当前操作，Unity Console 在实际执行前显示说明，并记录成功或失败；不自动展开调用参数或返回值。MCP 工具参数和原始 JSON 必须填写；Python 方法省略时会提供通用中文说明，也可用 `description=` 明确操作目的。

## 操作计划与复用规则

首次探测完成后保存验证过的反射步骤。后续相同意图先 `op=plan, action=list` 查已准备入口，再以 `run` 复用；计划缺失或会话、对象失效时才做必要恢复，不重复探测或临时编译辅助类。具体步骤与快捷脚本由消费工程保存，本包只执行通用元操作。

`prepare` 只校验并缓存 1～64 步；`run` 传入当前 `sessionId`、`operationId` 和本次参数，同一主线程回调顺序执行。`{$param:名称}` 使用本次参数，`{$step:序号,path:[路径]}` 引用前序结果，禁止在计划定义中硬编码运行对象句柄。每次新意图用新操作编号，超时重试沿用原编号或调用 `result`；原失败结果也保留，避免重复执行之前已成功的步骤。

计划同帧执行不代表事务回滚；失败会停止后续步骤并返回 `state=failed`。`completed` 仅表示反射步骤已经返回，不表示宿主异步任务完成。没有内置定时器、循环或动态 C# 编译。每个 Runtime 会话最多 32 计划、128 操作记录，容量满时拒绝新增；停止或域重载时清空。显式 `release` 计划会删除其结果记录并结束去重保证，不释放核心对象句柄。

Python 五个封装、回执格式与完整示例见仓库 [Tools/README.md](https://github.com/Counull/ClientMCP/blob/main/Tools/README.md#优先复用已验证的操作计划)。消费工程的 AI 约定应引用本使用规则，并只补充本项目私有脚本入口。

开发快照安装地址：

```text
https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#main
```

完整说明见 [GitHub 仓库](https://github.com/Counull/ClientMCP)。许可证见包内 `LICENSE.md`。
