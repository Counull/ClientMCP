# ClientMCP 当前状态

更新日期：2026-10-17

## Now

- [首版实现任务 #1](https://github.com/Counull/ClientMCP/issues/1)的功能与首轮验收已完成：通用反射元操作、本机 HTTP / MCP 接入和外部脚本客户端已实现，并在真实消费工程的 Editor Play 与 Windows Mono Player 中完成不暂停的对象注入、自然移动和显示验证。中转与 1:N 路由已添加，公网尚未部署，职责与接入方式见 [连接说明](remote-diagnostics.md)。

- 包提供类型与对象定位、成员元数据、字段或属性读写、普通对象构造、实例或静态方法调用、对象句柄与释放，以及复用这些元操作的通用计划，不包含消费项目语义。格式及限制见 [外部脚本说明](../Tools/README.md)。

- 本项目承接能力演进与通用实现，功能完成并自测后交给用户已授权的消费工程实测；消费工程的私有脚本、代码与测试数据留在消费工程。开发期间建议使用同一份 UPM 包源码，不复制维护两套实现。

- 设计讨论与说明统一放在 `doc/`。当前准则为“如非必要，勿增实体”：只保留最小实现所需内容，不维护备选方案或提前增加组件与抽象。

- 首版本机入口直接位于 Unity Runtime：默认关闭、显式启用、随机 loopback 端口与 Bearer 令牌。脚本 `/reflect` 与标准 MCP `/mcp` 共用主线程反射执行器。接入后的操作不依赖 Editor MCP、Rider 或动态 C# 编译器。

- `Server/` 提供最小 ASP.NET Core 中转，Unity 显式启用 WSS 出站连接，开发者通过 HTTP(S) MCP 列出实例，每次明确传 `instanceId`。客户端与开发者令牌分离，首版为同一运营者管理全部接入实例。两种传输共用 Runtime 反射队列与计划；心跳、连接/请求上限、断线清理和自动重连已实现，不自动重发写入。公网 TLS 部署、代理链路和其他 Unity 平台仍须实测。

- Unity 菜单 **Tools → ClientMCP → Connection Status** 提供当前 Editor Play 实例的启停与状态查看：监听入口、连接文件位置、分类请求计数、最近 16 条已完成请求的说明和结果；只统计已通过认证的实际访问，不显示令牌，不推断 AI 持续在线。外部 `/health` 与 `clientmcp_status` 同步提供 `connection` 快照。反射操作新增必填 `description`，Unity Console 记录执行说明和成功或失败，不自动展开参数或返回值；Python 方法省略时提供通用说明，原始 JSON / MCP 调用需要显式填写。

- 通用操作计划已实现并通过独立验证：`prepare/run/result/list/release` 保存有限步骤、绑定本次参数和前序结果、同帧顺序执行、缓存成功或失败回执；新编号真正再执行，原编号重试不重复写入。缓存限当前会话，容量满时拒绝新建，显式释放会结束去重保证；没有自动回滚或定时调度。AI 优先复用规则同时进入使用说明与 MCP 提示；Console 对缓存回执明确标注“返回原回执，未重复执行”。具体业务计划与验收记录留在消费工程。

- 已使用 Unity 6000.5.9f1 创建 `Project/` 开发验证工程，并生成 `DiagnosticsSandbox` 场景。
- UPM 包位于 `Project/Packages/com.counull.clientmcp/`，版本 `0.1.0-preview.5`；最低版本声明为 Unity 2022.3，依赖 Unity 官方 Newtonsoft Json。开发工程仍使用 Unity 6000.5.9f1。
- 整个工程与文档已发布至 [Counull/ClientMCP](https://github.com/Counull/ClientMCP) 公开仓库，默认分支为 `main`。采用 MIT 许可证。
- 已用独立消费工程验证 HTTPS Git URL 子目录安装，无需手动复制包或源码。
- Git URL 安装、版本标签及源码维护方式见 [工程结构与 UPM 发布流程](architecture-and-release.md)。
- 已配置 Matt Pocock 工程技能：任务与规格使用 GitHub Issues，采用五个默认分类标签和 single-context 领域文档布局。配置入口见 [任务管理](agents/issue-tracker.md)、[标签映射](agents/triage-labels.md)、[领域文档](agents/domain.md)。
- 本次仅完成仓库内技能配置，未创建远端标签或业务任务。

## Next

- 按消费工程的后续使用反馈演进通用能力；具体业务脚本和证据继续留在消费工程。
- 按需要部署公网中转，单独验证真实 HTTPS/WSS 证书与网络、双实例路由及重连；当前未部署。
- 其他平台与 IL2CPP 裁剪后续按实际需求验证。

## Blocked

- 首轮功能实现与验收无阻塞。其他平台、IL2CPP 和公网连接不在本轮已验证范围内。

## 依据与验证

2026-10-17 本轮：

- 中转：.NET 10 构建通过，44 项真实 loopback HTTP/WebSocket 传输检查通过，覆盖双令牌、Origin/Host、大小/深度/重复键、有界并发、超时无重发、断线清理、心跳及握手顺序竞争。测试直接链接 UPM 出站连接源码，执行委托采用隔离值，不替代 Unity 反射验收。
- Unity 6000.5.9f1 / Windows x64 / Mono / 非 Development Player 最终构建通过；两个独立 Player 通过中转完成 12 组验收、58 次 HTTP/MCP 调用：同名对象句柄的私有字段读写互相隔离，同名计划和操作编号按 Runtime 隔离，修改现值后重试原编号不重复写入，错误会话被拒绝，显式重连及整个中转进程重启后自动恢复且保留计划、句柄与回执，两实例帧号持续增长。测试通过后全部自启进程已清理，普通日志未出现两个接入令牌；本地原始证据不纳入公开历史。
- 取消状态竞争用实际 Runtime 的 PendingRequest 源码做独立检查，覆盖预取消、已开始后取消以及 10000 次并发开始/取消；断线取消先于主线程开始时不会落地写入。公网 TLS、反向代理、IL2CPP、其他平台及真实消费工程中转链路未验收。
- 操作计划：Unity 6000.5 编译、29 项 Editor 内计划检查和 Windows x64 / Mono / 非 Development 独立程序构建通过；25 项真实 HTTP/MCP 检查确认同帧执行、新编号真实修改、原编号成功及失败去重、会话校验、参数字面值、MCP 失败标记、历史隐私与释放边界。Player 实际日志确认执行说明、失败和缓存回执提示；Python 客户端 24 项 HTTP 契约测试通过。新操作与回执查询分别记录本机往返耗时，不作为 AI 响应速度或业务异步完成时间。通用夹具无图形运行，未作为视觉或消费工程业务验收；证据位于本地 `.scratch/reflection-plans/`。
- 连接状态与操作说明：Unity 6000.5 编译及 Windows Mono 独立验证程序构建通过；34 项真实 HTTP/MCP 检查覆盖分类计数、并发、失败标记、16 条历史上限、说明必填与边界、令牌脱敏和格式处理，Player 实际日志确认执行前说明及成功或失败。原有 22 项端点/超时检查通过，Python 客户端 17 项契约测试通过。Unity 2022.3 当前消费工程已加载并打开状态窗口，未进入 Play 时状态归零；本轮未以截图作为窗口视觉验收。
- Unity 6000.5.9f1 编译和 19 项 Editor 内反射检查通过，正常退出码 0；覆盖私有继承字段、无隐式 getter、构造、重载、句柄、结构副本回写及异常根因。这是 Editor API 检查，不等同于 Editor Play 的外部接入验收。
- Windows x64 / Mono / 非 Development 验证程序构建通过，正常退出码 0。该通用夹具以无图形模式运行，22 项真实 HTTP / MCP 端点检查通过，未作为视觉或性能验收。
- 真实 Player 验证了持续增长的帧号、运行对象字段写入读回、构造新对象并通过引用写回、方法调用、静态属性恢复、认证与来源检查，以及 MCP 初始化、工具发现和调用。
- 超时检查确认：已开始的调用不承诺终止，尚未执行的排队写入超时后不再落地；测试恢复状态无清理错误。
- Python 标准库客户端 11 项 HTTP 契约测试通过。原始日志和本地证据在 `.scratch/implementation/`，不纳入公开历史。
- Unity 2022.3.15f1c1 真实消费工程的 Editor Play 和正常渲染 Windows x64 / Mono / 非 Development Player 均通过：外部脚本创建并加入一个具有唯一标识的新对象，宿主时间继续前进，对象自然移动且视图激活；两条路径均保存实际截图。注入不修改宿主生成计数，不手动调用 Tick 或渲染，不需要新增宿主业务接口。
- 消费工程最终构建为 0 错误、0 警告，构建前后来源检查通过。前两轮因既有 URP 等设置自动升级造成来源不稳定，失败记录保留，没有作为性能比较；业务脚本、原始数据及具体构建记录留在消费工程，未进入本公开仓库。
- IL2CPP、其他平台与公网未验证。通用反射通过不代表任意被裁剪成员、开放泛型或平台接口自动可用。

2026-09-30 初始化历史：

- Unity 6000.5.9f1 批处理编译通过，正常退出码 0。
- `ProjectValidation.Bootstrap` 已生成验证场景，并输出 `CLIENTMCP_VALIDATION_OK`，确认嵌入包注册及 `ClientMcp.Runtime` 在 Player 编译程序集列表中。
- 当前仅验证 Editor 编译、包注册和场景生成；未运行 Play Mode、构建 Player 或验证 IL2CPP。程序集被列入 Player 编译列表不代表 Player 构建已通过。
- 独立消费工程通过 `https://github.com/Counull/ClientMCP.git?path=/Project/Packages/com.counull.clientmcp#main` 安装，锁文件来源为 `git`，解析到初始化提交 `433368814c577ce0bb922c7812d221a519a2971e`。
- 消费工程编译通过，输出 `CLIENTMCP_VALIDATION_OK`（`source=Git`、Unity `6000.5.9f1`），正常退出码 0。之后的文档更新不改变已验证的包内容。
- 初始化已推送；未创建版本标签或正式 Release。缓存、原始日志和临时消费工程不纳入公开历史。
