# 桌面端界面与流程优化审查

建立日期：2026-10-10。参考 [Android UI 审查](../../Client/RelaxKonOS.Client.Android/docs/development/UiReview.md) 的组织方式：先盘点所有界面，再分别审查功能、流程、异常恢复与布局。本文件维护桌面专项清单和进度；Android 详细规范继续由 Android 工程拥有。

已完成入口盘点、逐文件结构扫描，并开始逐项实现。已修复模态取消守卫、Docker 编排提交回执、防火墙草稿保护，以及登录探测/认证/登出的迟到响应问题，受控回归通过；真实桌面及远端业务尚未验收。结构扫描不等于完整源码审查，所有界面均未完整验收。

当前设计规则仍以 [内置应用 UI](RelaxKonOS.BuiltInApps.UI.md)、[桌面外壳](RelaxKonOS.Desktop.md)、[设置设计](RelaxKonOS.Settings.Design.md) 和 [操作反馈](../applications/RelaxKonOS.DesktopOperationFeedback.md) 为准。已有视觉调整不自动计为本专项流程验收。

## 范围与发现方法

桌面启动项目是 `Client/RelaxKonOS.Client.Desktop`，业务界面主要位于共享客户端 `Client/RelaxKonOS.Client`。范围包括登录前窗口、宿主桌面、三套 Shell、内置应用、设置子路由、编辑器、对话框、选择器和通用窗口交互。

1. 扫描 XAML 根视图，分别登记主入口、子页与弹窗。
2. 补查各 `*App.cs` 的 `Activate`、`ShowWindow`、`ShowDialogAsync`，找出 C# 动态构建页面。
3. 展开设置的 `DataTemplates` 与路由，登记复用同一视图的不同任务。
4. 追踪 ViewModel、会话归属、关闭事件、写请求及异步回调，逐场景深审。
5. 运行时发现的新菜单、弹出层及编辑器必须回填清单。

排除 `bin/`、`obj/`、语言资源和纯主题资源；`App.axaml` 是样式入口。`examples/` 的扩展示例不计为内置产品界面。系统文件选择器需检查取消、慢源和迟到回调，但不是独立维护的产品页面。下面的视图数量不等于独立业务界面数，更不等于功能覆盖率。

## 审查流程与状态

逐页完成：功能与入口清单 → 用户任务与状态流 → 正常/错误/取消路径 → 优化决定 → 实现 → 回归 → 目标平台视觉及真实业务验收。

状态：已盘点待深审、结构初查完成、流程深审完成、已实现待验证、已验收、受阻。编译通过、共享样式已接入或单个测试通过不能替代整页验收。

每页检查：

- 信息与操作：服务器、账号、工作区和资源目标清楚；主要操作突出；列表使用弹性数据区，概览按需滚动，避免重复标题与嵌套面板。
- 状态：加载、空数据、失败、旧事实、无权限、能力缺失、断线分别表达，提供可执行的下一步；旧事实不能提供未经确认的写权限。
- 草稿：关闭按钮、Esc、标题栏关闭、切页及登出统一保护；读取失败保留输入，显式决定是否采用新读取版本。
- 异步：提交锁定字段及目标；重复点击、迟到响应、切换身份不触发重复操作或污染新会话。
- 写入：区分明确拒绝、取消、结果未知和成功后刷新失败；未知结果先核实，不盲目重放变更。
- 安全：认证、凭据保存和提权分开；风险确认冻结目标；临时密码按生命周期清理，不通过跳过权限减少步骤。
- 桌面交互：窄窗口、低高度、大字体、长路径、三语和主题切换下仍可完成任务；键盘焦点、快捷键、模态焦点返回及屏幕阅读器可用。

采用当前 API 契约，接口变化同步更新调用者和文档，不添加兼容别名或双格式解析。

## 分批顺序

| 优先级 | 范围 | 首要目标 |
|---|---|---|
| P0-1 | 登录、本地管理、服务器中心及主机密钥 | 认证恢复、目标绑定、切换与取消门禁 |
| P0-2 | 文件、SSH 文件、文本/代码编辑、终端 | 草稿、文件安全、会话绑定与未知写入核实 |
| P0-3 | Docker、部署、证书、网站、防火墙、SMB、代理、FRP、守护 | 风险确认、提交快照、后台反馈与失败恢复 |
| P1 | Shell、窗口、设置、任务管理器、告警与诊断 | 空间分配、键盘交互、动态状态与资源释放 |
| P2 | 欢迎、浏览器、图片、关于及辅助设置 | 信息层级、长文本、无障碍与平台差异 |

## 已确认的局部流程问题

### D-001：Docker 编排关闭与后台提交反馈

P0；首批已实现并通过受控回归，完整故障与真实界面待验证。

证据：`Apps/Docker/Views/DockerStackDialogView.axaml.cs` 的 `Cancel_Click` 直接 `_dialog.Cancel()`；`Deploy_Click` 在 `TryDeployStackAsync()` 返回 true 后关闭。`Apps/Docker/DockerManagerViewModel.cs` 的该方法设置 `IsLoading`，启动 `_ = DeployStackCoreAsync(name)` 后立即返回 true；真正的预览、提交与追踪在后台。因此关闭仅表示开始执行，尚不能证明远端接受或部署成功。XAML 名称/YAML 和点击提交按钮没有显式忙碌绑定；ViewModel 已有 `IsLoading` 重入判断，仍需补齐界面执行阶段与字段冻结的表达。

保留已有正确行为：表单滚动、固定操作区；后台重新预览并携带 `DefinitionVersion` 校验。

建议：冻结编辑目标及初始草稿；修改后取消统一确认放弃；提交期间锁定字段；明确采用远端接受后关闭，或转至任务面板并保留可恢复草稿。错误、拒绝和未知结果关联本次目标与草稿。任务启动后关闭不能暗示任务已取消。

验收：修改后取消/保留；连续部署；预览失败；远端拒绝；接受后断线；刷新失败；身份切换；重新打开草稿与任务记录。Esc/标题栏关闭需要联动 WindowManager 单独核对。

2026-10-10 首批实现：提交方法等待远端操作回执，收到后后台继续追踪；回执前拒绝保留编辑器及草稿，不自动刷新覆盖失败原因。名称/YAML 与操作区忙碌锁定，编辑器内显示状态，操作按钮可换行；取消经统一守卫，修改后嵌套确认放弃。待继续完成：提交结果未知的核实门禁、已接受任务的故障恢复、会话归属、真实视觉及业务验收。

### D-002：防火墙规则编辑取消直接退出

P0；首批已实现并通过受控回归，完整故障与真实界面待验证。

证据：`Apps/Firewall/FirewallApp.cs` 的 `ShowRuleEditorAsync` 动态创建表单，取消按钮直接 `dialog.Cancel()`；保存只在 `AddRuleAsync`/`UpdateRuleAsync` 成功后关闭。弹窗已有 `StatusText` 和滚动字段，不能笼统声称缺少错误反馈；但该取消点击路径没有草稿放弃确认。

建议建立初始草稿差异与统一关闭守卫，继续核对 ViewModel 的忙碌、规则目标、事实变化与未知结果门禁。验收修改后取消保留、提交锁目标、读取失败保留只读草稿，以及默认策略变更导致当前连接断开的恢复。Esc/标题栏尚待基础设施联审，不将局部证据推广到全部关闭路径。

2026-10-10 首批实现：字段与保存/取消按钮绑定忙碌；草稿差异守卫涵盖规则选项、源/目标及端口，统一取消/Esc/标题栏/Alt+F4。直接新增/更新入口增加忙碌与能力门禁，更新冻结规则编号和请求字段。待继续完成：规则事实修订与身份边界、断连风险确认、未知写入核实和真实设备验收。

### D-003：登录探测、认证取消与迟到会话响应（P0-1）

2026-10-10 已实现，受控回归通过；完整真实界面与服务器验收待完成。

源码深审发现：`LoginViewModel` 在端点探测后才设置 `IsConnecting`，探测阶段可改字段或启动另一登录；部分设备密钥入口的探测取消在错误处理外。`AuthSession` 原先直接应用登录响应，失败仅改状态而不清令牌；远端登出完成无条件重置，旧刷新 401 也无条件重置。

修复：从探测开始锁定整个连接流程，三种探测入口取消均恢复非忙碌且不虚构认证失败。四种认证入口共用登录版本检查，发布身份/令牌前核对版本与取消，旧失败不清除新会话；失败清理完整身份。登出立即清本地会话，远端撤销只针对捕获的原会话；刷新归属检查同时覆盖成功和 401。若原会话正在刷新，登出等待其结果并撤销后继令牌。

证据：`AuthSessionCancellationChecks` 验证取消后迟到响应、未保存账号、旧登录/旧登出、旧刷新拒绝、刷新后继撤销、设备密钥登录与首次设置取消；`LoginDiscoveryCancellationChecks` 对密码/设备密钥/首次设置验证探测忙碌、互斥与取消恢复。`--owner-device-only` 同时通过既有登录记录和密码保留回归。完整 ServerCenter 测试沙箱内在临时日志 `File.Move` 被拒；沙箱外重跑退出 0，原有传输、证书信任、SSH/安装/维护/卸载和新增认证回归通过（Linux Secret Service 专项按平台跳过）。没有运行真实远端写操作。

窗口生命周期首批补齐：每次打开登录窗口建立取消范围，连接/探测/设备认证/密钥确认/隧道测试接入该范围；用户关闭取消请求并清理待确认密钥，桌面交接通过 `CloseForDesktop()` 保留连接。取消后不写迟到的隧道信任或凭据；窗口关闭解除标题语言订阅。SSH 发布检查取消及连接版本，断开使未完成握手失效。App 排队回调核对当前认证/SSH 状态，保存提示返回后再核对关闭状态。

待继续：配对完整交互、保存记录/异步凭据填充生命周期、登录视觉/语言/主题与真实服务故障矩阵。

### D-004：服务器中心管理弹窗关闭与安装选择器恢复（P0-1）

2026-10-10 局部实现：`ServerCenterManagementDialog` 原先仅页内关闭按钮检查忙碌，标题栏/Esc 可直接取消。现在直接接收模态句柄并接入统一草稿守卫；详情页无修改直接退出，修复页比较防火墙/证书选项及名称，卸载页比较组件和数据删除选项。忙碌时所有用户取消路径均拒绝，确认保留回到原编辑器，确认放弃关闭。

安装向导的三处本地选择器原先将取消返回的 null 传给路径设置方法，清除已有包/证书/私钥。现在空结果保留路径，忙碌不发起选择；返回结果检查当前页面、可见 owner、选择版本和对应来源/证书格式。页面脱离视觉树使旧选择失效，即使原页面重新挂回也不采用旧结果。

证据：`ServerCenterDialogChecks` 对详情、修复、卸载实际视图执行 10 项 headless 检查，覆盖忙碌关闭、无草稿关闭、确认保留/放弃及嵌套清理；最终 WindowPreviews `--ui-review-only` 回归通过。Desktop 构建通过（5 个既有警告/0 错误），最终测试项目增量构建 0 警告/0 错误。选择器修改完成源码审查与编译，尚未验证真实系统选择器的取消/慢源/页面重挂回调。

远端向导关闭保护继续实现：取消与成功回调已拆开，取消返回默认结果且等待守卫决策，成功独立返回 true。草稿比较包括所有来源、模式、路径、证书、密码、网络/权限、端口、目录和高级选项，非法输入也参与比较；取消确认不清密码，实际关闭后清除密码。远端选包与主机地址弹窗改为向导子模态，避免同级弹窗破坏阻塞/焦点链。直接安装入口补充忙碌/步骤门禁。

证据补充：`InstallationWizardDialogChecks` 的 5 项实际视图 headless 检查通过，覆盖默认草稿、非法输入、确认取消保留密码、忙碌关闭与确认放弃的取消结果/密码清理。最终 WindowPreviews `--ui-review-only` 通过，完整 ServerCenter 回归沙箱外通过，既有本地安装成功、选包和模式检查也通过；桌面构建通过。

登录前本地管理窗口的关闭保护后续已补齐，见 D-005。仍必须继续审查事实/目标绑定、未知操作核实、真实远端修复/卸载与选择器流程。此记录不把 3 个管理场景或安装向导标为完整验收。

### D-005：本地安装窗口取消、重开及默认值隔离（P0-1）

2026-10-10 局部实现并通过受控回归。`LocalServerInstallationWindow` 的页内取消与标题栏关闭统一使用原生草稿守卫；忙碌阻止关闭，修改后确认放弃，取消确认保留输入与密码。实际关闭或返回管理页后清理密码，成功返回管理页不需要放弃确认。每次重新打开安装向导创建新草稿，并将初始化模式/本地快照值登记为基线，放弃的内容不复用。

`NativeDraftCloseGuard` 合并确认期间的关闭门禁，批准后的关闭排到当前 Closing 事件之后执行；其他关闭监听器拒绝时不清密码，批准标志立即复位，后续编辑仍需确认。确认窗口是原生 owner 的子窗口，支持三语标签、Esc 与标题栏取消。

顺带修复本地向导对共享服务器中心残留状态的依赖：本地窗口不继承旧远端 Linux 主机、HTTPS 端口或证书选择；已读取的本地安装快照提供本机端口/HTTPS 的初始选择。证书的完整保留/更新语义与真实安装仍需后续核对，不能从 ListenUrl 推断原证书来源。

证据：`LocalInstallationCloseChecks` 使用实际本地管理窗口和原生确认窗口，在 headless 环境完成 9 项检查，包括取消保留、放弃清理、重开新草稿、忙碌标题栏、非法输入、外部关闭拒绝与批准标志复位。测试注册不支持安装的替身，未启动真实安装器。最终 WindowPreviews `--ui-review-only` 通过。完整 ServerCenter 沙箱外通过，新增旧远端 Linux/HTTPS 默认值不污染本地向导断言通过；Desktop 最终增量构建 0 警告/0 错误（首轮客户端编译仍有已有 5 项警告）。

未完成：原生实际渲染及全语言/缩放/键盘矩阵；真实 Windows UAC/安装/维护、已有证书类型与选包故障；未知写入核实。下一批继续其余 P0 编辑器与服务器中心事实恢复，不把 headless 关闭回归作为真实安装验收。

### D-006：网站编辑器草稿与保存忙碌（P0-3）

2026-10-10 局部实现并通过受控回归。`WebServerSiteDialogView` 原取消直接退出；站点保存未设置独立忙碌状态，字段仍可修改。现在取消/Esc/标题栏/Alt+F4 共用草稿守卫，比较名称、批量绑定输入、静态目录/权限、SPA、HTTPS/IPv6、证书来源/文件，以及绑定和路由集合的逐项值。嵌套行内编辑也需要确认放弃，取消确认保留草稿。

保存入口加入 `IsSavingSite` 与直接重入门禁，保存/新建/编辑/删除站点命令随状态更新；提交期间冻结表单及操作区并拒绝关闭，失败后恢复编辑。保留原来的错误子弹窗与成功回执关闭。操作区采用换行布局，低宽度下不强制一行。

证据：`WebSiteEditorChecks` 对实际编辑器完成 8 项 headless 检查，包括嵌套路由修改、确认保留、保存忙碌、单请求门禁、关闭拦截、提交字段快照、明确拒绝后完整输入保留及确认放弃。最终 WindowPreviews `--ui-review-only` 通过，测试项目构建 0 警告/0 错误；Desktop 编译通过（5 个已有警告/0 错误）。新增测试没有连接实际 Nginx 或写入测试服务器。

站点读取与保存后刷新恢复已接续实现：读取成功后才替换列表，读取失败或缺少响应保留最后数据与草稿并使站点事实不可用于写入；空列表只由有效空集合表示。显式刷新入口可恢复事实，新建/编辑/删除/保存命令同时在 UI 与直接调用检查门禁。读取版本和实例目标阻止旧结果覆盖新实例。保存已取得回执后，刷新失败仍保留已保存站点的权威回执，显示三语“已保存但列表刷新失败”，不虚构保存失败；旧列表和已保存项在重新核对前不提供站点写权限。提交期间增加进度提示，禁止全局刷新/发现切换实例事实。

新增 `WebSiteReadRecoveryChecks` 的 8 项受控检查通过，覆盖列表/草稿保留、写入门禁、null 与空列表区别、刷新恢复、成功回执后读取失败、不重放保存、旧实例迟到读取。最终 WindowPreviews `--ui-review-only` 全部通过，三语 JSON 校验通过，Desktop 最终增量构建 0 警告/0 错误（客户端首轮编译仍有已记录的 5 个已有警告）。本轮没有真实 Nginx 或服务器操作。

站点会话与目标绑定已接续实现：管理器捕获创建时的稳定服务身份、账号与认证会话对象；同账号重新登录即使 DTO 值相同也视为新认证，传输地址重绑定保留原身份时仍可继续。弱订阅认证变化，在 UI 线程清理旧站点、证书、实例和草稿并暂停观察；旧管理器不自动改绑新账号。保存的提权回调执行前、回执及随后读取返回后核对原作用域，失效响应不恢复旧数据或关闭新编辑器。

站点编辑器登记打开时的实例选择版本、站点和修订；实例/站点变化后旧草稿保留只读，提示关闭后重开。相同站点的后续读取不替换输入，提交仍携带原修订。创建/编辑在异步读取证书前记录目标与选择版本，选择变化后不打开错误目标的编辑器。实际模态结束时清理编辑器绑定。

新增 `WebSiteScopeChecks` 9 项受控检查通过：合法传输重绑定、提权前身份变化不发送写入、相同 DTO 值重新认证、迟到回执不恢复旧身份、实例变化拦截并保留只读草稿、重读保留输入/原修订、证书读取期间实例及站点变化不打开错误编辑器。相关已有站点编辑/读取恢复检查也通过；最终 Desktop、测试项目增量构建 0 警告/0 错误，三语目标变化提示已加入。

本轮作用域验证限于站点保存、列表/证书读取和编辑器开启；其他 Web 服务生命周期操作仍需逐项审查。仍需继续证书/目录选择器子模态和迟到回调、没有写入回执时的结果未知核实门禁，以及真实保存/拒绝/断线和视觉矩阵。当前不能将站点编辑器标为完整验收。

## 动态界面与复用路由补充清单

每个分号分隔的场景需分别验收，不能因共用源码而合并结论。除已明确记录的局部修复外，以下状态均为已盘点待流程深审。

| 源码（相对于共享客户端） | 界面或场景 | 审查与优化重点 |
|---|---|---|
| `Views/Login/LocalServerInstallationWindow.cs` | 登录前本地安装 | 步骤、选包、提权、日志、取消及失败后恢复 |
| `Views/Login/LocalServerMaintenanceWindow.cs` | 登录前本地维护 | 实例目标、启停/更新、忙碌关闭、历史与未知结果 |
| `Views/Login/LocalManagementLayout.cs` | 本地管理共用布局 | 与上述窗口联审，低高度正文滚动、操作可达 |
| `Apps/Firewall/FirewallApp.cs` | 未登录；规则列表；默认策略；新建；编辑 | 设置已用 WrapPanel，表格可横向滚动；D-002 跟进，断连风险和规则快照 |
| `Apps/ProcessGuardian/ProcessGuardianApp.cs` | 未登录；工作负载；新建/编辑；独立日志 | 可执行路径/环境/身份、草稿关闭、日志订阅释放 |
| `Apps/EventAlerts/EventAlertCenterApp.cs` | 事件与告警中心 | 摘要/时间/详情、空与失败状态、新旧事件区别 |
| `Apps/Certificates/Views/CertificateRenewalHistoryView.cs` | 续期历史 | 与执行列表区分，长日志、空历史、刷新失败 |
| `Apps/ServerCenter/Views/SshTransferProgressWindow.cs` | SSH 传输进度窗口 | 关闭与取消语义、部分完成核实、进度与失败 |
| `Views/Shell/WindowsTaskbarPreview.cs` | 任务栏预览及关闭 | 目标窗口、键盘/悬停、缩放、多屏与焦点 |
| `Services/UriSchemeRoutingUi.cs` | URI 应用选择；错误提示 | URI/应用目标可见、长地址、取消、无处理器 |
| `Services/Privileged/PrivilegedHelperUnavailableDialog.cs` | 提权助手不可用 | 原因与恢复，不误报密码错误 |
| `Apps/Settings/Views/SettingsView.axaml` | 首页；单网卡；单应用；应用权限路由 | 展开 DataTemplates；分别冻结适配器与应用目标，搜索定位/返回 |
| `Apps/Settings/SettingsApp.cs` | 账号；出站代理风险确认；环境变量/PATH 编辑及删除 | 动态嵌套弹窗、草稿、密码清理、子模态焦点 |
| 同上 | 壁纸；自定义/删除主题；卸载；清理数据确认 | 检查各调用点目标，不能只验收通用 ConfirmDialog |
| 各 `*App.cs` 的文件选择回调 | 本地打开/保存/包上传；远端文件/目录选择 | 系统选择器与 Explorer 选择模式分别检查；迟到结果核对原身份 |
| `Views/Shell/*ShellLayoutView.axaml` / `LauncherDesktopShells.cs` | 桌面菜单；启动器；任务栏/Dock；窗口菜单；通知区域 | 三套 Shell 分别验收鼠标/键盘、自动隐藏与窗口恢复 |
| `Framework/RelaxKonOS.WindowManager/RemoteWindow.cs` / `WindowManager.cs` / `Themes/RemoteWindowTheme.axaml`（仓库根路径） | 标题栏；缩放；关闭；模态遮罩；嵌套弹窗 | owner 局部阻塞、业务关闭守卫、焦点返回、屏幕边界 |
| `Framework/RelaxKonOS.UI/Themes/Controls/ControlThemeOverrides.axaml`（仓库根路径） | 共用控件/菜单/弹出层 | 三种风格、语义色、焦点、命中区域，随业务页验收 |

## 各模块深审重点

| 模块 | 功能与优化重点 |
|---|---|
| 登录/服务器中心 | 保留记录、远端认证、主机信任、部署/维护/历史；凭据复用有身份边界，切目标取消旧请求，密钥变化显式确认 |
| Shell/欢迎 | 保留引导、桌面文件、启动器、窗口切换、显示设置与登出；启动恢复失败有出口，跨 Shell 与关闭草稿一致 |
| Explorer/SSH 文件 | 导航、新建/删除/复制移动/传输/属性/选择器；部分完成和未知结果核实，取消不污染其他目标 |
| Notepad/CodeEditor/TextEditor | 打开/保存/另存/编码/BOM；路径与修订绑定，冲突显式解决，未知保存不盲目重试 |
| Terminal | 本地/远端输入、粘贴、设置；断线门禁、迟到粘贴不跨会话、活跃会话关闭语义 |
| Docker | 资源、编排、代理/镜像源、任务；D-001 优先，后台反馈与草稿恢复，删除目标明确 |
| Git | 项目、状态、分支、历史、diff、push、冲突；仓库消失保留草稿，刷新不切目标，未知 push 核实 |
| 部署 | 定义、选包、预检、任务；步骤保留输入、包归属、预检版本与提交一致 |
| 证书/Web 服务 | 申请/续期/部署/吊销、实例/站点/发布；条款与危险确认、冻结修订、保存失败返回原表单 |
| Proxy/FRP | 配置/订阅/安装/日志/节点/连接/网络/frps；刷新不覆盖草稿，失败反馈位于编辑区，上传回调身份绑定 |
| SMB/端口转发 | 共享/用户/凭据、连接/转发编辑；敏感密码清理、路径风险、刷新后目标变化重新确认 |
| 防火墙/守护/任务管理器 | 策略/规则、工作负载、日志、进程/指标；断连风险、进程标识、结束目标快照、订阅释放 |
| 设置/注册表/安装器 | 偏好/宿主/环境/权限/键值/包安装；作用域明确、类型校验、拒绝保留草稿 |
| 告警/诊断/浏览器/图片 | 事件、网络诊断、网页/图像；敏感日志导出、平台能力、失败恢复、缩放/键盘可达 |

## 验证矩阵

| 维度 | 场景与验收要求 |
|---|---|
| 窗口 | 宿主 800×520、应用最小尺寸、owner 压缩弹窗、全屏；正文和操作可达 |
| 缩放 | 100%/150%/200%、跨 DPI、多屏边缘、文本放大；菜单/弹窗位置与焦点正确 |
| 语言/外观 | 中/英/日、浅/深色；三种系统风格与三套 Shell 分别覆盖；长路径/地址可读可复制 |
| 键盘/无障碍 | Tab/Shift+Tab、Enter/Esc、快捷键、屏幕阅读器；焦点可见、名称明确、模态返回 owner |
| 数据 | 加载/空/失败/旧事实/能力缺失/无权限；区别状态并有恢复入口 |
| 身份 | 密码拒绝、信任取消/密钥变化、提权取消、登出/切服务器；不串草稿/响应/凭据 |
| 异步/写入 | 重复点击、慢响应、断线/超时、拒绝、未知结果、成功后刷新失败；固定目标且可核实 |
| 生命周期 | 关闭重开、切页、最小化、休眠、进程重启；持久化边界明确、订阅释放 |
| 平台/真实业务 | 按支持范围检查 Windows/Linux/macOS 客户端与 Linux/Windows 服务端；缺少环境标待验收 |

实现后按 [桌面导航](README.md) 构建并运行相关客户端测试，再做真实界面/服务验收。证据标明版本、平台、时间与场景，敏感数据脱敏。

## 实施与验证记录

### 2026-10-10：模态关闭与两处编辑器

- `ModalDialog.CanCancelAsync` 为按钮、Esc、Alt+F4 和标题栏关闭提供异步守卫；重复请求只检查一次。成功关闭与 owner 生命周期清理保留独立语义，详见 [模态取消契约](RelaxKonOS.Desktop.md)。共用 `DraftDialogGuard` 提供三语放弃确认，取消确认保留编辑器。
- 桌面构建通过：`dotnet build Client/RelaxKonOS.Client.Desktop/RelaxKonOS.Client.Desktop.csproj --no-restore -p:UsedAvaloniaProducts= -p:UseSharedCompilation=false -m:1`。首次并行构建被取消，串行重跑成功，5 个既有/无关警告、0 错误。
- WindowPreviews 整套受控回归通过，覆盖设置、断开、窗口预览与生命周期，并包含首版模态守卫检查。之后补充 Docker、防火墙与嵌套草稿确认回归，测试项目构建 0 警告/0 错误，`dotnet run --no-build --project Tests/Client/RelaxKonOS.WindowPreviews.Tests -- --ui-review-only` 全部通过。覆盖拒绝关闭、确认保留/放弃、重复取消、owner 清理、等待回执、重复提交、拒绝保留草稿、固定规则目标与不可用事实写入门禁。
- 原生桌面启动未完成：computer-use 的应用启动审批等待超时；不能据此登记真实截图或视觉通过。
- 指定测试服务器验证未成功：沙箱网络套接字不可用；获准在沙箱外直连后，请求 15 秒超时，未取得认证回执。此记录不能说明账号密码错误；未修改服务端配置，也未记录凭据或令牌。
- 此批之后继续 P0-1 登录/认证取消与迟到响应，并补齐 D-001/D-002 剩余故障恢复。后续进展见下方记录，当前目标持续进行中。

### 2026-10-10：登录探测与会话恢复

- D-003 实现与新增 19 项受控断言通过；完整 ServerCenter 回归在沙箱外通过。登录控件与使用偏好整套 Login 回归也在沙箱外通过；沙箱内临时文件替换导致持久化断言失败，没有更改生产存储代码绕过限制。
- 最终源码重新构建 Desktop、ServerCenter、WindowPreviews 测试项目通过；最后一次增量构建均 0 警告/0 错误。首次重新编译客户端仍有首批列出的 5 个既有警告。
- 最终 WindowPreviews 整套回归通过，包含模态守卫、Docker/防火墙提交、设置、断开连接、任务栏预览及内存生命周期。由受控 headless 页面产生的 QA 图像不能代替原生桌面/真实服务器验收。
- 下一步审查登录窗口关闭的请求生命周期、服务器中心恢复，以及其余 P0 编辑器的草稿/未知写入流程。指定服务器和原生界面没有新增验证结果。

### 2026-10-10：登录窗口交接与 SSH 验证生命周期

- `LoginWindowLifetimeChecks` 使用实际 LoginWindow 控件，在 headless 环境验证用户关闭取消探测、桌面交接不取消请求；Login 整套回归沙箱外通过。
- 探测取消回归增加窗口关闭、重开以及失焦探测的 3 项检查；`SshDesktopCancellationChecks` 通过取消后凭据不发布/传输释放、握手中断开、同主机新连接获胜 3 项检查。
- 完整 ServerCenter 回归在最终隧道取消修复后沙箱外通过。随后增加 App 的迟到登出事件当前状态检查，最终 Desktop 构建通过，5 个既有警告、0 错误；该 App 完整启动事件链未做原生 UI 验收。
- 此轮没有重试不可达服务器，也没有取得原生截图。下一批转入服务器中心和其余 P0 编辑器恢复，保留登录剩余验收项。

## 后续逐项记录模板

| 字段 | 填写内容 |
|---|---|
| 编号/入口 | 对应 UI 编号或动态场景，实际进入步骤及父子模态 |
| 功能清单 | 保留的全部用户操作及状态流 |
| 问题/证据 | 源码位置、复现/截图/测试，区分确认问题和风险 |
| 优化决定 | 行为变化、优先级及依赖 |
| 实现/回归 | 修改点、执行命令与实际结果 |
| 运行验收 | 平台/语言/主题/缩放、真实业务与证据 |
| 未完成项 | 尚缺环境与异常分支，不能提前标已验收 |

## 逐视图结构清单

以下路径相对于共享客户端。逐文件扫描控件、滚动及操作入口；每行的重点还需结合父容器和 ViewModel 深审。某控件未直接出现在文件中，不代表整个页面缺少相应能力。


### AppInstaller

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-001 [AppInstallerView](../../Client/RelaxKonOS.Client/Apps/AppInstaller/Views/AppInstallerView.axaml) | ScrollViewer；入口：ChooseLocalCommand、ChooseServerCommand、SkipCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |

### ApplicationDeployments

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-002 [ApplicationDeploymentsLoginRequiredView](../../Client/RelaxKonOS.Client/Apps/ApplicationDeployments/Views/ApplicationDeploymentsLoginRequiredView.axaml) | 展示/容器，随父入口联审 | 认证入口、能力/权限区别；重登录返回原任务 | 结构初查完成，待流程深审与运行验收 |
| UI-003 [ApplicationDeploymentsWorkspace](../../Client/RelaxKonOS.Client/Apps/ApplicationDeployments/Views/ApplicationDeploymentsWorkspace.axaml) | TabControl；TextBox；入口：CancelOperationCommand、NewDeploymentCommand、RefreshCommand | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-004 [DeploymentWizardView](../../Client/RelaxKonOS.Client/Apps/ApplicationDeployments/Views/DeploymentWizardView.axaml) | ScrollViewer；TextBox；入口：LoadImageTagsCommand、ChooseLocalArchiveCommand、ChooseServerArchiveCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |

### Browser

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-005 [BrowserMainView](../../Client/RelaxKonOS.Client/Apps/Browser/Views/BrowserMainView.axaml) | TextBox；ContentControl；入口：CloseTab_Click、AddTabCommand、GoBackCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-006 [BrowserSettingsView](../../Client/RelaxKonOS.Client/Apps/Browser/Views/BrowserSettingsView.axaml) | TextBox；入口：SaveBrowserSettingsCommand、CloseSettingsCommand | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |

### Certificates

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-007 [CertificateListView](../../Client/RelaxKonOS.Client/Apps/Certificates/Views/CertificateListView.axaml) | DataGrid；ScrollViewer；TextBox；入口：RefreshCommand、RequestCertificate_Click、CreateSelfSignedCertificate_Click | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-008 [CertificateLoginRequiredView](../../Client/RelaxKonOS.Client/Apps/Certificates/Views/CertificateLoginRequiredView.axaml) | 展示/容器，随父入口联审 | 认证入口、能力/权限区别；重登录返回原任务 | 结构初查完成，待流程深审与运行验收 |
| UI-009 [CertificateManagerWorkspace](../../Client/RelaxKonOS.Client/Apps/Certificates/Views/CertificateManagerWorkspace.axaml) | ScrollViewer；TextBox；ContentControl；入口：CancelOperationCommand、NavigationButton_Click | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-010 [CertificateOverviewView](../../Client/RelaxKonOS.Client/Apps/Certificates/Views/CertificateOverviewView.axaml) | 入口：RequestCertificate_Click、CreateSelfSignedCertificate_Click、RefreshCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-011 [CertificateRenewalRunsView](../../Client/RelaxKonOS.Client/Apps/Certificates/Views/CertificateRenewalRunsView.axaml) | DataGrid；入口：RefreshRenewalRunsCommand | 时间/阶段/目标；空记录与失败区别；复制与刷新；持续订阅释放 | 结构初查完成，待流程深审与运行验收 |
| UI-012 [CertificateRequestDialogView](../../Client/RelaxKonOS.Client/Apps/Certificates/Views/CertificateRequestDialogView.axaml) | ScrollViewer；TextBox；入口：PreflightCommand、Request_Click、Cancel_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-013 [SelfSignedCertificateDialogView](../../Client/RelaxKonOS.Client/Apps/Certificates/Views/SelfSignedCertificateDialogView.axaml) | TextBox；入口：Create_Click、Cancel_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |

### CodeEditor

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-014 [CodeEditorSettingsView](../../Client/RelaxKonOS.Client/Apps/CodeEditor/CodeEditorSettingsView.axaml) | 入口：CloseSettingsCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-015 [CodeEditorView](../../Client/RelaxKonOS.Client/Apps/CodeEditor/CodeEditorView.axaml) | TreeView；Menu；入口：NewDocumentCommand、OpenDocumentCommand、AddFolderCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |

### Docker

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-016 [DockerContainerDetailsDialogView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerContainerDetailsDialogView.axaml) | ScrollViewer；TextBox；入口：Copy_Click、Close_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-017 [DockerContainerDialogView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerContainerDialogView.axaml) | ScrollViewer；TextBox；入口：Cancel_Click、Create_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-018 [DockerContainerEditDialogView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerContainerEditDialogView.axaml) | TextBox；入口：Save_Click、Cancel_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-019 [DockerContainersView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerContainersView.axaml) | DataGrid；TextBox；入口：RefreshCommand、CreateContainer_Click、StartContainerCommand | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-020 [DockerErrorDialogView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerErrorDialogView.axaml) | 入口：Close_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-021 [DockerImageMirrorsView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerImageMirrorsView.axaml) | TextBox；入口：DataContext.SelectCommand, ElementName=Root、DataContext.RemoveCommand, ElementName=Root、AddCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-022 [DockerImagesView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerImagesView.axaml) | DataGrid；入口：RefreshCommand、PullImage_Click、DeleteImageCommand | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-023 [DockerLoginRequiredView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerLoginRequiredView.axaml) | 展示/容器，随父入口联审 | 认证入口、能力/权限区别；重登录返回原任务 | 结构初查完成，待流程深审与运行验收 |
| UI-024 [DockerManagerWorkspace](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerManagerWorkspace.axaml) | ScrollViewer；ContentControl；入口：OpenInstallationCommand、NavigationButton_Click | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-025 [DockerNetworkDialogView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerNetworkDialogView.axaml) | TextBox；入口：Cancel_Click、Create_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-026 [DockerNetworksView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerNetworksView.axaml) | DataGrid；入口：RefreshCommand、CreateNetwork_Click、LoadNetworkDetailsCommand | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-027 [DockerOperationActivityView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerOperationActivityView.axaml) | TextBox；入口：CloseOperationActivityCommand | 时间/阶段/目标；空记录与失败区别；复制与刷新；持续订阅释放 | 结构初查完成，待流程深审与运行验收 |
| UI-028 [DockerOverviewView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerOverviewView.axaml) | 入口：StartEngineCommand、StopEngineCommand、RestartEngineCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-029 [DockerProxyView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerProxyView.axaml) | TextBox；入口：SaveCommand、ClearCommand、RefreshCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-030 [DockerPullImageDialogView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerPullImageDialogView.axaml) | TextBox；入口：Cancel_Click、Pull_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-031 [DockerResourceDetailsDialogView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerResourceDetailsDialogView.axaml) | TextBox；入口：Copy_Click、Close_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-032 [DockerStackDialogView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerStackDialogView.axaml) | ScrollViewer；TextBox；入口：ValidateStackCommand、Deploy_Click、Cancel_Click | 草稿保护与提交回执已修复；继续核实未知结果/身份/真实业务 | 首批已实现，受控回归通过，完整验收待完成 |
| UI-033 [DockerStacksView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerStacksView.axaml) | DataGrid；ScrollViewer；入口：RefreshCommand、DeployStack_Click、StartStackCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-034 [DockerUnavailableDialogView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerUnavailableDialogView.axaml) | 入口：Refresh_Click、Confirm_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-035 [DockerVolumeDialogView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerVolumeDialogView.axaml) | TextBox；入口：Cancel_Click、Create_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-036 [DockerVolumesView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerVolumesView.axaml) | DataGrid；入口：RefreshCommand、CreateVolume_Click、LoadVolumeDetailsCommand | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-037 [DockerWindowsSetupGuideDialogView](../../Client/RelaxKonOS.Client/Apps/Docker/Views/DockerWindowsSetupGuideDialogView.axaml) | ScrollViewer；入口：Refresh_Click、Confirm_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |

### Explorer

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-038 [ConfirmDialogView](../../Client/RelaxKonOS.Client/Apps/Explorer/Dialogs/ConfirmDialogView.axaml) | 入口：NoCommand、YesCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-039 [FilePropertiesDialogView](../../Client/RelaxKonOS.Client/Apps/Explorer/Dialogs/FilePropertiesDialogView.axaml) | ScrollViewer；TextBox；入口：SavePermissionsCommand、CloseCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-040 [OpenWithDialogView](../../Client/RelaxKonOS.Client/Apps/Explorer/Dialogs/OpenWithDialogView.axaml) | 入口：CancelCommand、OpenCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-041 [TextInputDialogView](../../Client/RelaxKonOS.Client/Apps/Explorer/Dialogs/TextInputDialogView.axaml) | TextBox；入口：CancelCommand、ConfirmCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-042 [ExplorerMainView](../../Client/RelaxKonOS.Client/Apps/Explorer/Views/ExplorerMainView.axaml) | DataGrid；TreeView；ScrollViewer；TextBox；ContextMenu；入口：GoBackCommand、GoForwardCommand、GoUpCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-043 [ExplorerOperationsView](../../Client/RelaxKonOS.Client/Apps/Explorer/Views/ExplorerOperationsView.axaml) | ScrollViewer；入口：CancelCommand、RetryCommand、ReplaceCommand | 时间/阶段/目标；空记录与失败区别；复制与刷新；持续订阅释放 | 结构初查完成，待流程深审与运行验收 |

### FileServices

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-044 [FileServicesDeleteDialogView](../../Client/RelaxKonOS.Client/Apps/FileServices/Views/FileServicesDeleteDialogView.axaml) | 入口：Cancel_Click、Delete_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-045 [FileServicesLoginRequiredView](../../Client/RelaxKonOS.Client/Apps/FileServices/Views/FileServicesLoginRequiredView.axaml) | 展示/容器，随父入口联审 | 认证入口、能力/权限区别；重登录返回原任务 | 结构初查完成，待流程深审与运行验收 |
| UI-046 [FileServicesOverviewView](../../Client/RelaxKonOS.Client/Apps/FileServices/Views/FileServicesOverviewView.axaml) | ScrollViewer；入口：InstallCommand、StartServiceCommand、StopCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-047 [FileServicesPasswordDialogView](../../Client/RelaxKonOS.Client/Apps/FileServices/Views/FileServicesPasswordDialogView.axaml) | TextBox；入口：Cancel_Click、Confirm_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-048 [FileServicesPathWarningDialogView](../../Client/RelaxKonOS.Client/Apps/FileServices/Views/FileServicesPathWarningDialogView.axaml) | 入口：Cancel_Click、Continue_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-049 [FileServicesShareDialogView](../../Client/RelaxKonOS.Client/Apps/FileServices/Views/FileServicesShareDialogView.axaml) | ScrollViewer；TextBox；入口：BrowsePath_Click、AddPermission_Click、RemovePermission_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-050 [FileServicesSharesView](../../Client/RelaxKonOS.Client/Apps/FileServices/Views/FileServicesSharesView.axaml) | DataGrid；入口：NewShareCommand、EditShareCommand、DeleteShareCommand | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-051 [FileServicesUsersView](../../Client/RelaxKonOS.Client/Apps/FileServices/Views/FileServicesUsersView.axaml) | DataGrid；入口：ToggleUserCommand、SetSambaPasswordCommand | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-052 [FileServicesWorkspace](../../Client/RelaxKonOS.Client/Apps/FileServices/Views/FileServicesWorkspace.axaml) | ContentControl；入口：RefreshCommand、Navigation_Click | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-053 [HostAdministratorCredentialsDialogView](../../Client/RelaxKonOS.Client/Apps/FileServices/Views/HostAdministratorCredentialsDialogView.axaml) | TextBox；入口：Cancel_Click、Confirm_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |

### Git

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-054 [GitBranchesView](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitBranchesView.axaml) | DataGrid；入口：CreateBranchCommand、Checkout_Click、Delete_Click | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-055 [GitClientWorkspace](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitClientWorkspace.axaml) | ContentControl；入口：RefreshCommand、NavigationButton_Click | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-056 [GitConflictResolutionDialog](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitConflictResolutionDialog.axaml) | 入口：Close_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-057 [GitConflictResolutionView](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitConflictResolutionView.axaml) | TextBox；入口：ContinueConflictCommand、AbortConflictCommand、RefreshCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-058 [GitDiffDialog](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitDiffDialog.axaml) | 入口：Close_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-059 [GitHistoryView](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitHistoryView.axaml) | DataGrid；ScrollViewer；入口：RevertCommand | 时间/阶段/目标；空记录与失败区别；复制与刷新；持续订阅释放 | 结构初查完成，待流程深审与运行验收 |
| UI-060 [GitLoginRequiredView](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitLoginRequiredView.axaml) | 展示/容器，随父入口联审 | 认证入口、能力/权限区别；重登录返回原任务 | 结构初查完成，待流程深审与运行验收 |
| UI-061 [GitLogView](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitLogView.axaml) | TreeView；ScrollViewer；TextBox；ContextMenu；入口：CreateBranchCommand、FetchCommand、RefreshCommand | 时间/阶段/目标；空记录与失败区别；复制与刷新；持续订阅释放 | 结构初查完成，待流程深审与运行验收 |
| UI-062 [GitOverviewView](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitOverviewView.axaml) | ScrollViewer；入口：PullCommand、PushCommand、FetchCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-063 [GitProjectPickerView](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitProjectPickerView.axaml) | ScrollViewer；入口：RefreshRepositoriesCommand、OpenProject_Click、OpenFolderCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-064 [GitPushDialog](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitPushDialog.axaml) | TreeView；入口：AllCommits_Click、Push_Click、Cancel_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-065 [GitRemoteBranchPickerDialog](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitRemoteBranchPickerDialog.axaml) | 入口：Confirm_Click、Cancel_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-066 [GitRemotesView](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitRemotesView.axaml) | DataGrid；入口：AddRemoteCommand、RefreshRemotesCommand、Edit_Click | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-067 [GitWorkspaceView](../../Client/RelaxKonOS.Client/Apps/Git/Views/GitWorkspaceView.axaml) | ScrollViewer；TextBox；入口：SelectAll_Click、RefreshChangesCommand、Commit_Click | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |

### ImageViewer

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-068 [ImageViewerView](../../Client/RelaxKonOS.Client/Apps/ImageViewer/Views/ImageViewerView.axaml) | ScrollViewer；入口：ZoomOutCommand、ZoomInCommand、FitToViewCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |

### Notepad

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-069 [NotepadSettingsView](../../Client/RelaxKonOS.Client/Apps/Notepad/NotepadSettingsView.axaml) | 入口：CloseSettingsCommand | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-070 [NotepadView](../../Client/RelaxKonOS.Client/Apps/Notepad/NotepadView.axaml) | TextBox；Menu；入口：NewDocumentCommand、OpenDocumentCommand、SaveCommand | 内容空间与焦点；未保存关闭；身份/会话绑定；断线输入与剪贴板 | 结构初查完成，待流程深审与运行验收 |

### PortForwarding

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-071 [PortForwardingConnectionView](../../Client/RelaxKonOS.Client/Apps/PortForwarding/Views/PortForwardingConnectionView.axaml) | TextBox；入口：SaveConnectionSettingsCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-072 [PortForwardingEditorDialogView](../../Client/RelaxKonOS.Client/Apps/PortForwarding/Views/PortForwardingEditorDialogView.axaml) | ScrollViewer；TextBox；入口：Cancel_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-073 [PortForwardingForwardsView](../../Client/RelaxKonOS.Client/Apps/PortForwarding/Views/PortForwardingForwardsView.axaml) | 入口：OpenCreateForwardCommand、TestSelectedCommand、OpenEditForwardCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-074 [PortForwardingMainView](../../Client/RelaxKonOS.Client/Apps/PortForwarding/Views/PortForwardingMainView.axaml) | ScrollViewer；ContentControl；入口：RefreshCommand、NavigationButton_Click | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |

### Proxy

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-075 [ProxyConnectionsView](../../Client/RelaxKonOS.Client/Apps/Proxy/Views/ProxyConnectionsView.axaml) | DataGrid；入口：RefreshConnectionsCommand、CloseConnectionCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-076 [ProxyGroupsView](../../Client/RelaxKonOS.Client/Apps/Proxy/Views/ProxyGroupsView.axaml) | ScrollViewer；TextBox；入口：SetRoutingModeCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-077 [ProxyLogsView](../../Client/RelaxKonOS.Client/Apps/Proxy/Views/ProxyLogsView.axaml) | DataGrid；入口：RefreshCommand | 时间/阶段/目标；空记录与失败区别；复制与刷新；持续订阅释放 | 结构初查完成，待流程深审与运行验收 |
| UI-078 [ProxyManagerWorkspace](../../Client/RelaxKonOS.Client/Apps/Proxy/Views/ProxyManagerWorkspace.axaml) | ContentControl；入口：RefreshCommand、NavigationButton_Click | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-079 [ProxyNetworkSettingsDialogView](../../Client/RelaxKonOS.Client/Apps/Proxy/Views/ProxyNetworkSettingsDialogView.axaml) | ScrollViewer；TextBox；入口：ToggleTunCommand、EmergencyDisableCommand、Cancel_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-080 [ProxyOverviewView](../../Client/RelaxKonOS.Client/Apps/Proxy/Views/ProxyOverviewView.axaml) | ScrollViewer；入口：ToggleProxyCommand、SelectNetworkSettingsPageCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-081 [ProxyProfilesView](../../Client/RelaxKonOS.Client/Apps/Proxy/Views/ProxyProfilesView.axaml) | ScrollViewer；TextBox；入口：UpdateAllSubscriptionsCommand、ViewRuntimeSubscriptionsCommand、InstallRuntimeCommand | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-082 [ProxySettingsView](../../Client/RelaxKonOS.Client/Apps/Proxy/Views/ProxySettingsView.axaml) | ScrollViewer；入口：UninstallRuntimeCommand、RollbackRuntimeCommand、InstallRuntimeCommand | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-083 [ProxySubscriptionContentDialogView](../../Client/RelaxKonOS.Client/Apps/Proxy/Views/ProxySubscriptionContentDialogView.axaml) | TextBox；入口：Close_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |

### Registry

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-084 [RegistryKeyDialogView](../../Client/RelaxKonOS.Client/Apps/Registry/RegistryKeyDialogView.axaml) | TextBox；入口：CancelCommand、SaveCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-085 [RegistryValueDialogView](../../Client/RelaxKonOS.Client/Apps/Registry/RegistryValueDialogView.axaml) | TextBox；入口：CancelCommand、SaveCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-086 [RegistryView](../../Client/RelaxKonOS.Client/Apps/Registry/RegistryView.axaml) | DataGrid；TreeView；TextBox；Menu；ContextMenu；入口：NewValueCommand、NewKeyCommand、RefreshCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |

### ServerCenter

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-087 [ServerCenterDeploymentPage](../../Client/RelaxKonOS.Client/Apps/ServerCenter/Views/ServerCenterDeploymentPage.axaml) | ScrollViewer；TextBox；入口：RecoverCommand、ProbeHostCommand、OpenInstallationWizardCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-088 [ServerCenterHistoryPage](../../Client/RelaxKonOS.Client/Apps/ServerCenter/Views/ServerCenterHistoryPage.axaml) | DataGrid；TextBox；入口：LoadOperationHistoryCommand、RefreshOperationCommand、ClearOperationHistoryCommand | 时间/阶段/目标；空记录与失败区别；复制与刷新；持续订阅释放 | 结构初查完成，待流程深审与运行验收 |
| UI-089 [ServerCenterHostsPage](../../Client/RelaxKonOS.Client/Apps/ServerCenter/Views/ServerCenterHostsPage.axaml) | ScrollViewer；TextBox；入口：ConfirmHostKeyCommand、RemoveHostCommand、AddHostCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-090 [ServerCenterManagementDialog](../../Client/RelaxKonOS.Client/Apps/ServerCenter/Views/ServerCenterManagementDialog.axaml) | ScrollViewer；TextBox；入口：RepairCommand、UninstallCommand、Close_Click | 忙碌关闭和修改确认已修复；继续目标/事实/未知写入及真实业务 | 关闭保护实际视图 headless 回归通过，完整验收待完成 |
| UI-091 [ServerCenterWorkspace](../../Client/RelaxKonOS.Client/Apps/ServerCenter/Views/ServerCenterWorkspace.axaml) | ContentControl；入口：NavigationButton_Click | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-092 [ServerInstallationWizardView](../../Client/RelaxKonOS.Client/Apps/ServerCenter/Views/ServerInstallationWizardView.axaml) | ScrollViewer；TextBox；入口：ChooseBundle_Click、ChooseServerBundleCommand、ShowHostAddressesCommand | 选择器恢复、远端草稿关闭及密码保留已修复；继续原生本地窗口/提交恢复 | 远端关闭保护实际视图 headless 回归通过，完整验收待完成 |
| UI-093 [SshFileBrowserView](../../Client/RelaxKonOS.Client/Apps/ServerCenter/Views/SshFileBrowserView.axaml) | DataGrid；TreeView；ScrollViewer；TextBox；ContextMenu；入口：Back_Click、Forward_Click、Up_Click | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-094 [SshFileDeleteDialog](../../Client/RelaxKonOS.Client/Apps/ServerCenter/Views/SshFileDeleteDialog.axaml) | 入口：Cancel_Click、Delete_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-095 [SshFileNameDialog](../../Client/RelaxKonOS.Client/Apps/ServerCenter/Views/SshFileNameDialog.axaml) | TextBox；入口：Cancel_Click、Save_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-096 [SshFilePropertiesDialog](../../Client/RelaxKonOS.Client/Apps/ServerCenter/Views/SshFilePropertiesDialog.axaml) | 入口：Close_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |

### Settings

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-097 [AliasOperationDialogView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/AliasOperationDialogView.axaml) | ScrollViewer；TextBox；入口：CancelCommand、SubmitCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-098 [AppDataClearDialogView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/AppDataClearDialogView.axaml) | 入口：CancelCommand、ClearCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-099 [AppPermissionDialogView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/AppPermissionDialogView.axaml) | ScrollViewer；入口：CancelCommand、SaveCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-100 [AppPermissionRequestDialogView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/AppPermissionRequestDialogView.axaml) | 入口：LaterCommand、DenyCommand、AllowCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-101 [LegalTextDialogView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/LegalTextDialogView.axaml) | TextBox；入口：CloseCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-102 [AboutPageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/AboutPageView.axaml) | 入口：CopyOfficialWebsiteCommand、OpenOfficialWebsiteCommand、CopyAllProductsWebsiteCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-103 [AccessibilityPageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/AccessibilityPageView.axaml) | 入口：ResetAccessibilityCommand、RetrySaveCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-104 [AccountSecurityPageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/AccountSecurityPageView.axaml) | TextBox；入口：OperateCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-105 [AppsPageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/AppsPageView.axaml) | 入口：DataContext.ShowAppDetailsCommand, ElementName=Root、OpenSelectedAppCommand、ClearSelectedAppDataCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-106 [DailySettingsPageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/DailySettingsPageView.axaml) | 入口：ResetDailySettingsCommand、RetrySaveCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-107 [DefaultAppsPageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/DefaultAppsPageView.axaml) | 入口：AddMappingCommand、RemoveCommand、ResetMappingsCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-108 [DeveloperPageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/DeveloperPageView.axaml) | TextBox；入口：DeveloperMode.RegeneratePairingTokenCommand、NetworkInspector.OpenCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-109 [EnvironmentPageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/EnvironmentPageView.axaml) | ScrollViewer；TextBox；入口：NewUserVariableCommand、EditUserVariableCommand、DeleteUserVariableCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-110 [HostNetworkView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/HostNetworkView.axaml) | TextBox；入口：ReloadCommand、OnAdapterClick、ApplyCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-111 [NetworkPageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/NetworkPageView.axaml) | TextBox；入口：SaveCommand、RefreshCommand、TestConnectionCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-112 [PersonalizationBackgroundView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/PersonalizationBackgroundView.axaml) | 入口：ChooseImageCommand、ResetBackgroundCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-113 [PersonalizationColorsView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/PersonalizationColorsView.axaml) | TextBox；入口：ResetAccentCommand、ImportThemeCommand、ExportThemeCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-114 [PersonalizationLayoutView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/PersonalizationLayoutView.axaml) | 入口：ResetLayoutCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-115 [PersonalizationPageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/PersonalizationPageView.axaml) | 入口：OnSectionClick | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-116 [PersonalizationStyleView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/PersonalizationStyleView.axaml) | 入口：ApplyRecommendedStyleCommand、ResetStyleCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-117 [SystemPageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/SystemPageView.axaml) | TextBox；入口：OnDailySettingsClick、OpenWorkspaceEnvironmentCommand、OpenPerformanceOptionsCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-118 [TimeLanguagePageView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/TimeLanguagePageView.axaml) | 入口：ResetTimeFormatsCommand、HostTime.ReloadCommand、HostTime.ApplyCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-119 [WorkspaceEnvironmentEditorView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/Pages/WorkspaceEnvironmentEditorView.axaml) | ScrollViewer；TextBox；入口：StageSetCommand、StageDeleteCommand、ReloadCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-120 [PerformanceOptionsDialogView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/PerformanceOptionsDialogView.axaml) | ScrollViewer；入口：CancelCommand、ApplyCommand、OkCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-121 [ServerHttpsDialogView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/ServerHttpsDialogView.axaml) | TextBox；入口：RefreshCommand、DeployCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-122 [SettingsHeaderView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/SettingsHeaderView.axaml) | TextBox；入口：BackCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-123 [SettingsView](../../Client/RelaxKonOS.Client/Apps/Settings/Views/SettingsView.axaml) | ScrollViewer；ContentControl；入口：OnQuickLinkClick、OpenPageCommand | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |

### TaskManager

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-124 [TaskManagerMainView](../../Client/RelaxKonOS.Client/Apps/TaskManager/Views/TaskManagerMainView.axaml) | ScrollViewer；TextBox；入口：SwitchToPerformanceCommand、SwitchToProcessesCommand、RefreshCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |

### Terminal

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-125 [TerminalSettingsView](../../Client/RelaxKonOS.Client/Apps/Terminal/TerminalSettingsView.axaml) | 入口：CloseSettingsCommand | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-126 [TerminalView](../../Client/RelaxKonOS.Client/Apps/Terminal/TerminalView.axaml) | Menu；入口：OpenAdministratorTerminalCommand、OpenSettingsCommand、OpenEnvironmentCommand | 内容空间与焦点；未保存关闭；身份/会话绑定；断线输入与剪贴板 | 结构初查完成，待流程深审与运行验收 |

### TextEditor

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-127 [EncodingActionDialogView](../../Client/RelaxKonOS.Client/Apps/TextEditor/EncodingActionDialogView.axaml) | 入口：ReopenCommand、SaveCommand、CancelCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-128 [EncodingDialogView](../../Client/RelaxKonOS.Client/Apps/TextEditor/EncodingDialogView.axaml) | 入口：CancelCommand、SelectCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |

### Tunnels

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-129 [TunnelDefinitionEditorView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelDefinitionEditorView.axaml) | ScrollViewer；TextBox；入口：SaveCommand、CloseCommand、DeleteCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-130 [TunnelDefinitionsView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelDefinitionsView.axaml) | DataGrid；入口：EditTunnelCommand、NewTunnelCommand | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-131 [TunnelLoginRequiredView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelLoginRequiredView.axaml) | 展示/容器，随父入口联审 | 认证入口、能力/权限区别；重登录返回原任务 | 结构初查完成，待流程深审与运行验收 |
| UI-132 [TunnelLogWindowView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelLogWindowView.axaml) | TextBox；入口：RefreshCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-133 [TunnelManagedFrpsConfigurationView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelManagedFrpsConfigurationView.axaml) | ScrollViewer；TextBox；入口：SaveManagedFrpsCommand、ReloadManagedFrpsEditingCommand、CloseButton_Click | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-134 [TunnelManagedFrpsDiagnosticsView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelManagedFrpsDiagnosticsView.axaml) | ScrollViewer；TextBox；入口：RefreshManagedFrpsDiagnosticsCommand、CloseButton_Click | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-135 [TunnelManagedFrpsView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelManagedFrpsView.axaml) | 入口：OpenManagedFrpsDiagnosticsCommand、OpenManagedFrpsConfigurationCommand、RefreshCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-136 [TunnelManagerView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelManagerView.axaml) | ScrollViewer；TextBox；ContentControl；入口：RefreshCommand、NavigationButton_Click | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-137 [TunnelOverviewView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelOverviewView.axaml) | 入口：RefreshCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-138 [TunnelProfileEditorView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelProfileEditorView.axaml) | ScrollViewer；TextBox；入口：ProbeExternalRuntimeCommand、SaveCommand、CloseCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-139 [TunnelRuntimeView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelRuntimeView.axaml) | TextBox；入口：UninstallRuntimeCommand、RollbackRuntimeCommand、ShowRuntimeDownloadCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-140 [TunnelServersView](../../Client/RelaxKonOS.Client/Apps/Tunnels/Views/TunnelServersView.axaml) | DataGrid；入口：EditProfileCommand、NewProfileCommand、OpenLogsButton_Click | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |

### WebServers

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-141 [NginxIntegrationConfirmationDialogView](../../Client/RelaxKonOS.Client/Apps/WebServers/Views/NginxIntegrationConfirmationDialogView.axaml) | 入口：CancelCommand、ConfirmCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-142 [WebServerInstancesPageView](../../Client/RelaxKonOS.Client/Apps/WebServers/Views/WebServerInstancesPageView.axaml) | ScrollViewer；TextBox；入口：RefreshCommand、DiscoverCommand、IntegrateCommand | 选择目标与刷新一致；空/失败/旧事实；危险确认；长列及键盘选择 | 结构初查完成，待流程深审与运行验收 |
| UI-143 [WebServerLoginRequiredView](../../Client/RelaxKonOS.Client/Apps/WebServers/Views/WebServerLoginRequiredView.axaml) | 展示/容器，随父入口联审 | 认证入口、能力/权限区别；重登录返回原任务 | 结构初查完成，待流程深审与运行验收 |
| UI-144 [WebServerManagerWorkspace](../../Client/RelaxKonOS.Client/Apps/WebServers/Views/WebServerManagerWorkspace.axaml) | ScrollViewer；TextBox；ContentControl；入口：CancelOperationCommand、NavigationButton_Click | 导航状态；读取失败保留编辑器；窄窗口；切页与焦点返回 | 结构初查完成，待流程深审与运行验收 |
| UI-145 [WebServerSiteDialogView](../../Client/RelaxKonOS.Client/Apps/WebServers/Views/WebServerSiteDialogView.axaml) | ScrollViewer；TextBox；入口：GenerateSiteBindingsCommand、RemoveBinding_Click、AddSiteBindingCommand | 草稿关闭、保存忙碌、读取及成功后刷新恢复已修复；继续会话/目标/未知写入 | 实际视图关闭与状态恢复受控回归通过，完整验收待完成 |
| UI-146 [WebServerSiteSaveErrorDialogView](../../Client/RelaxKonOS.Client/Apps/WebServers/Views/WebServerSiteSaveErrorDialogView.axaml) | 入口：Close_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-147 [WebServerSitesPageView](../../Client/RelaxKonOS.Client/Apps/WebServers/Views/WebServerSitesPageView.axaml) | DataGrid；入口：NewSiteCommand、EditSiteCommand、DeleteSiteCommand | 读取失败保留列表、旧事实写入门禁与迟到响应已修复；继续危险确认/长列/键盘 | 列表状态模型受控回归通过，完整页面验收待完成 |

### Welcome

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-148 [WelcomeView](../../Client/RelaxKonOS.Client/Apps/Welcome/WelcomeView.axaml) | ScrollViewer；入口：OpenNotepadCommand | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |

### 宿主与服务

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-149 [NetworkInspectorView](../../Client/RelaxKonOS.Client/Services/Diagnostics/NetworkInspectorView.axaml) | DataGrid；TabControl；ScrollViewer；TextBox；入口：Record_Click、Clear_Click、RequestName_Click | 主要任务与状态；窄窗口/长文本；键盘/无障碍；刷新与身份切换 | 结构初查完成，待流程深审与运行验收 |
| UI-150 [DownloadUrlDialogView](../../Client/RelaxKonOS.Client/Views/DownloadUrlDialogView.axaml) | TextBox；入口：CopyCommand、CloseCommand | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |

### 登录

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-151 [LoginView](../../Client/RelaxKonOS.Client/Views/Login/LoginView.axaml) | ScrollViewer；TextBox；入口：RelaxLogin_Click、SshLogin_Click、TogglePasswordVisibilityCommand | 探测忙碌/取消、认证迟到响应已修复；继续窗口生命周期与完整视觉/业务验收 | 局部流程修复及受控回归通过，完整验收待完成 |
| UI-152 [LoginWindow](../../Client/RelaxKonOS.Client/Views/Login/LoginWindow.axaml) | 展示/容器，随父入口联审 | 用户关闭取消、成功交接及语言订阅释放已修复；继续启动事件链/视觉验收 | 生命周期修复、实际控件 headless 回归通过，原生验收待完成 |
| UI-153 [SshHostKeyDialog](../../Client/RelaxKonOS.Client/Views/Login/SshHostKeyDialog.axaml) | 入口：Cancel_Click、Confirm_Click | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |

### 宿主与服务

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-154 [MainWindow](../../Client/RelaxKonOS.Client/Views/MainWindow.axaml) | ContentControl；入口：Minimize_OnClick、Maximize_OnClick、CloseWindow_OnClick | 窗口/全屏/缩放；启动与断线；菜单和焦点；恢复窗口边界 | 结构初查完成，待流程深审与运行验收 |

### Shell

| 编号 / 界面 | 结构证据 | 分别审查的重点 | 状态 |
|---|---|---|---|
| UI-155 [DesktopDisplayDialogs](../../Client/RelaxKonOS.Client/Views/Shell/DesktopDisplayDialogs.axaml) | ScrollViewer；入口：CancelOrSkipButton_OnClick、SaveButton_OnClick | 草稿差异与校验；忙碌锁定；取消/Esc/窗口关闭；失败保留目标及输入 | 结构初查完成，待流程深审与运行验收 |
| UI-156 [MacosShellLayoutView](../../Client/RelaxKonOS.Client/Views/Shell/MacosShellLayoutView.axaml) | ContentControl | 窗口/全屏/缩放；启动与断线；菜单和焦点；恢复窗口边界 | 结构初查完成，待流程深审与运行验收 |
| UI-157 [UbuntuShellLayoutView](../../Client/RelaxKonOS.Client/Views/Shell/UbuntuShellLayoutView.axaml) | ContentControl | 窗口/全屏/缩放；启动与断线；菜单和焦点；恢复窗口边界 | 结构初查完成，待流程深审与运行验收 |
| UI-158 [WindowOverviewView](../../Client/RelaxKonOS.Client/Views/Shell/WindowOverviewView.axaml) | ScrollViewer；入口：CardOpen_OnClick、CardClose_OnClick | 窗口/全屏/缩放；启动与断线；菜单和焦点；恢复窗口边界 | 结构初查完成，待流程深审与运行验收 |
| UI-159 [WindowsShellLayoutView](../../Client/RelaxKonOS.Client/Views/Shell/WindowsShellLayoutView.axaml) | ContentControl | 窗口/全屏/缩放；启动与断线；菜单和焦点；恢复窗口边界 | 结构初查完成，待流程深审与运行验收 |

本表登记 159 个 XAML 视图，包含展示组件与子视图；动态场景另见补充表。后续实现与验收须逐行更新状态。
