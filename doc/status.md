# ClientMCP 当前状态

更新日期：2026-09-30

## Now

- 已使用 Unity 6000.5.9f1 创建 `Project/` 开发验证工程，并生成 `DiagnosticsSandbox` 场景。
- UPM 包位于 `Project/Packages/com.counull.clientmcp/`，版本 `0.1.0-preview.1`；当前仅为程序集与包结构骨架，尚未实现 MCP 接口。
- 整个工程与文档使用一个 Git 仓库管理，计划发布至 `Counull/ClientMCP` 公开仓库。采用 MIT 许可证。
- Git URL 安装、版本标签及源码维护方式见 [工程结构与 UPM 发布流程](architecture-and-release.md)。

## Next

- 完成公开仓库首次推送，并使用独立消费工程验证远程 Git URL 安装。
- 确定首个诊断场景、目标平台及 Mono/IL2CPP，再评估官方 Unity CLI/Pipeline 与自研运行时接口的分工。
- 明确首批只读诊断工具及反射访问边界，再开始功能实现。

## Blocked

- 工程初始化已无阻塞。功能实现仍需确认目标平台、脚本后端和访问场景。

## 依据与验证

- Unity 6000.5.9f1 批处理编译通过，正常退出码 0。
- `ProjectValidation.Bootstrap` 已生成验证场景，并输出 `CLIENTMCP_VALIDATION_OK`，确认嵌入包注册及 `ClientMcp.Runtime` 在 Player 编译程序集列表中。
- 当前仅验证 Editor 编译、包注册和场景生成；未运行 Play Mode、构建 Player 或验证 IL2CPP。程序集被列入 Player 编译列表不代表 Player 构建已通过。
- 尚未验证远程 Git URL 消费安装；结果完成后更新本页。
