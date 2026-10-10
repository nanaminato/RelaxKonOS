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

真实登录页接续审查发现：手动改为另一地址后，已保存记录仍被选中，隐藏的旧密码仍可能参与提交。现在直接登录目标或账号变化时解除已保存记录绑定、清空回填密码、关闭该表单的密码保存选项并展开凭据输入；同一端点的大小写/默认端口/根路径规范化保留绑定，未绑定记录的手动密码仍可用于更正地址。SSH 异步回填额外核对当前地址与账号，不能仅依赖选择对象相同。

`Login.Tests` 的实际控件新增 4 项回归通过，覆盖等价端点、目标变化、账号变化、未绑定手动凭据；整套 Login 回归沙箱外通过。该新增规则已在共享客户端构建及 headless 控件验证，当前运行中的原生客户端尚未重启覆盖这次改动，原生复验待完成。

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

### D-007：证书申请与自签证书编辑器（P0-3）

2026-10-10 局部实现并通过受控回归。ACME 申请与自签证书弹窗接入统一草稿守卫，比较域名、邮箱、挑战方式、密钥算法、条款/公网确认及有效期。取消确认保留输入，确认放弃退出；读取/提交忙碌时取消、Esc、标题栏和 Alt+F4 一致拒绝关闭。字段与提交入口随状态锁定，公开提交方法与通用操作入口阻止直接重入，自签请求固定已经校验的有效期。

原申请页取消按钮把停止任务与关闭合在一起。现在独立保留“停止”操作，调用已有服务端取消流程；退出编辑不再隐式取消远端任务，停止后的完整权威状态仍待专项验证。自签表单增加正文滚动，两页操作区可换行。编辑器内增加校验反馈；缺少域名、算法、条款、邮箱、权限或有效期不再仅显示在被遮挡的工作区。打开新编辑器清除前一次编辑校验提示，预检非法输入不沿用原预检结果。

证据：`CertificateEditorChecks` 对两个实际编辑器完成 19 项 headless 检查，包括放弃确认、保留选项、非法输入无请求/页内错误可见、字段忙碌、单请求门禁、忙碌关闭、明确拒绝保留草稿、自签有效期快照和确认退出。最终 WindowPreviews `--ui-review-only` 与 Desktop 构建通过；最终增量构建 0 警告/0 错误，首轮仍有已有 5 项客户端警告。没有发送真实 ACME 申请或生成测试服务器证书。

仍需继续：认证/证书目标归属、预检迟到响应与关闭取消、取消请求和远端终态的区分、读取失败及写入结果未知、成功后刷新失败、真实证书任务与语言/主题/缩放/键盘视觉矩阵。当前不是完整证书界面验收。

### D-008：SMB 共享编辑器草稿与等待输入（P0-3）

2026-10-10 局部实现并通过受控回归。共享编辑器原先只在点击保存时手动禁用保存按钮，其他字段与关闭仍可操作。现在字段、页内操作及模态取消统一使用 `CanEditShareDraft`，等待路径风险确认/提权输入和执行宿主操作期间均锁定。请求开始时先设置执行忙碌再清除等待输入，避免两个阶段之间短暂开放其他操作。

草稿守卫比较共享名称、路径、说明、只读/启用/访客选项，以及权限行的主体和访问等级；嵌套权限修改、增删行同样需要放弃确认。取消确认保留输入，确认放弃退出。保留原保存方法的完整请求快照与成功结果关闭，不再用按钮的临时 IsEnabled 覆盖模型绑定；操作区可换行。

证据：`FileShareDialogChecks` 使用实际视图与读取接口替身，在有效能力/运行状态下完成 8 项 headless 检查，覆盖权限草稿、确认保留、等待输入门禁、直接保存防绕过、宿主忙碌、恢复和确认退出。最终 WindowPreviews `--ui-review-only` 通过，测试项目增量构建 0 警告/0 错误；客户端首轮编译仍有已有 5 项警告。本轮没有更改真实 SMB 共享、密码或 ACL，也未覆盖仍在运行中的原生桌面程序集。

读取恢复已局部实现：能力、状态、共享、用户和连接信息全部读取成功后统一发布；任一失败保留原列表、选择与草稿，并使事实不可用于写入。编辑器提供刷新恢复入口，保存按钮绑定当前管理门禁。五阶段故障与恢复受控回归通过，首次部分读取不能发布事实。

共享编辑器打开时固定原共享 ID，刷新或改变选择不会重定向保存；原共享不再存在时拒绝提交并提供三语重选提示。提权完成后再次检查管理权限及共享存在性。受控回归覆盖改变选择仍写原 ID、原共享被移除后不写其他共享。

仍需继续：认证后的会话归属、编辑期间共享被远端修改的冲突处理、路径选择器子模态与迟到回调、未知写入核实、真实共享保存/拒绝/故障及完整视觉矩阵。独立 FileServices 测试已迁移到当前客户端接口，平台发现、生命周期刷新、输入序列化与安装路由受控回归通过，详见实施记录。

会话归属源码核查：`RemoteFileServicesClient.Send`/`ElevateAsync` 每次请求使用共享 `IAuthSession` 的当前地址，`FileServicesViewModel` 仅保留 client/权限，没有窗口登录快照。共享 ID、用户和路径守卫均不能证明请求仍属于原登录。需按稳定服务身份及当前用户/会话快照绑定窗口，在读取发布、提示返回、提权、写入和回执处理处核对；同服务器同用户重新登录也不能复活旧窗口。现有 Web 站点窗口快照可供复用设计参考，但不能据此登记 SMB 已修复。

进一步核查：`UsageMemoryScope.IsCurrent` 比较会话 ID、账号和工作区，适合偏好记忆的写入范围，不能代替窗口登录对象快照；Web 站点快照还比较当前用户及 Session DTO 对象引用。SMB 应采用后者的登录边界，并允许同一登录的隧道实际地址更新。需要验证同 ID 新对象、提权等待中换登录、读取间换登录和迟到写入回执；此项仍待实现。

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

### 2026-10-10：真实服务器恢复可达与原生登录检查

- 重新探测指定测试服务器，OPTIONS 认证端点返回预期 HTTP 405。使用提供的账号成功认证，GET 证书与 Web 服务列表均成功，当前各有 0 条记录；测试会话随后成功登出。凭据/令牌只在请求进程内存中，没有写入源码或本文。此记录验证真实 API 登录与有效空列表，不等于原生 UI 登录或证书/Web 写入验收。
- computer-use 已成功启动当时最新桌面构建，观察到中文浅色登录窗口及 TLS 指纹核对弹窗。用户已核对并手动确认，随后关闭密码保存选项并完成真实登录，进入桌面，连接栏显示指定服务器及测试账号；等待认证期间表单禁用。尚未验收最新凭据目标绑定修复、全套视觉及故障恢复场景。
- 真实页面暴露的已保存记录/隐藏密码目标绑定问题已修复并补充 4 项 Login 控件回归。共享客户端/测试构建通过（既有 5 项客户端警告/0 错误）；尚未覆盖运行中桌面输出，须在原生登录阶段结束后重启最新构建复验。
- 证书编辑器的 19 项受控回归已通过，见 D-007。当前证书与 Web 列表为空，真实申请/生成、部署、站点保存与错误恢复仍需独立业务场景，不能用只读空列表代替。
- 原生证书概览成功显示 0 条记录，自签证书表单可打开，中文说明、域名、算法、有效期及底部操作完整可见；未创建证书。该观察限于当前大屏、浅色中文环境和运行中构建。

### 2026-10-10：SMB 独立回归迁移

- FileServices 测试改为引用真实客户端项目及其本地化服务，删除旧本地化替身，按当前权限选项和等待输入状态更新断言；构建与整套运行通过。
- 安装验证使用当前 InstallationClient/InstallationTaskViewModel 与受控 HTTP 响应，检查 SMB 路由、确认载荷、幂等键、回执、重复提交门禁及完成后刷新，不调用真实安装。共享草稿视图另有 8 项受控检查通过；读取恢复、会话目标及原生业务验收仍待完成。
- 随后修复 SMB 部分读取发布与列表清空问题，新增五阶段失败/重试及首次部分读取检查，FileServices 整套回归通过。增加编辑器刷新入口及保存门禁后，WindowPreviews 的 `--ui-review-only` 回归通过；构建 5 个既有警告、0 错误。运行中的原生客户端尚未重启到这一版，因此没有登记该修复的真实服务器验收。
- 继续固定共享编辑器保存 ID，增加共享移除门禁和三语反馈；FileServices 整套回归在最终源码上通过（构建 5 个既有警告、0 错误）。本项尚未在真实服务器执行共享更新。
- 删除确认纳入统一等待输入/提权流程，确认期间禁用刷新及其他变更，保持本地确认与远端操作进度的区别；固定确认共享 ID，并在提权后检查共享仍存在。新增确认等待门禁及选择变化不会删除另一条共享的受控检查，FileServices 整套回归通过。未执行真实共享删除。
- 共享文件夹选择器移到共享编辑器的子模态，并在退出编辑器后释放选择回调。返回路径前检查草稿版本、忙碌状态和原路径，重复打开不再提交第二个选择请求。新增编辑器关闭后迟到结果、手动纠正路径不被覆盖的受控检查；原生嵌套焦点/尺寸及认证变化仍待验证。
- Samba 密码和用户启停入口补充直接调用门禁；输入结束和提权完成后复查原用户名、资格及启用状态，变化时显示三语刷新重选提示。新增密码输入期间原用户被移除时不发请求的受控检查，最终 FileServices 整套回归通过（5 个既有构建警告、0 错误）。未更改真实用户密码。
- SMB 刷新反馈区分单纯读取失败、成功回执后刷新失败和失败回执后刷新失败；成功明确要求只刷新状态、不重复提交，失败保留原问题反馈。新增成功后读取失败仍返回成功、事实禁用重复写入及明确反馈的受控检查；未知写入结果不在此修复范围内。
- 随后补充未收到回执的首步保护：发起写入即使旧事实失效，请求/解析异常提示结果未知，禁止立即重复提交并要求刷新核实。受控回归模拟服务端执行后丢失回执，检查反馈与重复写入门禁；整套 FileServices 回归通过。尚缺持久化操作身份、针对操作的权威结果核实及重启恢复，普通刷新不能证明密码变更等写入结果，不能据此标记未知写入流程完成。
- 共享编辑保存前比对打开时的配置快照（包含权限值集合），刷新发现远端变化时保留本地草稿并拒绝覆盖，三语提示重选。新增远端权限变化检查，FileServices 整套回归通过。协议尚无条件修订写入，此检查只覆盖已观察到的变化，不能防止最后一次读取之后发生的并发修改；服务端并发保护仍待实施。
- 删除确认也捕获共享配置快照，提权后发现已观察到的路径/权限等配置变化时拒绝删除，要求重选确认。新增确认期间共享路径改变时不提交的受控检查，FileServices 整套回归通过；未执行真实删除，同样尚缺服务端条件修订保护。
- SMB 安装及服务启停入口增加直接调用门禁；启停在提权后再次检查已观察到的运行状态。新增已停止服务的 Stop/Restart 命令直接执行不发送请求检查，FileServices 整套回归通过（5 个既有构建警告、0 错误）；未操作真实服务。
- 对明确的 HTTP 权限/输入拒绝增加三语状态码反馈，避免通用拒绝被误报为未收到回执；超时及其他不确定响应继续保留未知结果语义。新增 HTTP 403 拒绝与丢失回执不同反馈的受控检查。
- 原生真实服务器文件服务概览显示 Linux/Samba 未安装，启停按钮禁用；共享页为空，新建/编辑/删除禁用。未安装软件或更改服务。该证据来自此前启动的运行版本，不能验收之后的恢复/目标修复。中文共享导航在 960 像素应用窗口中截断，源码改为换行；共享客户端/XAML 构建通过，最终原生复验待重启最新构建。
- 文件服务工作区状态栏由固定单行省略改为换行，并限制最大高度、提供垂直滚动，完整恢复建议可直接阅读。XAML 构建及 `--ui-review-only` 回归通过；该检查不代表小窗、多语言和 DPI 的最终原生视觉验收。
- 紧凑文件服务导航改用可换行容器，长共享入口限制宽度并换行，避免横排超出窄窗口。XAML 构建通过；真实小窗、长语言及焦点顺序尚待原生复验。
- 共享保存参数必须匹配当前编辑器的新建/编辑模式，不能把编辑草稿直接提交成新共享。新增错模式直接保存不发请求的受控检查，FileServices 整套回归通过；该门禁不替代服务端修订条件和会话归属检查。
- 补充通用 HTTP 408/500 写入异常回归：结果仍为未知、旧事实失效、立即重复保存不增加请求。测试项目增量构建 0 警告/0 错误，FileServices 整套运行通过；该检查不证明具体服务端 problem code 的执行结果或跨会话隔离。
- SMB 窗口开始接入打开时登录快照：服务身份及用户/会话对象引用固定，读/管理/安装入口检查当前性；逐次读取之间、统一发布前、提权凭据发送前、写入前和回执后复查。新增提权提示期间登录改变不发送写入的受控检查。尚需会话变化主动清理、三语反馈、完整迟到响应和同 ID 新登录回归，以及 HTTP 派发阶段隔离，不能登记全流程已完成。
- 检测到窗口登录失效时清理共享/用户列表、选择及路径/权限草稿，失效路径选择版本，并提供三语关闭重开提示。受控检查验证提权提示期间换登录不会发送管理员凭据，草稿清空且反馈正确；FileServices 整套回归通过。主动事件订阅与完整派发/迟到隔离仍待完成。
- 文件服务工作区在附着时订阅认证状态事件，失效清理调度到 UI 线程；脱离时取消订阅，重附着避免重复注册。共享客户端及 WindowPreviews 构建通过，`--ui-review-only` 回归通过。本次已有视图回归未覆盖 App 的事件附着/脱离链，须补充生命周期验证；HTTP 派发隔离仍待实施。
- 会话失效后共享字段不可编辑，关闭门禁独立于会话有效性，保留取消/关闭出口；仅真实忙碌或输入等待阻止关闭。新增旧草稿锁定但关闭仍可用的受控检查，FileServices 整套回归通过。原生会话变化下模态恢复仍待验证。
- 本地确认返回和每次提权重试前复查窗口会话；迟到 HTTP 拒绝及写入后刷新异常优先保留登录结束反馈，不恢复成功结果。新增旧登录迟到 HTTP 403 不能覆盖会话失效提示的受控检查，FileServices 整套回归通过。完整派发阶段身份隔离仍待完成。
- 新增能力读取返回时登录变化的受控检查，验证后续状态请求不发送、旧事实不发布且显示三语会话结束反馈；FileServices 整套回归通过，增量构建 0 警告/0 错误。这不覆盖 HTTP handler 等待取令牌期间的切换。
- 共享 `AuthenticatedHttpHandler` 捕获进入处理器时的服务身份、用户和会话对象，在取令牌后、缓冲内容后及 401 刷新前后核对，登录变化时不发送初次/重试请求，并释放旧响应。WorkspacePreferences 整套回归通过，新增同 ID 新会话在初次取令牌和 401 重试时阻止派发的检查；构建 5 个既有警告、0 错误。尚需把 SMB 窗口快照传入 HTTP 层，覆盖进入处理器前的身份切换，不能登记完整派发隔离已完成。
- SMB HTTP 请求和提权请求在进入 HTTP 管线前通过请求选项绑定构建请求时的登录身份，认证处理器取令牌前复查绑定，并在后续等待边界继续检查。新增同 ID 新会话在进入处理器前发生时不取令牌、不达传输层的受控检查，WorkspacePreferences 整套回归通过。此绑定与窗口门禁共同覆盖当前同步调用链；真实隧道更新、事件生命周期及服务器完整业务复验仍待完成。
- 登录失效清理进一步移除平台能力、运行状态、版本及连接地址，并重置共享标志；旧窗口不再保留原服务器事实。新增事实清理检查，FileServices 整套回归通过（5 个既有构建警告、0 错误）。原生登录切换表现仍待验收。
- 登录失效后的平台标题显示三语“登录已结束”，说明区域提示关闭重开，不再误显示为等待加载。构建及 FileServices 回归通过；原生视觉复验仍待完成。
- 新增旧登录迟到成功回执检查：不继续刷新、不恢复草稿/管理权限、不返回可用于关闭编辑器的成功结果，保留会话结束反馈；FileServices 整套回归通过，增量构建 0 警告/0 错误。此项为状态模型验证，原生关闭与跨会话业务仍待验收。
- Samba 密码提示返回时优先检查窗口会话，登录结束不再被用户状态变化提示覆盖，也不发送密码请求；新增对应受控回归，FileServices 整套构建/运行通过。未修改真实密码。
- 路径/删除确认的取消结果及提权否定结果也先复查窗口会话，避免迟到取消或权限提示覆盖登录结束反馈。新增确认返回取消同时登录结束的受控检查，FileServices 整套构建/运行通过；原生事件生命周期仍待验证。

## 后续逐项记录模板

### 2026-10-10：端口转发编辑器首批处理

- 编辑器接入共用草稿关闭守卫，比较目标地址、本地端口及 SSH 连接/密码字段；忙碌期间阻止按钮及窗口关闭，正文禁用，弹窗内显示状态和进度，操作区支持换行。
- 客户端/XAML 构建通过，5 个既有警告、0 错误；已有 `--ui-review-only` 回归通过。该套回归尚无端口转发编辑器专项案例，不能登记此编辑器流程验证通过；直接命令门禁、固定更新目标、持久化失败、密码清理、原生 SSH 业务与视觉矩阵仍待审查。
- 随后前移 Start/Update/保存设置/打开编辑器的忙碌门禁，避免重复调用先写设置或清空草稿；更新操作固定选中 ID。新增等待提交期间重复命令不重复写设置/请求、不清空草稿，以及拒绝后保留输入的状态模型检查，`--ui-review-only` 回归通过。实际弹窗关闭、选中目标与刷新冲突、密码清理及真实 SSH 业务仍待验证。
- 连接设置持久化异常转为编辑器状态反馈并中止转发提交，不再从命令入口抛出。新增设置写入拒绝时不启动转发、保留主机/密码草稿的受控检查，`--ui-review-only` 回归通过。实际文件存储的原子性、密码关闭清理仍待审查。
- 编辑器实际返回后清空 SSH 密码，取消放弃确认不会结束等待，因此保留当前输入；管理窗口 Dispose 也清除密码。增加 Dispose 密码清理受控检查，构建及 `--ui-review-only` 回归通过。App 弹窗返回/owner 关闭链和原生密码生命周期仍待专项验证。
- 新增实际 PortForwardingEditorDialogView 与窗口管理器的 headless 回归，检查编辑触发放弃确认、保留确认保持草稿、忙碌阻止关闭、确认放弃返回取消；构建与 `--ui-review-only` 通过。原生窗口/SSH 及 App 密码返回链仍待验收。
- 打开转发编辑器时固定目标 ID，新建/编辑互斥；编辑期间选择和列表刷新不回填覆盖输入。保存检查原 ID 仍存在，更新使用固定 ID。新增选择另一条并刷新仍保留草稿、更新仍发原 ID 的受控检查，构建及 `--ui-review-only` 回归通过；目标消失反馈、配置变化及原生生命周期继续审查。
- 原转发消失时更新入口显示三语重选提示，保留草稿，不保存 SSH 设置或派发更新；新增对应受控检查，构建及 `--ui-review-only` 通过。真实进程消失、配置变化及原生生命周期仍待验证。
- 编辑转发捕获原目标的地址、端口、协议和路径配置；更新前发现这些配置变化时拒绝覆盖，保留草稿并提示重选。新增同 ID 配置变化不保存设置/更新的受控检查，构建及 `--ui-review-only` 通过。服务层原子更新与真实进程生命周期仍待验证。
- 实际设置存储原本吞掉 IO 异常，服务先更新内存配置。现在保存失败向 UI 返回，文件成功保存后才更新内存，避免假成功和内存/磁盘不一致。构建及现有设置失败/UI 回归通过；实际文件故障注入仍待补充。
- 服务层 `UpdateAsync` 源码核查发现先移除原进程再创建替代，替代启动失败会丢失原连接。必须继续处理替换失败恢复和本地端口变化语义，尚未修复，不能登记完整更新业务通过。
- 随后调整服务替换顺序：纯路径变化原进程更新 URL；其他配置先创建独立替代进程，启动成功后按原对象移除旧进程，原目标期间变化则清理本次替代。原连接占用端口会触发正常备用端口选择，使用返回实际 URL。构建及现有 UI 回归通过；当前回归模拟服务而未覆盖真实进程替换，故障注入、取消、事件发布和真实 SSH 更新必须专项验收。
- 替代启动返回后、移除原进程前复查取消，取消时仅清理本次替代并保留原连接；构建通过。该取消窗口尚缺真实/受控进程故障注入，不能用编译登记流程通过；进程退出与替换提交的竞态仍需继续核查。
- 设置存储路径由启动配置显式提供，实际服务/存储回归使用隔离临时路径模拟父目录被文件占用：保存抛出 IO 错误，服务内存设置保持原值。构建及 `--ui-review-only` 回归通过，测试未读取或改写用户真实设置；覆盖的是目录故障，原子替换阶段故障仍待验证。
- 替换阶段故障注入使用目录占用目标文件位置，验证失败不改变内存且清理临时文件。存储改用唯一临时文件，失败清理；读取访问异常使用默认配置，保存错误仍报告。首轮回归暴露读取访问异常并修复，最终构建及 `--ui-review-only` 通过。尚未覆盖已有文件锁定、磁盘满或进程崩溃恢复。
- 编辑已有转发时 Start 直接调用被拒绝，不保存设置或创建新连接；新增模式错配受控检查，构建及 `--ui-review-only` 通过。真实 SSH 替换及原生生命周期仍待验收。
- 目标请求在保存连接设置之前解析并复用服务校验，提交冻结校验后的请求。新增非 loopback 目标和非 HTTP/HTTPS 协议不会保存设置或发起转发、保留输入的受控检查；构建及 `--ui-review-only` 通过。真实 SSH 替换/进程竞态仍待验证。
- 目标 URL 含 userinfo 或 fragment 时拒绝解析，不再静默丢弃输入的凭据信息/片段；新增非法输入不保存设置、不启动转发检查，构建及 `--ui-review-only` 通过。测试只使用虚构凭据。
- 修正 `localhost:port` 简写解析：无 `://` 时按 HTTP 解析，完整 URL 保留显式协议并继续校验。提交回归使用简写实际走到模拟服务并验证忙碌/失败保留原输入，构建及 `--ui-review-only` 通过。
- SSH 连接页字段和保存按钮在忙碌期间统一禁用，避免连接执行时继续修改界面参数。XAML 构建通过；原生忙碌场景仍待验证。
- 端口转发模型释放状态阻止后续刷新、设置保存及提交，排队到 UI 线程的服务刷新不会继续读取已关闭窗口；Dispose 幂等并清密码。新增释放后刷新/Start 不读取、不保存及密码清理检查，构建与 `--ui-review-only` 通过。已发起操作关闭后的迟到结果仍待进一步核查。
- Start/Update/Remove 迟到结果在模型已释放时不回填选择/状态，统一操作返回不再触发已关闭编辑器的成功回调，异常也不回写状态。新增窗口释放后模拟启动成功不刷新、不回填、不调用关闭的受控检查，构建及 `--ui-review-only` 通过。后台进程的完整生命周期及原生 owner 关闭仍待验收。
- 连接测试响应在模型释放后不更新状态；新增启动迟到失败不覆盖已释放窗口状态的受控检查，构建及 `--ui-review-only` 通过。实际网络测试取消和后台进程生命周期仍待验证。
- 连接探测使用窗口生命周期取消令牌，Dispose 取消等待。新增本机回环服务器接受请求但不回应的真实 HTTP 检查，关闭后探测及时结束且状态不变。首轮测试未泵送 UI 继续队列导致等待超时，修正 headless 等待后整套 `--ui-review-only` 在沙箱外通过；不连接外部服务。后台 SSH 进程生命周期仍待验收。
- 服务设置保存串行化，磁盘写入和内存发布属于同一次锁保护；连接解析一次读取不可变设置快照，避免主机、用户、端口跨保存混用。新增隔离临时文件的 12 次并发保存后内存/磁盘一致及无临时残留检查，构建和沙箱外 `--ui-review-only` 回归通过。
- Start 服务入口在查找复用和解析 SSH 身份前检查取消；已取消请求不改 URL、不启动或登记进程。新增实际服务入口取消返回 OperationCanceledException 且无登记转发的检查，构建和沙箱外 `--ui-review-only` 通过；启动期间/替换期间的进程故障注入仍待完成。
- 真实 SSH 环境只读探测：指定测试服务器 22 端口可达，返回 OpenSSH 握手；本机 OpenSSH 客户端可用。未发送账号/密码、未修改信任记录或建立转发。此证据为后续真实 SSH 业务验证的环境准备，不代表认证或转发通过。
- 用户明确要求直接信任展示的测试主机公钥；重新获取并核对同一 ED25519 指纹后写入该主机 OpenSSH 信任记录。未更改其他信任策略或发送密码，真实认证/转发仍待验证。
- 服务复用转发时增加 SSH 主机、用户及端口匹配，运行记录保存非秘密连接身份；更新的纯路径复用也要求身份匹配，避免切换设置后复用另一主机的隧道。构建及既有沙箱外 `--ui-review-only` 回归通过；真实跨主机复用专项仍待验证。
- SSH 密码路径源码核查：辅助脚本只读取子进程环境，没有嵌入密码；启动后移除父 Process.StartInfo 持有的密码环境值。SSH 参数明确 StrictHostKeyChecking=yes，未知/变更密钥不进入自动接受流程。构建及既有回归通过；真实密码认证、拒绝变更密钥和辅助进程清理仍待专项验证。
- 新增显式 `--forward-runtime-only` 真实服务检查，输入仅从进程环境读取，使用隔离临时设置。首次 HTTP 检查遇到启动期间超时并清理，验证器增加有界启动等待后，真实密码 SSH 转发成功访问指纹匹配的 HTTPS 服务（HEAD 返回 HTTP 404），随后移除转发并清理隔离设置。源码/文档不含测试密码，辅助脚本不含密码。该证据证明真实启动/访问/移除，替换失败保留原连接、取消/竞态及原生操作仍待专项验收。
- 原生测试旧构建正常断开后返回登录窗口（客户端仍运行）；首次 Desktop 构建因 DLL 占用失败。正常关闭登录窗口后确认没有客户端窗口，重建最新 Desktop 成功，0 警告/0 错误。登录窗口返回时仍显示“已连接。正在打开桌面…”且连接按钮禁用，需在最新构建复查此状态恢复，不能用旧构建现象认定当前源码缺陷。最新原生重新登录尚未完成。
- 最新 Desktop 已重新启动并使用测试账号成功进入指定服务器桌面。启动恢复的服务器/用户名正确，密码为空且保存密码未勾选；填入测试密码后连接可用，登录完成无新增信任提示。此项证明最新构建启动及真实登录，断开返回状态、SMB/端口转发原生修复仍须继续复查，未把本次登录计为全部界面验收。
- 最新构建原生中文浅色复查：SMB 共享导航完整换行，服务器仍未安装 SMB；端口转发新建弹窗字段及底部状态/操作可达。修改本地端口后 Esc 打开放弃确认，确认层 Esc 返回且保留端口草稿；恢复初始值后 Esc 直接关闭。未启动 SSH 或安装 SMB。此项限于当前大屏，忙碌/故障/密码及小窗多语言仍待验收。
- 登录窗口重开时清除前次“正在打开桌面”状态，保留独立错误提示机制。新增实际 LoginWindow 生命周期检查，Login 整套回归在沙箱外通过；构建 5 个既有警告、0 错误。运行中的 Desktop 尚未包含此后续改动，须重启后复查断开返回，按钮是否可连接需结合密码输入判断。

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
| UI-012 [CertificateRequestDialogView](../../Client/RelaxKonOS.Client/Apps/Certificates/Views/CertificateRequestDialogView.axaml) | ScrollViewer；TextBox；入口：PreflightCommand、Request_Click、Cancel_Click | 草稿关闭、提交门禁及页内校验已修复，独立保留停止操作 | 实际视图受控回归通过，完整流程与运行验收待完成 |
| UI-013 [SelfSignedCertificateDialogView](../../Client/RelaxKonOS.Client/Apps/Certificates/Views/SelfSignedCertificateDialogView.axaml) | ScrollViewer；TextBox；入口：Create_Click、Cancel_Click | 草稿关闭、提交/有效期快照、正文滚动及页内校验已修复 | 实际视图受控回归通过，完整流程与运行验收待完成 |

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
| UI-049 [FileServicesShareDialogView](../../Client/RelaxKonOS.Client/Apps/FileServices/Views/FileServicesShareDialogView.axaml) | ScrollViewer；TextBox；入口：BrowsePath_Click、AddPermission_Click、RemovePermission_Click、RefreshCommand | 草稿关闭、等待输入/提交门禁与读取重试已修复；继续目标恢复 | 实际视图受控回归通过，完整流程及原生验收待完成 |
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
| UI-072 [PortForwardingEditorDialogView](../../Client/RelaxKonOS.Client/Apps/PortForwarding/Views/PortForwardingEditorDialogView.axaml) | ScrollViewer；TextBox；入口：Cancel_Click | 已接入草稿关闭、忙碌锁定与弹窗内反馈；继续目标、命令及密码生命周期 | 首批实现/构建通过，专项流程及原生验收待完成 |
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
