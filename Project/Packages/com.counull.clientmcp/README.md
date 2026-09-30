# ClientMCP UPM 包

版本：`0.1.0-preview.1`。当前仅包含包清单与运行时程序集骨架，尚未实现 MCP 连接或诊断能力。

- 开发验证版本：Unity **6000.5.9f1**。
- 本包作为源码包交付，由宿主 Unity 工程编译；无需预先生成 DLL。
- 包内运行时代码放在 `Runtime/`，不得依赖宿主工程的 `Assets/` 或 `UnityEditor`。
- 各游戏的业务数据适配在后续设计确定后接入，不把具体游戏实现放进通用包。
- 尚未验证其他 Unity 版本、IL2CPP 或任何 Player 平台。

开发快照安装地址：

```text
https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#main
```

完整说明见 [GitHub 仓库](https://github.com/Counull/ClientMCP)。许可证见包内 `LICENSE.md`。
