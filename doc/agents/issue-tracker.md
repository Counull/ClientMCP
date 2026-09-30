# Issue tracker: GitHub

任务与规格的权威位置是 [Counull/ClientMCP Issues](https://github.com/Counull/ClientMCP/issues)。

- 使用已连接的 GitHub 工具，或可用且已登录的 gh CLI；先核对目标仓库。不得因缺少 gh 改用另一套任务存储。
- 阅读任务时同时读取正文、标签、评论和依赖；区分 issue number 与数据库 ID。
- 创建、修改或关闭任务须在用户授权或明确调用的技能范围内进行；初始化配置不代表授权自动发送评论或批量创建任务。
- 标题、正文和评论使用简体中文，标识符保留原文。多行内容使用结构化参数；使用 gh 时通过 --body-file 传递文件。
- 技能所说的 publish to the issue tracker 表示创建 GitHub Issue；fetch the relevant ticket 表示读取对应 Issue 及评论。
- PRs as a request surface: no. 不将外部 PR 自动作为待分流需求。
- 使用原生子任务与阻塞依赖记录拆分关系；工具不支持时用任务清单、Part of #编号 和 Blocked by: #编号 明确链接，不能只在聊天里记录依赖。
- wayfinder 流程在实际启用时才建立 map Issue、子任务及相关标签；本次不创建占位任务。
- 每项任务的执行状态由对应 Issue 持有；doc/status.md 只记录项目级 Now、Next、Blocked，并链接相关 Issue，不复制整份任务清单。
- .scratch/ 用于本地临时证据和草稿，不作为任务数据库，不上传包含私人信息的原始日志。
