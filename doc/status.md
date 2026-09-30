# ClientMCP 当前状态

更新日期：2026-09-30

## Now

- 当前仅讨论需求与设计，是否编码另行决定。桥接服务端职责、1:N 路由和打包验证顺序已补入 [设计草案](remote-diagnostics.md)，尚未创建服务端或执行验证。

- 已明确通用反射调试目标：尽量一次实现、持续复用，接口数量不随业务功能数量增长；对象定位、结构查看、字段读取与显式写入的接口划分仍处于讨论阶段，见 [设计草案](remote-diagnostics.md)。

- 设计讨论与说明统一放在 `doc/`。当前准则为“如非必要，勿增实体”：只保留最小实现所需内容，不维护备选方案或提前增加组件与抽象。

- 已记录客户端主动连接公网服务端、开发者通过标准 MCP 接入的方案方向；术语与未定选型见 [远程诊断连接草案](remote-diagnostics.md)。尚未实现或验证远程连接。

- 已使用 Unity 6000.5.9f1 创建 `Project/` 开发验证工程，并生成 `DiagnosticsSandbox` 场景。
- UPM 包位于 `Project/Packages/com.counull.clientmcp/`，版本 `0.1.0-preview.1`；当前仅为程序集与包结构骨架，尚未实现 MCP 接口。
- 整个工程与文档已发布至 [Counull/ClientMCP](https://github.com/Counull/ClientMCP) 公开仓库，默认分支为 `main`。采用 MIT 许可证。
- 已用独立消费工程验证 HTTPS Git URL 子目录安装，无需手动复制包或源码。
- Git URL 安装、版本标签及源码维护方式见 [工程结构与 UPM 发布流程](architecture-and-release.md)。
- 已配置 Matt Pocock 工程技能：任务与规格使用 GitHub Issues，采用五个默认分类标签和 single-context 领域文档布局。配置入口见 [任务管理](agents/issue-tracker.md)、[标签映射](agents/triage-labels.md)、[领域文档](agents/domain.md)。
- 本次仅完成仓库内技能配置，未创建远端标签或业务任务。

## Next

- 继续讨论通用反射的对象定位、字段路径和读写接口，确定目标平台与 Mono/IL2CPP。
- 若后续决定实施，先验证单个 Unity 打包程序的本机闭环，再验证公网多实例路由；当前不启动编码。

## Blocked

- 工程初始化已无阻塞。功能实现仍需确认目标平台、脚本后端和访问场景。

## 依据与验证

- Unity 6000.5.9f1 批处理编译通过，正常退出码 0。
- `ProjectValidation.Bootstrap` 已生成验证场景，并输出 `CLIENTMCP_VALIDATION_OK`，确认嵌入包注册及 `ClientMcp.Runtime` 在 Player 编译程序集列表中。
- 当前仅验证 Editor 编译、包注册和场景生成；未运行 Play Mode、构建 Player 或验证 IL2CPP。程序集被列入 Player 编译列表不代表 Player 构建已通过。
- 独立消费工程通过 `https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#main` 安装，锁文件来源为 `git`，解析到初始化提交 `433368814c577ce0bb922c7812d221a519a2971e`。
- 消费工程编译通过，输出 `CLIENTMCP_VALIDATION_OK`（`source=Git`、Unity `6000.5.9f1`），正常退出码 0。之后的文档更新不改变已验证的包内容。
- 初始化已推送；未创建版本标签或正式 Release。缓存、原始日志和临时消费工程不纳入公开历史。
