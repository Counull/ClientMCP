# ClientMCP

![ClientMCP](doc/images/clientmcp-banner.png)

面向 Unity 游戏运行时的 AI 调试工具，旨在通过 MCP 向 AI 提供日志、对象状态与诊断信息。

> 项目处于早期开发阶段，当前仅提供 UPM 包骨架，尚未实现 MCP 连接与诊断功能。

## 安装

在 Unity Package Manager 中选择 **Install package from git URL**，输入：

```text
https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#main
```

该地址跟随开发分支，目前没有稳定版本。

## 环境要求

- Unity **6000.5.9f1**：已验证包安装与 Editor 编译。
- Git：需已安装并加入 PATH。
- 其他 Unity 版本、Player 构建与 IL2CPP 尚未验证。

开发进展见 [项目文档](doc/README.md)。

## 许可证

项目采用 [MIT](LICENSE) 许可证。第三方依赖遵守各自许可证。
