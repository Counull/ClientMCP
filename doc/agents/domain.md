# Domain docs

本项目采用 single-context。Unity 验证工程和 UPM 包属于同一领域，不因存在 Packages 目录而拆成多个上下文。

- 从 doc/README.md → doc/status.md 进入任务，再按主题选择相关领域文档或决策记录；不预读全部历史。
- 根目录 CONTEXT.md 记录稳定的领域术语、职责和不变量；doc/adr/ 记录已确定且值得保留的架构决策。
- 文件缺失时正常继续，不为凑齐结构创建空文件。由 domain-modeling / grill-with-docs 等流程在事实和决策明确后按需创建。
- CONTEXT.md 不复制项目进度；项目级状态属于 doc/status.md，任务状态属于 GitHub Issues。
- 输出沿用 CONTEXT.md 中的术语。缺少术语时说明缺口，不把推测写成已确定事实。
- 与已有 ADR 冲突时明确指出对应记录和重议理由，不静默覆盖。现有工程选型说明见 doc/architecture-and-release.md，新增 ADR 引用它，不复制维护相同事实。
- 所有公开领域文档只包含 ClientMCP 开源项目内容，不包含其他游戏的私有实现、机器路径或凭据。
