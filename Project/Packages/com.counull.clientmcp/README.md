# ClientMCP UPM 包

版本：`0.1.0-preview.2`。提供通用反射元操作与显式启用的本机接入，不包含消费项目业务语义。

- 最低版本声明：Unity **2022.3**；开发工程：Unity **6000.5.9f1**。已执行的验证以仓库 `doc/status.md` 为准。
- 本包作为源码包交付，由宿主 Unity 工程编译；无需预先生成 DLL。
- 包内运行时代码放在 `Runtime/`，不得依赖宿主工程的 `Assets/` 或 `UnityEditor`。
- 外部脚本组合类型查找、对象定位、成员查看、字段或属性读写、构造及方法调用；不要求宿主注册业务命令。
- JSON 使用 Unity 官方 `com.unity.nuget.newtonsoft-json`；没有外部调试器或动态 C# 编译器依赖。
- IL2CPP 与其他平台需要单独验收，反射可见性取决于实际构建的类型与成员保留。

## 启用

Editor：勾选 **Tools → ClientMCP → Enable For Play Mode** 后进入 Play。该开关只保存在当前 Editor 会话。

Player：启动时传入 `--clientmcp --clientmcp-info <本地连接文件.json>`。默认只绑定 `127.0.0.1` 的随机端口，令牌随机生成并写入本地连接文件；不显式启用时不监听。

宿主也可在主线程调用 `ClientMcpRuntime.Start(new ClientMcpOptions { InfoPath = ... })`，结束时调用 `ClientMcpRuntime.Stop()`。没有指定文件时，连接信息写到系统临时目录的 `clientmcp-<进程ID>.json`。

连接文件是凭据，不应提交或公开。`/reflect` 接收外部脚本的通用 JSON 请求；`/mcp` 支持 MCP 初始化与工具发现/调用；两者均要求 Bearer 令牌。完整脚本客户端与调用说明位于仓库的 `Tools/`。

开发快照安装地址：

```text
https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#main
```

完整说明见 [GitHub 仓库](https://github.com/Counull/ClientMCP)。许可证见包内 `LICENSE.md`。
