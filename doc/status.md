# ClientMCP 当前状态

更新日期：2026-10-17

## Now

- 当前仍处于需求与设计阶段，尚未启动运行时功能或桥接服务端实现。已补充开发阶段的使用目标与消费工程协作方式，见 [设计草案](remote-diagnostics.md)。此前桥接职责与 1:N 路由仍为未实现方案。

- 已明确能力定位为通用反射元操作，不包含消费项目的业务语义。AI 通过外部脚本组合类型与对象定位、成员查看、字段或属性读写、对象构造、实例或静态方法调用，操作持续运行的游戏；至少能在 Codex 中显示变量数据。实际操作不依赖其他编程工具，接口签名与执行边界尚待收敛，功能均未实现。

- 本项目承接能力演进与通用实现，功能完成并自测后交给用户已授权的消费工程实测；消费工程的私有脚本、代码与测试数据留在消费工程。开发期间建议使用同一份 UPM 包源码，不复制维护两套实现。

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

- 收敛通用反射元操作的最小接口，明确对象引用、字段路径、参数与返回值、方法重载，以及访问授权、启用开关和目标平台 / 脚本后端。
- 若后续决定实施，先验证外部脚本对单个运行实例的变量查看、写入、对象构造和方法调用，再交给消费工程定向实测；公网多实例路由后续验证。当前尚未进入编码阶段。

## Blocked

- 工程初始化已无阻塞。功能实现仍需确认目标平台、脚本后端和访问场景。

## 依据与验证

- Unity 6000.5.9f1 批处理编译通过，正常退出码 0。
- `ProjectValidation.Bootstrap` 已生成验证场景，并输出 `CLIENTMCP_VALIDATION_OK`，确认嵌入包注册及 `ClientMcp.Runtime` 在 Player 编译程序集列表中。
- 当前仅验证 Editor 编译、包注册和场景生成；未运行 Play Mode、构建 Player 或验证 IL2CPP。程序集被列入 Player 编译列表不代表 Player 构建已通过。
- 独立消费工程通过 `https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#main` 安装，锁文件来源为 `git`，解析到初始化提交 `433368814c577ce0bb922c7812d221a519a2971e`。
- 消费工程编译通过，输出 `CLIENTMCP_VALIDATION_OK`（`source=Git`、Unity `6000.5.9f1`），正常退出码 0。之后的文档更新不改变已验证的包内容。
- 初始化已推送；未创建版本标签或正式 Release。缓存、原始日志和临时消费工程不纳入公开历史。
