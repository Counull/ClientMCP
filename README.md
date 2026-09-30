# ClientMCP

面向 Unity 已发布程序的 AI/MCP 诊断能力探索。使用 C#，以 UPM 源码包复用，以 Unity 工程开发和验证。

> 当前是工程与包的初始化骨架，尚不提供 MCP 服务、网络连接或运行时反射查询。不要将成功安装包理解为已经能够调试游戏。

## 一个仓库，两种使用方式

- 开发本项目：克隆整个仓库，用 **Unity 6000.5.9f1** 打开 `Project/`。
- 在其他游戏中接入：通过 Unity Package Manager 的 **Install package from git URL** 安装仓库里的 UPM 子目录。

```text
ClientMCP/
├─ doc/                                      # 设计、状态与发布流程
└─ Project/                                  # Unity 开发和验证工程
   ├─ Assets/                               # 验证场景
   ├─ Packages/
   │  └─ com.counull.clientmcp/              # 唯一一份插件源码
   └─ ProjectSettings/
```

开发快照安装地址（分支可变，不作为稳定版本）：

```text
https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#main
```

版本发布后使用标签，例如以下地址须等对应标签创建并推送后才能使用：

```text
https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#v0.1.0
```

本次不发布正式版本标签。首次包版本为 `0.1.0-preview.1`，仅表示初始化阶段。

已用独立 Unity 6000.5.9f1 工程验证上述 `#main` Git URL 安装、包注册和编译通过；尚未验证 Player 构建、IL2CPP 或实际诊断功能。

## 文档

- [文档入口](doc/README.md)
- [当前状态与验证范围](doc/status.md)
- [工程结构与 Git URL 发布流程](doc/architecture-and-release.md)

## 许可证

项目采用 [MIT](LICENSE) 许可证。第三方依赖遵守各自许可证。
