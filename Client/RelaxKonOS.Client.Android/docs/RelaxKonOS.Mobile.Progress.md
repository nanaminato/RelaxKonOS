# RelaxKonOS Mobile 实施进展

## 连接管理的宿主系统标记与登录页入口（已实现，2026-09-28）

- 登录页的两条设置入口改为同类形状：品牌标记下方两个文字链接（「添加 Windows 10/11 设备」「安装或管理服务器」），同侧对齐、上下排列。「安装或管理服务器」原来是一条整宽描边按钮，而这条界面上唯一的主动作是「登录」，整宽按钮会把它读成第二个主按钮。两者上下排列而非并排，是因为英文/日文文案在小屏上一行放不下两条链接。
- 连接管理里每条密码登录记录左侧的标记改为**该宿主的操作系统类别**，来源是新增的匿名只读路由 `GET /api/v1.0/server/host-operating-system`（只回答 `unknown` / `ubuntu` / `windows10` / `windows11` / `windowsServer`）。判定在服务端只有一处（`HostOperatingSystemDescriptor`）：Windows 工作站与 Server 靠 `RtlGetVersion` 的 `ProductType` 分开（`Environment.OSVersion` 在两者上都是 10.0.x），`BuildNumber >= 22000` 记作 Windows 11；Linux 只认 `/etc/os-release` 的 `ID=ubuntu`，其余发行版（含 Debian）与读不到的情况一律 `unknown`——宁可回落通用标记，也不套一个错的商标。这是 Server 上**唯一不需要凭据的信息面**，因此只带系统类别，不含账号、版本、配置或主机身份。
- 答案是**随记录保存**的显示投影（`SavedLogin.hostOperatingSystem`），落盘布局升 `RKC3`。按本仓库既有规则「布局升版即降级为空、不写迁移」，升级后旧的连接列表会被清空——密码仍在保险箱里，重新登录一次即可恢复记录与指纹登录。`null`（还没问过）与 `unknown`（问过，且不是这四类）是两个不同状态：前者下次打开列表会再问一次，后者不会再产生请求。
- 连接管理打开时会为「还没问过」的直连记录**并发补问一次**（`HostOperatingSystemLookup`，并发上限 4），答完即落盘并刷新列表。受管登录不参与：它的地址是隧道，只在打开宿主时才存在。失败与拒绝都不落盘——「问不到」不会被固化成结论，下次打开再试；重复答案不写盘，因此界面也不会无谓重组。
- 首部图例与每行标记共用同一份映射（`ui/icons/HostPlatformMark.kt`），所以图例不会宣传一条记录永远显示不出的标记，也不会漏掉记录能显示的标记。标记带本地化无障碍名称（`host_platform_*`，三语同名）。
- **未做**：Shell 内「更多 → 连接」页面仍不显示标记（那里原本不显示任何图标），桌面客户端的连接列表同样未加。两处都可以复用同一份映射，需要时再补。
- **本轮验证**：服务端完整套件通过（含新增 `HostOperatingSystemChecks`，独立入口 `--host-os-only`）；Android `:app:assembleDebug` + `:app:testDebugUnitTest` 通过（49 类 / 435 用例 / 0 失败），三语 `strings.xml` 各 752 键且键集一致。新增/扩展用例：`HostOperatingSystemTest`（线上名字解析）、`HostOperatingSystemLookupTest`（补问规则）、`ConnectionProfileStoreTest`（宿主列往返、只影响一条、重复答案不写盘、`RKC2` 文件降级为空）。

## Windows 10/11 所有者设备注册与登录（已实现；待真机验收，2026-09-28）

- Android 登录页现在接受由已注册 Windows 控制器创建的一次性配对码。它只接受桌面端既有的 base64 UTF-8 JSON 载荷（`version`、HTTP(S) `serverUrl`、`token`、`expiresAt`）；拒绝过期、非 HTTP(S)、含用户信息/查询/片段或非根路径的地址，不引入 Android 专用的第二种协议格式。
- 配对前，应用在 Android Keystore 生成不可导出的 P-256 密钥；私钥不进入文件、网络或备份。仅 `(serviceId, deviceId)` 落在 `noBackupFilesDir`，公钥 SPKI 与设备名通过现有 `accept-invitation` 路由注册。配对失败会清除刚生成的本地密钥和记录。
- 后续登录先向既有 `challenge` 路由请求一次性 nonce，再由 Keystore 使用 ECDSA SHA-256 签名并调用 `sign-in`。强生物识别可用时每次签名都要求确认；只有屏幕锁/弱生物识别时使用 Android 平台允许的五分钟解锁窗口；完全没有本机认证能力时密钥仍不导出。此路径从不采集、缓存或提交 Windows/Microsoft/本地管理员密码。
- 登录页的“添加 Windows 10/11 设备”位于品牌标记下方的主滚动内容中，不再固定在右上角而与小设备上的图标重叠。它进入独立页面后可粘贴配对码、打开免相机权限的系统 QR 扫描器，或选择本地图片交给内置 ML Kit QR 阅读器。扫描结果只回填到同一个输入框；解析成功后必须在确认服务器 HTTP(S) 地址与到期时间的对话框中再次确认，才会创建密钥或消耗邀请码。
- “安装或管理服务器”和“连接管理”已移至服务器地址之前。连接列表点击有可用保存密码的直连记录会直接连接；没有可用密码或连接失败时保留刚选服务器和登录标识在表单中以便输入密码。列表还独立列出“Windows 10/11 · 已配对的设备密钥登录”并可直接用 Keystore 密钥登录；密码登录记录左滑可进入“忘记密码”或“删除登录记录”的确认流程。尚未在真实 Windows 10/11 主机和 Android 设备上验证邀请码过期、取消生物识别、屏幕锁窗口过期和密钥失效后的端到端行为。
- “安装或管理服务器”现紧随“添加 Windows 10/11 设备”，而连接管理仍在服务器地址之前。连接管理的可滑动条目改为不透明表面，避免左滑操作层的文字与连接正文重叠；增加 Ubuntu、Windows Server、Windows 10、Windows 11 平台标记及 Canonical/Microsoft 商标归属说明。
- **本轮验证**：` :app:compileDebugKotlin :app:testDebugUnitTest --rerun-tasks` 使用 Gradle 9.7.1 成功（26 个任务全部执行）。

## 无电脑部署规划（2026-09-26）

新增 [总路线图](./RelaxKonOS.Mobile.Deployment.Roadmap.md) 及 AD01–AD08 独立计划，覆盖服务器初始化、应用部署、模板应用库、Docker/Compose、网站发布、Git/轻量编辑、终端/守护和运维恢复。

规划提交仅建立文档与导航，没有实现上述新功能、运行设备测试或新增功能验收证据。后续实现见下面按阶段记录的进展，不将“计划已建立”记为“功能已完成”。

后续实施以 `ADxx-Mn` 记录阶段、`ADxx-Tn` 记录验收；进度和证据只在本文件维护。首轮 R1 交付 AD01 首次安装、AD02 镜像部署、AD05 基本发布及 AD08 任务恢复。应用部署的真实 Docker 验收和服务器中心的真实宿主安装仍是必要条件，见各计划前置要求。

## AD05-M1：网站发布只读诊断（已实现；Android 编译与 JVM 验收，2026-09-27）

- “管理 → 网站”仅在服务器声明 `server.web-server` 时显示。它读取受管 Web 服务器的运行状态、配置语法检查、RelaxKonOS 所属站点及其 TLS 绑定；读取不会请求提权，也不会创建、覆盖、重载或启动宿主 Web Server。
- 应用与站点的关系只读取服务端的权威 `siteId`：Android 不会从端口、上游地址或域名推测关联。证书只显示服务端返回的域名和状态，绝不读取私钥、证书文件路径或挑战凭据。
- 页面明确把“宿主运行 / 配置语法检查 / 站点记录 / TLS 关联”分开呈现。它声明公网 DNS 传播和互联网可达性尚未验证；站点列表读取失败也不会被误写为“没有站点”。`server.certificates` 缺失时，TLS 关联同样显示为未核实。
- Android 网络层新增 Web Server 与证书只读 projections、按单一路径段编码的路由构造，以及 session-scoped repository；wire 测试覆盖服务器、站点、证书、配置检查、必填字段拒绝和动态 ID 编码。
- **本轮验证**：`:app:assembleDebug` 与 `:app:testDebugUnitTest` 均 `BUILD SUCCESSFUL`（Gradle 9.7.1；44 个测试类、409 个用例，0 失败 / 0 错误 / 0 跳过）；新增 `WebPublishingWireTest`。未执行真机、真实 Nginx/证书、DNS 或外网访问验证，因而 AD05-T1–T6 仍未完成。

## AD05-M2/M3：确认式 HTTPS 发布、持久化恢复与主机观测（已实现，2026-09-27）

- 共享协议新增 `WebsitePublishing` 契约与三条权威路由：提交发布、按应用读取历史、按操作读取状态。请求只含应用、受管 Web Server、域名、可选既有证书及明确确认；DNS 服务商令牌、证书私钥、Nginx 配置全文和联系人邮箱均不会出现在操作记录或 Android 状态中。
- 服务端 `WebsitePublicationCoordinator` 是发布流程中唯一会改变站点的编排器：它要求应用实际运行、上游监听在 loopback、站点未被另一应用使用、域名未被其他受管站点占用、证书覆盖请求域名，并要求当前会话已获得目标实例的 `nginxConfigurationWrite` 授权。新证书走既有 ACME HTTP-01 流程；既有证书只复用服务端验证过的 SAN。站点由现有 Nginx 受控写入路径生成（`nginx -t`、reload 和失败回滚），之后才绑定应用的权威 `siteId`。
- 发布账本 `data/website-publications.json` 以原子替换持久化幂等键、状态、阶段、关联 ID 与检查结果。每个应用同时只能有一个活动操作；服务重启把 queued/running 标为 `interrupted` 并要求重新核对，绝不重放 ACME 或站点写入。绑定目录失败时只还原本次写入的站点，不删除旧证书，也不影响应用进程。
- Android “管理 → 网站”现在提供受控发布卡：选择正在运行的回环应用与受管 Web Server，输入域名，明确提示先把 A/AAAA 指向宿主公网地址；可选择 SAN 匹配的受管证书，或填写证书联系人、同意条款并确认 HTTP 80 公网可达。提交先走手机现有的单次管理员授权，再提交带 UUID 幂等键的持久操作。网络掉线仅影响手机读取，宿主操作继续；重开页面可按应用历史恢复。
- 完成后不把“主机能看到 HTTPS”误写为全球成功：操作分别保留宿主上游、DNS、TLS 握手与 HTTP 响应检查的观察者、时间、通过/失败/尚未验证和问题码。Nginx/证书/绑定成功但 TLS 或 HTTP 无法由宿主确认时，操作仍说明配置完成，同时显示 `unverified`，而非声称应用已停止或公网已经可达。
- M4 保持条件阶段：项目尚未接入某个可授权的 DNS 提供商，也没有把内网应用通过 FRP 对外暴露的已批准需求；因此没有伪造 DNS 自动化、共享 DNS 凭据，或自动添加宽泛防火墙规则。已有的独立 Firewall 与 FRP 服务仍是未来的集成边界。
- **本轮构建**：服务端以独立 `BaseOutputPath` 编译成功（避免覆盖正在运行实例锁定的 Protocol DLL），仅有 3 条既有 CA1416 平台警告；Android `:app:assembleDebug` 使用缓存的 Gradle 9.7.1 成功。三套 Android `strings.xml` 均为 597 键、键集一致。真实 Nginx、ACME、DNS、端口转发/防火墙和真机外网访问未在此开发环境执行，仍须按 AD05-T1–T6 进行宿主验收。

## AD04-M1：Docker 资源浏览与受控操作（已实现；服务端真实宿主已验收，2026-09-27）

- “管理 → Docker”仅在服务器声明 `server.docker` 时显示。页面读取 Engine、容器、镜像、命名卷、网络和 Compose Stack；Stack 可展开读取服务状态。资源读取全部经现有认证 REST API，Android 不直接接触 Docker socket 或自行执行 YAML。
- 容器提供 start/stop，容器、Stack 与卷的删除均要求本机再次确认；服务端删除路径保留命名卷。Stack 编辑器支持 Storage Access Framework 导入或粘贴 YAML。
- 服务端新增保守准入检查：拒绝 `build`、特权与设备权限、bind mount（含相对/绝对路径）、外部资源和 Docker socket；通过准入的定义仍由 `docker compose config` 进行权威解析，客户端不会删除或改写未知 YAML 项。**变量引用同样在准入阶段拒绝**（`docker.compose_variable_unresolved`）：`docker compose config` 会把未设置的变量静默替换为空串且 exit 0，而本服务不提供变量输入面，放行等于让操作者批准一份、执行另一份。
- 卷列表可展开读取引用容器（`usedBy`，含已停止容器）；服务端在引用非空时以 `docker.volume_in_use` 拒绝删除，因此“停止应用”“移除容器”“删除持久数据”是三个分别表达的动作。**已用真实宿主验收**：运行中与已停止的容器都被计为引用并拒绝删除，删除项目保留命名卷，无引用后才释放成功。

## AD04-M2：持久 Stack 操作与恢复（已实现；服务端真实宿主已验收，2026-09-27）

- **同步 Compose 路径已不存在**。新增共享契约 `RelaxKonOS.Protocol/Docker/DockerStackOperationContracts.cs`：操作种类（deploy/start/stop/restart/delete）、状态（queued/running/succeeded/partialFailed/failed/cancelled/interrupted）、阶段、预览 DTO、部署请求与诊断 DTO；`DockerApiRoutes` 增加 `stacks/preview`、项目操作历史与活动操作、`stack-operations/{id}`（含 `/diagnostics`、`/cancel`）。
- 服务端新增 `DockerStackOperationCoordinator`（`IHostedService`）与宿主账本 `DockerStackOperationStore`（`stack-operations.json`，与 Compose 源同目录；`DockerComposePaths` 是两者唯一路径来源）。`deploy`、项目级动作用 `202` 返回操作记录，端点只提交与读取：**断开 HTTP、App 被回收或服务端重启后，结果仍可从 `stack-operations/{id}` 或项目历史读到**；`IDockerComposeService` 只剩协调器一个调用方。
- 幂等键与请求指纹绑定：同键同请求回放原操作，同键不同请求 `docker.stack_idempotency_conflict`，缺键或畸形 `docker.stack_idempotency_required`；客户端在重试同一份文档时复用键、请求变了就换键。同项目活动操作互斥（`docker.stack_operation_conflict`），不同项目并行，全局上限 `DockerCompose:MaximumConcurrentOperations`（默认 2）。
- 部署必须回传 `preview` 给出的 `definitionVersion`（名称 + YAML 的内容标识），不一致以 `docker.stack_definition_changed` 拒绝——“对某一份文档的批准”无法套用到另一份。
- 重启核对**不重放**：仍活动的操作在下次启动观察项目实际服务后置为 `interrupted`（`docker.stack_interrupted`），记录观察结果并给出恢复动作；引擎失联绝不上报为成功。

## AD04-M3/M4：预览、部署、部分失败与卷保护（已实现；服务端真实宿主已验收，2026-09-27）

- Android 侧新增 `DockerStackPreview`/`DockerStackOperation`/`DockerStackOperationDiagnostics` 与网关/仓库入口（每次变更生成 UUID 幂等键）；`DockerScreen` 的编辑面板先预览（显示定义版本、服务、命名卷、网络），再携带 `definitionVersion` 部署。项目详情新增“近期操作”区，展示种类、状态、问题码、阶段、逐服务观察结果与诊断输出，并按状态给出可执行动作：活动且 `cancellable` 时可取消，需要决策时可停止项目。发现他处发起的活动操作会继续按 1 s 间隔读取持久记录（上限 5 分钟），离开页面不影响服务端继续执行。
- 结果取自**观察到的服务**而非命令退出码：命令成功但服务未达目标状态、或命令失败但有容器残留，都是 `partialFailed`；两者都不声称整组原子回滚，也不声称恢复旧定义能撤销数据库迁移。`recoveryProblemCode` 只在需要操作者决策**且**能补充 `problemCode` 未表达的信息时出现（实现上与 `problemCode` 永不同值）。
- 诊断在写入口逐行脱敏、单行限 512 字符、最多保留末 120 行；丢弃行首即置 `DiagnosticsTruncated`，界面明示截断，读者不会把日志尾部误当全量。账本保留 200 条操作与 1000 条审计，先淘汰最旧的**终态**操作、活动操作永不被裁剪，审计行随后按仍在册的操作过滤。写这条校验时发现并修掉了一个真实缺陷：`diagnostics.Select(ProxyLogSanitizer.Sanitize)` 会绑定到 `Select` 的 `Func<T, int, TResult>` 重载，把**行下标当成最大长度**，于是第 0 行被截成「…」、第 1 行只剩 1 个字符；现在改为显式 lambda 加独立常量，并有一条断言钉住（校验会打印每行实际长度）。
- 拒绝文案两端各自成表：桌面把 `docker.compose_failed` 归入既有 `docker.problem.failed`，并为 12 个 stack 拒绝码新增 `docker.problem.*`；Android 新增 `ui/manage/docker/DockerLabels.kt`（`dockerProblem`/`dockerFailure`，`DockerLabelsTest` 覆盖），403/404/连接失败三种情形不合并。历史列表里的问题码仍按原样展示，因为它正是工单要引用的稳定标识。
- 服务端测试新增 `PASS DOCKER STACK`（`--stack-operations-only` 可单独运行）：路由与动作表、项目名/定义版本/问题码/引用格式规则、账本写入与重开、幂等回放与两类冲突、按项目互斥且跨项目独立、诊断逐行脱敏与限长与截断标记、成功/部分失败/失败三类分类、确认删除、取消、重启核对（并断言 `DeployCalls == 0`，即没有重放）。
- **本轮验证**：服务端完整套件 `RelaxKonOS.Server.Tests` 通过（含 `PASS DOCKER STACK`，exit 0；`--stack-operations-only`、`--stack-live-only` 可单独运行）；桌面客户端 `RelaxKonOS.Client` 编译通过（0 错误，4 个既有 warning）；Android `:app:assembleDebug` + `:app:testDebugUnitTest` 通过，**42 个测试类、406 个用例，0 失败 / 0 错误 / 0 跳过**（`DockerLabelsTest` 覆盖 12 个拒绝码），三套字符串各 **555 键**、键集一致，桌面三份 `docker.json` 各 **327 键**、键集一致且仅新增不删改，`aapt2 compile --dir res` 通过。APK：`app/build/outputs/apk/debug/app-debug.apk`（19,816,811 字节）。环境为 Gradle 9.7.1、JDK 21、SDK 位于本机 `D:\environments\Android\Sdk`（项目未提交 wrapper）。
- **真实 Compose 宿主验收（2026-09-27）**：环境为 **Docker 29.8.0（linux/overlayfs）+ Docker Compose v5.5.1**，入口 `RelaxKonOS.Server.Tests --stack-live-only`，输出 `PASS DOCKER STACK LIVE`。该检查用真实 `docker`/`docker compose` 驱动服务端自身的 `DockerComposeService` 与协调器（不是替身），依次验证：`config --format json` 的解析结果正是协调器所依赖的服务/命名卷/网络；部署一个「一服务常驻、一服务立刻退出」的定义时 `up` 仍 exit 0，而分类为 `partialFailed` 且 `Services` 记录的是引擎实际状态；用修复后的同一项目定义更新得到 `succeeded`；同键同文档再次提交回放**同一个** `OperationId` 而不新建部署；`docker volume rm` 在运行中与停止后都被拒（`docker.volume_in_use`）；删除项目后命名卷仍在、引用为空后才释放成功。无 Engine 或缺少 `alpine:3.20` 时该检查打印 SKIP 并返回，不会假通过，也不进默认套件。
- **这两次真实宿主运行抓到了两个只在真实 Engine 上才会暴露的缺陷（均已修复）**：① `DockerComposeService.ListServicesAsync` 与 `ListAsync` 的 `--format` 模板把标签名写成 `\"name\"`（面向 shell 的转义），而该参数直接进 `ProcessStartInfo.ArgumentList`，Docker 以 `failed to parse template: unexpected "\\" in operand` 退出 1 —— 结果 `ListServicesAsync` 恒返回空，于是**真实宿主上任何一次成功部署都会被判为部分失败**，停止的项目也不会出现在 `stacks` 列表里；改为正确的模板引号（`{{.Label "com.docker.compose.project"}}`）后修复。② 未设置变量被 `docker compose config` 静默替换为空串且 exit 0，现在准入阶段即以 `docker.compose_variable_unresolved` 拒绝（`$$` 字面转义与注释不算引用，均有断言钉住）。
- **尚未执行**：带认证的 HTTP 往返（`202` + 轮询整条链路）和 Android/iOS 真机矩阵。登录需要真实宿主凭据，验收不绕过认证；AD04-T3 的端口冲突与引擎失联注入、T6 的跨管理域归属也未执行，详见计划 §6。

## AD02-M1：应用部署只读接入（2026-09-26）

状态：**已实现待验证**（真实远端和设备矩阵待执行）；AD02 的 Android 客户端 M1–M4 已完成实现。

- “管理 → 应用部署”按 `server.application-deployments` 能力显示；手机列表/详情独立导航，Expanded 平板显示列表与详情双栏。仅提供查看和刷新，明确标注只读，不显示未实现的创建、启停、回滚或上传按钮。
- Gateway/DTO/Repository 消费当前 `/api/v1.0/application-deployments/applications`、`applications/{id}` 和 `/api/v1.0/docker/status`，不修改共享协议、不引入兼容路由。DTO 是只读字段投影，不保留配置值、秘密或原始响应正文。
- 独立展示 Docker 能力、Engine 状态、列表读取权限与普通执行身份限制。缺失 Docker 或运行时失联不隐藏已有应用记录；Engine 可访问不表示拥有部署权限，也不表示磁盘/端口预检通过。部署最终预检仍属于 M2 的服务端提交流程。
- 详情展示实际/期望状态、来源、工作负载、就绪检查方式、端口绑定、当前修订、运行偏差、活动操作和近期操作及镜像修订。就绪检查方式不是健康结论；未知枚举显示“未知”，空进度不编造百分比。列表和详情显示读取核验时间，手动刷新，不宣称实时订阅。
- 刷新失败移除旧成功数据并提示结果待核实；账号/服务器切换、登出清空状态。请求归属与递增请求编号共同拒绝过期响应，Repository 串行化本域鉴权刷新，避免列表和详情同时轮换 refresh token。此内存浏览器不构成 AD08 持久任务恢复。
- HTTP 授权中间件返回无正文 401/403 时保留状态码，分别触发既有一次刷新规则与权限提示；无结构 5xx 仍视为结果未核实。三语文案同步维护，图标复用桌面应用部署资源。
- 新增 JVM 覆盖：当前路由和 JSON 字段、null/未知状态/坏响应、不保留配置秘密、HTTP 错误边界、能力门控、运行时故障、刷新失联、账号与服务器隔离、过期详情、token 单次刷新；测试使用真实 `org.json`，不以 Android stub 代替解析验证。
- 本轮验证：`:app:assembleDebug`、`:app:testDebugUnitTest` 均通过，**40 个测试类、382 个用例，0 失败 / 0 错误 / 0 跳过**，其中本轮新增 25 个用例。三套字符串各 **423 键**，无重复且键集一致；`git diff --check` 通过。APK 位于 `app/build/outputs/apk/debug/app-debug.apk`（19,293,523 字节）。环境为 Gradle 9.6.0、现有 JDK/SDK；构建时临时替换 SDK 路径，结束后已逐字节还原 `local.properties`。
- 全量 `:app:lintDebug` **未通过**：5 个已存在于本轮起点提交的错误（下载 MediaStore API 29、`contentLengthLong` API 24、保险箱两处 API 24/28 检查、登录页 `LocalContext` 转 Activity），另有 65 个 warning、7 个 hint。报告为 `app/build/reports/lint-results-debug.html`；未抑制这些错误，也未将 Lint 计为通过。
- **尚未执行**真实 Docker/服务器及手机竖横屏、8/11 英寸平板、大字体、三语视觉验收；AD02-T1–T6 均不能因 M1 编译或单测通过而标为已验收。

## AD02-M2：镜像部署与受限配置（已实现待验证，2026-09-27）

- “管理 → 应用部署”从服务端模板目录读取支持的来源；镜像路径收集应用名、镜像引用和容器端口，固定 `127.0.0.1` 绑定。配置输入仅能添加受限名称/值对；秘密值以密码控件输入并只显示“已配置”，不会进入列表、快照或日志。它不提供任意宿主挂载、Dockerfile 或 shell 参数。
- 客户端先 `POST /applications` 创建定义，再 `POST /applications/{id}/deploy` 提交已确认部署；两个变更各有独立 UUID 幂等键，因此任一响应丢失后的重复请求仍由服务端归并，不会重复创建资源或重复排队。成功后刷新权威列表、自动打开对应详情，并只在操作仍为 Queued/Running 时轮询权威快照；失败仍保留服务端问题码供现有三语错误映射展示。
- 创建和部署仍是分离的幂等操作：先 `POST /applications`，再 `POST /applications/{id}/deploy`；两次提交各使用一个 UUID 键。成功后刷新权威列表、打开详情，并在操作为 Queued/Running 时轮询快照。资源限制、命名卷与站点绑定属于后续受管模板/发布工作流，未在此受限首条路径中暴露。
- AD02-M2 与任何 AD02-T 项均**未验收**：JVM 验证不替代服务器预检、真实镜像或设备测试。

## AD02-M3：操作维护（已实现待验证，2026-09-27）

- 已部署且状态为 Running 的镜像应用可在详情页请求停止或重启；Stopped 应用可请求启动。停止/重启先在客户端确认；所有动作都固定映射为 start/stop/restart 闭集路由，提交 `force=false`、`confirmed=true` 和独立 UUID 幂等键，随后复用权威快照观察。详情页也读取服务端已脱敏、长度受限的近期日志，并明确提示截断。
- 活动操作只有在服务端 `cancellable=true` 时才显示取消入口；取消同样只走 UUID 操作路由和独立幂等键，之后重新读取快照。删除必须单独确认，客户端固定提交 `deleteVolumes=false`，因此受管数据卷会保留。
- 详情页现在列出快照内的不可变修订，仅允许选择非当前修订来发起回滚。回滚需要单独确认，说明短暂服务中断及“数据卷不会随修订回滚”，并只提交服务端已知的 UUID、`confirmed=true` 与独立幂等键。删除仍未实现。
- M3 及 AD02-T1–T6 仍**未验收**，见本轮验证记录。

## AD02-M4：Java/.NET/Python 归档输入（已实现待验证，2026-09-27）

- 创建对话框消费服务端返回的四类模板（Image、JavaJar、DotNetPublish、PythonProject），不在 APK 中复制模板版本或默认端口。Java、.NET 与 Python 均通过 Storage Access Framework 选择归档；归档由 `HttpURLConnection` 以 multipart 流直接传到 `/application-deployments/uploads`，不读入内存，之后只将服务端暂存引用传入部署操作。
- Java/.NET/Python 可选运行时/基础镜像参数受服务端模板验证；Python 强制填写模块入口，.NET 可选择自包含发布；Web 使用 HTTP `/` 就绪检查，Worker 使用 Process 检查。归档流不冒充通用文件上传的续传能力；大包、过期引用、切账号、真实解压和引擎行为仍需 AD02-T2/T6 的真实环境验收。
- AD02-M4 及 AD02-T2/T6 均**未验收**。

## AD02：本轮实现与验证记录（2026-09-27）

- 聚焦 `ApplicationDeploymentWireTest`、`DeploymentHttpTest` 和 `DeploymentBrowserTest` 通过，覆盖服务端模板读取、四来源归档暂存/部署的 wire 合同、秘密配置标记、生命周期、回滚、删除保卷和操作幂等键。
- 全量 `:app:testDebugUnitTest` 通过：**393 个用例，0 失败**。`git diff --check` 通过。
- 全量 `:app:lintDebug` 未通过，报告有本轮起点已有的 **5 个错误、75 个 warning**（MediaStore、`contentLengthLong`、两处 Keystore API、登录页 Context 转 Activity）；新增应用部署文件没有 Lint 错误。报告：`app/build/reports/lint-results-debug.html`。
- 未执行真实 Docker/服务器、SAF 大包、手机竖横屏/平板/大字体和三语视觉验收；它们仍是 AD02-T1–T6 的阻塞验证条件，不能以本轮 JVM 测试替代。

## AD03-M1 / M2：模板应用库（已实现待验证，2026-09-27）

- Shared Protocol 定义版本化用途模板、受限字段和值、安装请求和安装结果；服务端只从可信内置目录读取首批个人网站、状态监控、文件服务与 Webhook 模板，目录不接受脚本、Dockerfile、宿主路径或任意 UI 代码。
- `GET /application-deployments/catalog` 返回目录，`POST /catalog/install` 以模板 ID 与精确版本重新验证字段、生成受限 Image 部署定义并排队 AD02 操作。安装记录在应用及不可变修订中保留模板 ID/版本；刷新目录不会修改已安装实例。
- Android 展示“从模板安装”入口、模板用途、端口、持久数据位置与维护说明，按服务端字段生成原生文本/秘密控件。秘密值只在提交请求中存在，不进入列表、快照、摘要或日志；未知 schema、撤回模板和缺失能力都会禁用安装。
- 目录协议现在明确携带服务端校验过的来源标识；Android 保留来源、支持的平台及最小资源字段，并在安装前阻断未受信任来源、未知 schema/字段类型、能力缺失、Docker 不可用或架构不匹配。详情页显示实例绑定的模板 ID 与版本，因此目录刷新不会被误认为更新了既有实例。
- **本轮自动化验证（2026-09-27）**：Android `:app:testDebugUnitTest` 通过，**401 个 JVM 用例，0 失败/错误/跳过**；`ApplicationDeploymentWireTest` 覆盖目录版本/来源、秘密字段无默认值，以及 schema、信任、字段、能力、Docker 运行时和平台不兼容时的本地阻断。服务端 `dotnet run --project RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj -- --deployment-progress-only` 也通过：确认 4 个内置目录模板的受信任来源、schema、平台、能力、资源和闭集字段契约，并验证部署日志的秘密脱敏与有界重放。该服务端验证不启动 Docker Engine，也不实际安装目录模板。
- **尚未验证**：Android 对 `GET /catalog`、`POST /catalog/install` 的联网安装闭环；设备上选模板、填写字段、提交和打开服务；安装端点对缺秘密、非法字段/端口、版本不匹配及目录变化的独立 HTTP 拒绝；每个首批模板的真实 Docker 镜像许可/架构/资源、卷保留、升级、卸载与恢复；手机竖横屏、8/11 英寸平板、大字体及中英日视觉验收。模板更新说明、差异预览与显式实例更新流程（AD03-M4）尚未实现。因此 AD03-M3/M4 和 AD03-T1–T5 仍未验收。

## M0：Kotlin / Jetpack Compose 基线（已完成）

- 移除 `RelaxKonOS.Client.Mobile`、Avalonia Mobile、.NET for Android Host、其 XAML 页面、NuGet 包与 `.sln` 项目条目。
- `Client/RelaxKonOS.Client.Android` 现在是独立 Gradle 工程，使用 Kotlin、Jetpack Compose、Material 3 与 Android SDK。
- `AppCompatActivity` 承载 Compose 登录页与 Shell；按可用 Compose 宽度的 dp 切换 Compact、Medium、Expanded 导航布局。
- Kotlin `RelaxKonApi` 调用当前 `/api/v1.0` wire contract，并发送 `clientPlatform: "android"`。其字段名、路由与枚举值必须与 `RelaxKonOS.Protocol` 同步。
- 布局断点单元测试覆盖 599.99、600、839.99、840 dp。
- Android 构建与安装脚本已切换至 Gradle APK 输出；不再要求 .NET Android workload。

## V1：初版功能（对应 `RelaxKonOS.Mobile.V1.Design.md` §7）

| 阶段 | 状态 | 落点 |
| --- | --- | --- |
| V1-A 认证闭环 | 已实现 | 连接档案、登录、token 单次刷新重试、连接保险箱 + 指纹登录、登出 |
| V1-B 自适应 Shell | 已实现 | 顶级导航、断点布局、能力门控、首页、`more/*` 子页 |
| V1-C 核心操作 | 部分 | 文件浏览、上传/下载、创建、重命名、复制、移动与删除已完成；终端（SignalR + PTY）未实现 |
| V1-D 提权闭环 | 已实现 | 提权对话框、提权保险箱 + 指纹提权、危险操作确认、5 分钟窗口 |
| V1-E 管理工作台 | 部分 | 系统监控、进程管理及应用部署只读列表/详情已实现；Docker 管理、守护、部署写操作未实现 |

已实现细节：

- 两个独立保险箱（`VaultKind.Connection` / `VaultKind.Elevation`）：AES-256-GCM + Android Keystore，AAD 绑定
  `relaxkonos-vault|kind|serviceId|account`，永不合并、永不互相回填、不可移动密文。
- 生物识别四档（`Strong` / `WeakOnly` / `DeviceCredentialOnly` / `None`）与两种解锁模式（按次强生物识别
  `CryptoObject` 与 5 分钟设备解锁窗口），由 `unlockModeFor` 统一裁决。
- D1–D4 按设计落地：管理员密码仅强生物识别按次授权可保存；服务端拒绝提权即删除该条密码，不按错误码分支；
  网络错误、超时与 5xx 不删除。
- 提权严格保持 `capability + target + jti + 5 分钟`；客户端只做一次安全重试，token 变化即丢弃本地授权缓存。
- 文本全部走 Android resource（`values`、`values-zh`、`values-ja`），无用户可见字符串字面量；方向使用 `start`/`end`。
- 终端入口（`TopDestination.Terminal`）标记为未实现，因此不出现在导航中——设计 §8 禁止"不可用入口"。
- 文件上传使用 Android Storage Access Framework 的单个文档流，不申请宽泛的存储权限；上传和下载均显示
  已传输字节进度并可取消。**下载已改为直接落盘**，见下节；不再经过私有缓存与分享面板。
- 复制和移动会将提权范围限定为源与目标目录；Windows 驱动器根路径（例如 `C:\\`）在向上导航、创建目录
  与提权时保持正确的根分隔符。文件详情与列表共享操作对话框，手机详情页不再出现无响应的操作按钮。
- Expanded 首页现在显示本次进程内成功完成的文件与进程操作；记录最多 20 条，且不会落盘或包含密码、令牌、
  请求体和服务端诊断内容，因此不替代服务器审计日志。
- 连接密码的系统认证与保险箱写入现在先于认证态切换完成，避免登录页卸载后错失生物识别提示；成功登录后，
  主界面才会出现。
- 修复首次打开“更多”子页时路由栈未被 Compose 观察的问题；账户与安全、连接、外观、诊断和关于现在会在
  第一次点击后立即显示，无需先切换顶级导航。
- 密码输入（登录页与提权对话框）共用一个两态控件：默认掩码，点击尾部眼睛图标转为明文并保持在明文，
  再点一次回到掩码；可见性只存在于界面状态（`rememberSaveable`），不进会话、保险箱或任何文件。
- 首页与 `manage/monitor` 的主机指标走 `GET /system/performance/snapshot`。Android 没有 SignalR 客户端，
  服务端把这次读取当作一次有界 demand（覆盖建立差分基线所需的两个采样周期），因此没有实时订阅者时也能取到样本；
  只有在窗口内确实取不到样本（`503 performance-not-ready`）时才提示指标尚未就绪，而不再被误报为
  "无法连接到服务器"——`RelaxKonApi` 只在 5xx 响应体明确给出 RelaxKonOS 问题码时才把它当作 Problem。

## 登录与本地凭据（2026-09-23，对应 `RelaxKonOS.Mobile.LoginCredentials.Design.md`）

- 身份唯一键已演进为 `(serviceId, identifier)`：直连 `serviceId` 是规范化 URL，受管隧道使用安装 ID；`id` 与保险箱 `credentialKey`（`recordId`）由同一对值派生，临时 loopback 端口不参与身份。
- 四个概念分离为可单测的纯 Kotlin：`SelectedLogin` / `SavedCredentialState`（四态）/ `CredentialStatus`（状态行）/ `LoginDecision` + `decideLogin`（§5.1 决策表）。`core/auth/` 不依赖任何 Android 类型。
- 登录页改为单形态：密码框始终可见、`value` 只表示本次手动输入；「已保存密码」由密码框外的状态行（锁形图标 + 文案）表达；按钮文案随决策变化（`登录` / `连接` / `连接中…`）；缺字段或需要输入密码时把焦点移到对应输入框。原「简洁模式」与「指纹登录 / 改用密码」双按钮路径删除。
- 保存动作仍在认证成功之后（`AuthSession.login` 的 `afterLogin` 回调）；认证失败不触碰任何已存凭据，`401 invalid-credential` **不再删除**连接保险箱里的密码（§7.3，相对旧实现的行为变更）。保存失败提示为「已登录，但密码没有保存：<原因>」。
- 保存结果跨过登录页到 Shell 的界面切换：认证成功后发生的「未保存」提示由 `AppContainer.pendingNotice` 承载，在 Shell 顶部显示并可关闭；本地档案或凭据写入抛异常时，`AuthSession` 仍会发布已认证会话，不会永久停在「连接中…」。
- D5 落地：密钥永久失效由「自动删除」改为「标记作废、保留记录与密文、禁止读取」。`VaultRecord` 增加 `state`（`Sealed` / `Invalidated`）、`CredentialVault.markInvalidated` / `markAllInvalidated`、`VaultRecordInvalidatedException`；一个 `VaultKind` 共享一把 Keystore alias，因此 alias 失效会标记该保险箱的全部记录。用户手动登录成功并明确保存时，`VaultAccess.save` 会轮换一次失效 alias、重新请求授权并仅重新密封当前身份，避免每次登录都重复报「指纹已变更」。`beginOpen()` 与 `open()` 双双拒绝作废记录；保险箱文件格式升到 `RKV2`（每条记录多一个 state 字节），旧格式按版本不匹配降级为「无已保存凭据」，不写迁移。账户与安全页对作废记录标注原因；提权保险箱沿用同一规则，作废记录不再出现在「使用指纹确认」路径上。
- `mapKeyException` 现在把 `UserNotAuthenticatedException` 归为「当前不可用」而不是「已失效」。D5 之后误判会把一条好记录永久标死，所以「无法使用不是已失效的证据」必须在 Keystore 层也成立。
- 连接管理拆成两个互不替代的动作，且一律按 `(serviceId, identifier)` 成对生效：**忘记密码**（只删凭据、保留登录记录）与**删除登录记录**（删凭据 + 删该条登录）。按服务器全删的缺陷已修复。
- `SavedConnection` 更名为 `SavedLogin`，补 `id` / `displayName` / `hasSavedCredential` / `credentialKey`，档案文件格式升到 `RKC2`。`hasSavedCredential` 只是显示投影：启动时由 `AppContainer` 以保险箱记录复核修正，两者不一致时以保险箱为准；布尔值不构成安全边界。
- `CredentialUnlocked` 落地为「本进程内、窗口模式（D3）下已授权过的身份集合」：窗口内再次登录走 `VaultAccess.loadWithoutPrompt`，失败即回落为正常授权提示；按次强指纹模式恒为 false，不参与安全边界。
- 新增 `ProblemCodes.LOGIN_RATE_LIMITED`（`429 login-rate-limited`）与对应文案：登录被限流时说「登录尝试过于频繁」而不是通用拒绝；它不参与任何凭据删除判定。
- 三份 `strings.xml`（`values` / `values-zh` / `values-ja`）键集完全一致（现 279 键），并删除了不再使用的 `login_stored_credential_rejected`、`login_use_fingerprint`、`login_use_password`、`login_saved_credential`。
- **修复指纹保存与解封完全不可用**（真机报「设备的指纹已变更」）：`VaultKeyManager.generate` 只调用了 `KeyGenerator.init()` 配置策略，却从未调用 `generateKey()`，因此两个保险箱的 Keystore alias 从未被创建（该文件自 `58b4c64d` 起即如此）。随后 `beginSeal` / `beginOpen` 拿到的 `getKey()` 为 `null`，抛出的「密钥缺失」被界面统一呈现为「设备的指纹已变更」，与 debug / release 无关——两条构建路径是同一份代码。现在补上 `generateKey()`，`ensureKey` 在建钥后校验 alias 确实存在，并把**任何**建钥失败归类为 `VaultKeyUnavailableException`：「拿不到密钥」不是「指纹变了」，两者给用户的建议相反。
- 新增 debug-only 排障链路 `security/VaultDiagnostics.kt`（logcat tag `RelaxKonVault`，`adb logcat -s RelaxKonVault:D`）：记录 `canAuthenticate` 码（映射为 `SUCCESS` / `NONE_ENROLLED` / `NO_HARDWARE` 等名称——`BIOMETRIC_SUCCESS` 就是 `0`，原样打印会被读成失败）、`unlockMode` 裁决、密钥创建与 provider（StrongBox / TEE）选择、alias 存在性、`BiometricPrompt` 的结果码与返回文本，以及 Keystore 异常被分类前的原始类型与消息。能力探测与解锁模式只在结果**变化**时打印（探测本身仍每次调用都执行，不缓存结论），否则重组风暴会淹没关键行。日志只含保险箱种类、provider 决策、异常类与结果枚举，不含密码、账户、服务器地址、`Cipher` 或任何密钥材料；sink 由 `RelaxKonApplication` 仅在 debug 构建安装，release 构建保持未安装，`security` 包因此从不触碰 `android.util.Log`（`VaultDiagnosticsTest` 断言了这一点）。

## 服务器中心 G2 基础件（2026-09-25）

- 已有独立宿主资料、主机密钥固定、SSH 凭据域、JSch SSH/SFTP、loopback 隧道与连接解析规则；SSH 密码/私钥不复用登录或 API 提权保险箱。新增主机只在 SSH 握手成功后才保存；已有主机重新验证失败不会删除记录，并保留端点输入供重试。同一次服务器中心会话中，已验证主机的“管理”会用内存凭据立即复验并直达独立 SSH 工作区；失败后才显示密码输入，离开服务器中心或应用进程结束即清除该凭据。SSH 工作区使用底部“文件 / 终端 / 部署 / 系统”导航，不设顶端退出；系统页通过固定只读 Linux 检测展示 CPU、内存、运行时间与平台，退出按钮才关闭工作区并返回主机添加／管理页。工作区的文件页复用主文件页的卡片、图标与详情层级，支持目录浏览、新建、重命名、递归删除、SAF 单文件上传/下载、常见文本文件的 UTF-8 编辑保存和常见图片预览，所有操作经 SFTP 完成且每次重验固定主机密钥。
- 新增无界面部署操作层：上传前验证签名 ZIP、RID、架构和逐文件摘要，经内置 SFTP 写入远端私有暂存目录，调用固定启动器，并按 `operationId` 查询权威回执。该层尚未接入 Compose 页面或应用级恢复协调器。
- SSH 工作区的安装配置已改成与桌面端同序的三步：选择官方/本地/SSH 主机发布包，选择安装模式、文件权限、网络与证书，最后核对宿主和配置。缺少发布包或证书时不能前进；最后一步显示本构建缺少可信部署资产，安装按钮不可用。真实预检、部署执行与远端操作恢复仍待接入。
- SSH 登录后的工作区增加「终端」页：通过同一固定主机密钥守卫建立独立的交互式 PTY Shell，支持连续命令、输入行、Ctrl+C、Tab 和上下方向键；离开终端页关闭会话并清空本机转录。输出仅作有界纯文本展示，全屏 TUI 的光标/颜色仿真和真机验收仍待完成。
- 登录身份与传输地址已拆分。`SelectedLogin` / `SavedLogin` / `ConnectionProfileStore` / 连接与 debug 凭据都按 `(serviceId, identifier)`；`AuthSession` 另持有可重绑定的 `effectiveBaseUrl`，文件、指标、提权和上传等 API 调用读取当前地址。隧道换端口只更新传输地址，不改变登录记录、保险箱 AAD 或上传恢复归属。
- `RKC2` / `RKV2` 的二进制布局未改变：直连记录原先保存的 URL 本身就是 URL 型 `serviceId`；未增加旧端口键兼容分支。受管安装记录只允许保存安装 ID，不保存临时 loopback 地址。
- 受管登录选择已接入隧道解析：新增 `servercenter/ManagedLoginTunnelResolution.kt`。`ManagedLoginTunnelRules.hostFor` 只按安装标识在宿主仓库中找宿主（相同 IP、URL 文本或 DNS 解析都不足以合并），`bind` 要求宿主与隧道解析结果属于同一安装，否则拒绝跨安装身份；`StoreManagedLoginResolver` 由 `AppContainer.managedLogins` 持有。`LoginViewModel.select` 对受管登录不再回填地址，而是显示宿主名称与「通过 SSH 连接」说明，宿主资料缺失时明确报「缺宿主资料」；`LoginScreen` 增加对应的字段说明行，三份 `strings.xml` 同步新增 `login_managed_host_field` / `login_managed_tunnel_required` / `login_managed_host_missing`。建立隧道本身需要 SSH 凭据与用户确认，仍属宿主详情页与保险箱。
- 已补 JVM 单元检查，覆盖受管登录档案持久化、隧道换端口后登录/凭据键不变、会话改用新端口、受管登录按安装标识找宿主、跨安装绑定与直连解析被拒，以及部署操作的上传、执行、回执与拒绝路径。本轮已在本地 Gradle/JDK 环境执行 `:app:compileDebugKotlin` 与 `:app:testDebugUnitTest`，均 BUILD SUCCESSFUL；真机与真实 Windows/Linux 宿主验收仍未完成。

## 界面现代化（2026-09-23）

按 `RelaxKonOS.Mobile.Design.md` §6.2「视觉语言与界面构成」重做 Android 端的界面层。业务逻辑、导航、凭据与提权链路未改动。

- 新增设计令牌层：`ui/theme/Palette.kt`（四套调色板 + `RelaxKonColors` 语义色，经 `MaterialTheme.relaxKon` 读取）与 `ui/theme/Tokens.kt`（`Spacing` / `Radius` / `Layout`、`RelaxKonShapes`、`RelaxKonTypography`）。四套调色板都显式填满 `surfaceContainer*` 阶梯，避免 Material 基线紫灰调渗入；`RelaxKonOSTheme` 的对外 API（`ColorMode` / `AppLanguage` / `AppearanceState` / `applyAppLanguage` / `applyAppNightMode`）保持不变。
- 新增共享组件：`AppBackdrop`（整窗渐变 + 两处柔光，高对比度下近似纯色）、`ScreenHeader`、`StatusChip`（含 `loadTone` 阈值）、`MetricTile` / `MetricTrack` / `DiskRow`、`ListRow` / `IconBadge`、`EmptyState`、`SectionGroup` / `SectionLabel`；`SectionCard`、`ErrorBanner`、`ProgressSheet` 改为零阴影 + 细边表面。`ErrorBanner` 不再自带外边距，改由调用方决定（页面内与整窗浮层两种位置需要不同的内缩）。
- 界面改造：首页改为「身份 hero 卡 + 状态胶囊 + 指标磁贴 + 能力清单」（hero 替换原来的会话事实卡，服务器/账户/工作区/平台四个事实全部保留）；登录页加品牌标记与渐变；文件页改为位置栏 + 新建/上传双按钮 + 图标列表行；管理与更多改为分组图标行；性能页与进程页改用指标磁贴、胶囊与统一列表行；`more/*` 五个子页统一 `ScreenHeader` 与分组卡。`MainActivity` 只保留一处背景绘制，Shell 的 `Scaffold` 改为透明并补上 `safeDrawingPadding`。
- 新增 12 个自绘矢量图标（`ic_server` / `ic_folder` / `ic_file` / `ic_memory` / `ic_storage` / `ic_activity` / `ic_process` / `ic_upload` / `ic_download` / `ic_copy` / `ic_link`）。**未**引入 `material-icons-extended`：Compose BOM 2025.12.01 已不再解析该坐标（图标库在 Compose 1.7 起冻结），且会显著增大包体。**这套自绘矢量已在下一节被桌面端图标镜像整体取代。**
- `values/colors.xml` 与 `values-night/colors.xml` 的 `window_background` 改为对应渐变的首个色标，避免首帧 Compose 之前闪出不同底色。
- 未新增或删除任何字符串资源；三份 `strings.xml` 键集仍完全一致（279 键）。

校验：`:app:assembleDebug` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL，产物 `app/build/outputs/apk/debug/app-debug.apk`（13.3 MB）；单测 17 个测试类、158 个用例，0 失败 / 0 错误 / 0 跳过。**本次改动全部是界面层，尚未在真机上目视确认**：渐变与柔光在小屏上的观感、指标磁贴在窄屏（360dp）两列布局下的换行、以及深色与高对比度两套调色板的实际对比度，都需要按设备矩阵复核。

## 图标与语言（2026-09-23）

对应两条要求：「图标使用和桌面端相同的图标」与「语言跟随系统，中文、日文以外均使用英文」。业务逻辑、导航栈、凭据与提权链路未改动。

- **图标不再自绘**：来源改为桌面端 `Client/RelaxKonOS.Client/Assets`，由新增的 `Tools/Mobile/sync-desktop-icons.py` 镜像到
  `app/src/main/res/drawable-nodpi/`（128px 见方，113 个 PNG，1.71 MB）。分两组，职责与桌面端一致：`ic_app_*`（24 个，来自
  `Assets/AppIcons`，另有 `RelaxKonOS-client-icon.png` 作为品牌图）是自带上色的圆角方形应用图标，只用于品牌标记与顶层目的地；
  `ic_sys_*`（89 个，来自 `Assets/Icons/Explorer`）是透明彩色字形，用于表头、列表行、按钮与文件类型。桌面端的
  `Assets/Icons/Explorer/debug-grid.png` 是 2048×768 sprite sheet 而非图标，脚本按名跳过。脚本支持 `--check`（只报差异、
  不写文件，有差异即以非零退出码失败），用于在提交前或未来的 CI 中防止图标与桌面端漂移。
- **单一映射层**：新增 `ui/icons/DesktopIcons.kt`，按语义（导航、页面头、动作、文件系统）暴露资源 id，并提供 `DesktopIcon`
  组件。业务代码不再直接引用 `R.drawable.*`——现在只剩 `PasswordTextField.kt` 的两个自绘矢量（见下）。
  `TopDestination` 与 `ManageDomain` 的 `icon: ImageVector` 改为 `@param:DrawableRes iconRes: Int`（用 `@param:` 形式，
  否则 Kotlin 2.x 会报「注解目前只作用于值参数」警告）。
- **不做主题染色**：素材自带配色，`Icon` 的 tint 会把它们压成剪影，因此统一用 `Image`。`IconBadge` 容器相应从
  `primaryContainer` 改为中性 `surfaceContainerHigh`（蓝底衬黄文件夹是染色徽章的典型坏结果），文件列表行也不再按目录/文件分色。
  首页能力清单的勾选图标改为 6dp 成功色圆点——桌面图标集里没有对勾，凭空造一个会引入第二套图标语言。
- **桌面端确实没有的两类**：上传/下载（桌面把这两个动作放在无图标的菜单里，故取集合中语义最近的箭头）与密码显隐
  （桌面登录页没有该控件，保留 Android 自绘的 `ic_password_visible|hidden.xml`）。这是有意例外，不是遗漏。
- **文件类型判定移植**：`DesktopIcons.fileFor(name, isDirectory)` 的判定顺序（整名 → 具体扩展名 → 归类）逐条移植自桌面端
  `ExplorerIconAssetResolver` / `ExplorerFileIconKindResolver`，由 `ui/icons/DesktopIconsTest.kt`（7 个用例）固化，
  两个客户端对同一文件必须给出同一图形。
- **语言规则落到资源目录**：`values-zh-rCN` 重命名为 `values-zh`，`app/build.gradle.kts` 改用语言级
  `androidResources.localeFilters`（`en` / `zh` / `ja`，同时消掉了 `resourceConfigurations` 的 AGP 弃用警告），
  `res/xml/locales_config.xml` 同步由 `zh-CN` 改为 `zh`——写成区域级会让系统「应用语言」页声称本应用只支持这一种中文。
  只有一份中文译文，`zh-TW`/`zh-HK` 等全部变体因此落到同一份中文而不是英文。Kotlin 侧不写猜设备语言的分支：
  `AppLanguage.SimplifiedChinese` 更名为 `Chinese`，`AppLanguageTest`（6 个用例）固化各变体归属。
- 删除 11 个不再使用的自绘矢量（`ic_activity` / `ic_copy` / `ic_download` / `ic_file` / `ic_folder` / `ic_link` / `ic_memory` /
  `ic_process` / `ic_server` / `ic_storage` / `ic_upload`）；`appearance_language_note` 等三语文案同步更新，键集仍为 279 一致。

校验：`:app:assembleDebug` 与 `:app:testDebugUnitTest --rerun-tasks` 均 BUILD SUCCESSFUL，Kotlin 编译零警告；单测 19 个测试类、
171 个用例，0 失败 / 0 错误 / 0 跳过（本轮新增 `DesktopIconsTest` 7、`AppLanguageTest` 6）。图标资源共 113 个文件、1.71 MB，
APK 由 13.3 MB 增至 14.5 MB，`sync-desktop-icons.py --check` 复跑输出 "113 icons are up to date"（幂等）。**尚未做真机目视确认**：
底部导航 26dp 槽位上的彩色应用图标在深色与高对比度主题下的对比度、文件列表行改用彩色字形后的信息密度，都需要按设备矩阵复核。

## 登录链路修复与 debug 明文兜底（2026-09-23）

对应两条要求：「正确的用户名密码也必须卸载重装才能登录」与「debug 构建且机器没有强密码时能否保存密码（只存一条、可读取）」。

**根因：地址探测被当成了门禁。** `LoginViewModel.submit` 原先先做一次服务端地址探测（`ServerEndpointDiscovery`，
`OPTIONS /api/v1.0/auth/login`），只有探测通过才发登录请求；探测失败直接写一条提示并 `return`，请求根本不发出。
持久化的服务器地址一旦探测不过——改了端口或协议、中间有代理、探测路径被拒——正确凭据永远没有机会被发送；
卸载重装会清掉 `noBackupFilesDir` 下的地址，于是「重装就能登录」，与症状完全自洽。修法是把它降回**优化**：
`submit` 现在无条件进入 `submitResolved`，探测在后台并行跑，只用于润色「连不上」时的文案（`loginTransportMessage`
按探测结论分派 `login_server_unavailable` / `login_server_invalid` / `error_connectivity`），且结果只在地址未被
用户改过时才应用。这条原则已写进代码注释：**地址解析是优化，永远不是门禁。**

**顺带修掉两条「登录页点击后永久卡住」的路径**（与主 bug 同属「点击没反应」这一症状族）：
`signInWithSavedPassword` 原先在两个分支静默 `return`（既没有可用记录、也没有可用解锁方式时，点击等于什么都没发生），
且 `isLoggingIn` 不在 `finally` 里清除——一次异常就会让整张表单（三个输入框 + 按钮）永久 disabled。现在无凭据可用时
给出明确文案（`login_saved_password_unavailable`）并把焦点移回密码框，`isLoggingIn` 一律在 `finally` 复位。
`VaultAccess` 的三个入口（`save` / `load` / `loadWithoutPrompt`）原先不捕获未知平台异常，现在统一经
`unnameableVaultFailure(operation, error)` 归类为 `UnlockFailure.Unknown`，并在 `catch (Exception)` 之前先重抛
`CancellationException`（否则协程取消会被吞成「未知失败」）。未知异常映射为 `Unknown` 而不是 `KeyInvalidated` 至关重要：
前者提示「暂时不可用」，后者会把好记录 `markAllInvalidated` 永久标死，两者给用户的建议相反。

**debug 明文兜底（设计文档 §8.1，决策 D10）。** `AndroidKeyStore` 的 `setUserAuthenticationParameters` 只接受
`AUTH_BIOMETRIC_STRONG` 与 `AUTH_DEVICE_CREDENTIAL`，没有「弱生物识别」标志位；设备完全没有锁屏时
（`BiometricCapability.None`）连接保险箱在结构上无法建钥，`unlockMode()` 恒为 `null`，「保存密码」整体不可用。
为此新增 `security/DebugCredentialStore.kt`：仅 debug 构建、仅当设备无任何可用锁屏且用户已开启指纹保存总开关时，
把**一条**凭据明文写入 `noBackupFilesDir/debug-credential.bin`（格式 `RKD1`）。边界用代码结构锁死：release 下
`AppContainer.debugCredentials` 恒为 `null`，调用点全部走可空接收者；`save` 即替换，永远只有一条。界面每个出现处
都写明「未加密」（登录页勾选框与提示、状态行、连接列表、安全页），忘记密码 / 删除登录记录 / 关闭指纹总开关 /
清空全部四条删除路径全部接通。安全页会单独列出这条明文记录并标红，诊断导出含 `debugCredentialRecord=` 一行。

界面与文案：三份 `strings.xml` 各新增 5 键（现 285 键，键集一致）：`login_remember_hint_debug`、
`login_no_lock_screen_debug`、`login_saved_password_debug`、`login_credential_saved_debug`、
`connections_saved_password_debug`、`account_security_debug_record_note`。

调试诊断：登录决策链路新增 debug-only 跟踪点，走既有 `VaultDiagnostics` 通道（`adb logcat -s RelaxKonVault:D`，
release 下 sink 不安装即 no-op）：`login.decision`（走哪条路径、为什么）、`login.discovery`（探测结论）、
`login.result`（服务端结论）、`login.debug-store`（明文落盘）、`login.stored-path.failed`。

校验（2026-09-23）：`:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest --rerun-tasks`
均 BUILD SUCCESSFUL，Kotlin 编译零警告；单测 **21 个测试类、194 个用例，0 失败 / 0 错误 / 0 跳过**
（本轮新增 `DebugCredentialStoreTest` 13、`VaultAccessTest` 6，`SavedCredentialStateTest` 由 6 增至 10）；
产物 `app/build/outputs/apk/debug/app-debug.apk` 14.4 MB。**根因修复与兜底逻辑尚未真机复现**：
需要在「旧 APK 直接覆盖安装」的机器上确认登录不再被地址探测拦截，并在无锁屏设备上确认明文兜底可用。

## 文件传输落盘与回执归属（2026-09-24）

**用户报告**：小屏（Compact/Medium）上点「下载」不立即生效，按下返回键之后才生效；平板端（Expanded）能生效但弹的是
分享面板，文件并没有落到本机目录。

**根因有两层，缺一不可**：

1. **动作由界面托管，而不是由状态托管。** 下载完成后的投递写成 `FilesScreen` 内的
   `LaunchedEffect(viewModel.downloadReady)`，而 Compact/Medium 的文件详情是**压栈页**——点开文件后 `FilesScreen`
   已不在组合中，`LaunchedEffect` 自然不会跑；等用户返回、列表重新入栈时它才以当时的非空 `downloadReady` 触发，
   于是表现成「按返回键才生效」。**同一个缺陷还吞掉了另外两样东西**：`ProgressSheet`（进度卡）与 `ErrorBanner`
   （错误横幅）此前也只在 `FilesScreen` 里渲染，所以详情页上启动的传输**全程没有任何进度或失败提示**。
2. **投递目标本身就是错的。** 下载只写进私有 `cacheDir`，完成后再用 `FileProvider` 交给分享面板——文件从未进入
   本机存储，用户必须为每个文件在系统面板里挑一次目标。

**修复**：

- **落盘**：新增 `data/DownloadStore.kt`（`DownloadTarget` = `open` / `commit` / `discard`）。API 29+ 经 `MediaStore`
  写入共享的 `Download/RelaxKonOS`：无权限、无对话框，文件名冲突由 MediaStore 追加计数解决，写入前 `IS_PENDING=1`，
  `commit()` 清零后才对全设备可见；失败或取消则 `discard()` 删除，不留半截文件（旧实现会留下残包）。API 23–28
  没有无权限的公共下载目录写入路径，落入应用自身的外部 `Download` 目录——这不是降级为缓存，而是本机共享存储上
  用户可删的真实文件；应用内文案始终报出实际落盘位置。
- **传输层不再认识「文件」**：`RelaxKonGateway.download` 的目标参数由 `java.io.File` 改为 `core/net` 的
  `fun interface DownloadSink`，`RelaxKonApi` 只在响应码通过后才 `sink.open()`——被拒绝的下载不会创建任何文件。
- **回执归属移出页面**：新增 `FileMessageBanner` 与 `FileTransferCard`，由 `MobileNavHost.FilesDestination`（三种
  布局、两条 files 路由都在组合中）渲染；`ProgressSheet` 与 `ErrorBanner` 从 `FilesScreen` 移除。
- **上传两处修正**：目录在协程启动前取定，上传途中导航不会把文件投到别的目录；文档名、大小与流都在
  `Dispatchers.IO` 上取——云端文档的 `query` 要几百毫秒、`openFile` 可能数秒，旧实现是主线程同步调用。名称与大小
  取自一次 `OpenableColumns` 查询（`DISPLAY_NAME` + `SIZE`），失败才回落 `AssetFileDescriptor.length`。
- **回执语气**：`UiMessage` 增加 `tone`（默认 `Danger`），`ErrorBanner` 接受同一 tone 取色。下载/上传/新建目录的
  成功回执改用 `StatusTone.Success`——旧实现把「已上传 x」显示在红色错误横幅里。
- **删除**：`FileProvider` 声明、`res/xml/file_paths.xml` 与 `files_share_download` 文案随分享面板一并移除；
  应用不再需要任何文件分享 URI。三份 `strings.xml` 各删 1 键、增 4 键（`files_downloaded`、
  `files_download_failed`、`files_download_default_name`、`files_upload_unreadable`），现 288 键且键集一致。

**未做真机验证**：`MediaStore` 落盘、`Download/RelaxKonOS` 的可见性与重名追加计数需要在真机上确认一次。

## 图片预览与预览缓存（2026-09-24）

- **选中图片即显示**：详情页在属性卡片上方增加「预览」卡片。是否可预览由文件名扩展名判定，名字没有可用扩展名时才看服务端
  推断的 `mimeType`；`image/svg+xml`、`image/tiff`、`image/x-icon` 被显式排除——平台本来就解不出来，把它们算作图片
  只会让应用自己刚承诺可预览的文件立刻报「无法显示」。目录永不预览。
- **字节进缓存而非 Downloads**：新增 `data/ImagePreviewCache.kt`，图片流写进 `cacheDir/previews`。键是
  `服务器 + 远端路径 + 大小 + 修改时间` 的 SHA-1：远端路径本身不能当文件名（Windows 路径含 `\` 与 `:`），而改动过的
  文件必须换键，否则会把旧图当新图给用户看。复用前校验文件长度等于服务端报告的大小，因此崩溃或中断留下的半截文件不会
  成为命中（重命名做不到这件事——它分不清完整与截断）。容量上限 64 MB，`trim` 按最久未查看淘汰。
- **两次解码的阶梯**：新增 `data/ImageDecoder.kt`。第一趟只解码 96 px 见方并立即上屏（再大的照片也几乎不耗时），
  第二趟按当前显示框的像素尺寸重新解码并替换。降采样倍率由纯函数 `previewSampleSize` 决定，先保证「不小于显示框」
  （观感），再用 400 万像素上限兜底（内存：4000×3000 的 `ARGB_8888` 是 48 MB；50000×50000 的 25 亿像素还会让
  `Int` 乘积溢出成负数，所以这条比较在 `Long` 上做）。EXIF 方向在解码器里应用，竖拍照片不会横躺。
- **显示框由界面上报**：卡片与全屏查看器各自用 `BoxWithConstraints` 量出自己的像素尺寸交给 ViewModel。质量在一次
  选中内只升不降——打开查看器只对**已缓存**的文件重新解码到屏幕尺寸（不产生第二次传输），关闭不会把刚解出的图降回去。
- **代次守卫**：取消一个协程不等于停住它——取消下载不会中断阻塞读，旧任务仍会写完并走到收尾。因此每次新选中都递增
  `previewGeneration`，旧任务对 `preview`、缓存与磁盘的每一次写入都以「我的代次仍是当前的」为前提；过期的任务只留下
  一个长度不符的文件，而长度校验会把它当作未命中。
- **预览不自动提权**：`ElevationAnswerProvider.Declines`（加在 `ElevationRepository.kt`）给出的答案与「用户关掉对话框」
  完全相同——`null`，于是 `withPathElevation` 原样返回服务端的拒绝。选中受保护文件得到的是「读取该文件需要管理员密码」
  与「授权并预览」，只有点击才弹管理员密码提示。
- 详情页内容改为可滚动：预览加在原属性卡之上后，手机横屏会超出窗口；标题栏留在滚动区之外，返回入口不会滚走。
- 新增 7 个字符串键（`files_preview_title/open/loading/progress/failed/elevation/authorize`），三份 `strings.xml`
  现 295 键且键集一致。

## 服务端缩略图（2026-09-24）

- **为什么加它**：此前的「先缩略图」省的只是**解码时间**——图片仍是一次完整请求，第二次点同一张图才命中缓存。
  要让大图与慢链路上的**传输本身**渐进，只能让服务端先给一张小图。新增
  `GET /api/v1.0/files/thumbnail?path=&maxEdge=`（路由常量 `FileApiRoutes.Thumbnail`；`maxEdge` 缺省 256、允许
  16–1024、越界 400 `invalid-size`）。
- **渲染**：新增 `RelaxKonOS.Server/Files/ImageThumbnailRenderer.cs`。先 `Image.Identify` 读头，声明像素数超过 6400 万
  直接拒绝（解压炸弹在读第一个像素之前就被挡下）；解码只取一帧（`DecoderOptions.MaxFrames = 1`，否则一张动图会按
  帧数放大内存）；EXIF 方向在缩放前应用；缩放用 `ResizeMode.Max` + Lanczos3，小图不放大。输出格式跟着**像素**走
  而不是容器：`CloneAs<Rgba32>()` + `ProcessPixelRows` 逐像素扫描 alpha，有透明像素出 PNG，否则出 JPEG(q85)。
  **这一条踩过坑**：`image.PixelType.AlphaRepresentation` 对同一张透明 PNG 报 `bpp=32 alpha=[]`（编码侧不声明 alpha），
  据此判断会把透明图当 JPEG 输出、透明区域直接丢成黑底，因此改为按像素扫描。并发上限
  `MaxDegreeOfParallelism = min(4, CPU)`，几个缩略图请求占不满一个宿主。
- **不是错误的错误码**：内容不是本服务能解码的图像时返回 415 `thumbnail-unsupported`，客户端读作「没有缩略图」并
  照旧拉原图（`ProblemCodes.THUMBNAIL_UNSUPPORTED`）。受保护路径与 `download` 完全同构：`elevation-required` → 提权后重试。
- **客户端**：`RelaxKonGateway.thumbnail(...): ApiResult<ByteArray>`——唯一归宿是内存，因此不是 `DownloadSink`；
  `RelaxKonApi` 把它与 `download` 共用的传输逻辑抽成 `streamInto(route, query, sink, onProgress)`，
  `FilesRepository.thumbnail` 走同一个 `withPathElevation`，因此与下载**同权限、同范围**（文件自身路径，不扩大到目录）。
  `FilesViewModel` 在确认没有缓存命中后**并行**发起预取（`prefetchThumbnail`，`ElevationAnswerProvider.Declines`，
  永不等待、永不弹窗）：到货即画在进度条下方（`ImagePreview.Downloading.thumbnail`），原图落地后再充当详细图解码的
  占位，省掉本地那趟 96 px 解码。代次守卫沿用既有规则——过期、或已经进入 `Ready` 的缩略图一律丢弃，绝不把已解码的
  照片换回小图。
- **许可判定**：`SixLabors.ImageSharp` 3.1.12。Six Labors Split License v1.0 第 2 条把「用于 Open Source 或
  Source-Available 许可的软件」划入 **Apache-2.0** 分支，本项目即 Source-Available，故可用；3.1.x 是首个提供
  `DecoderOptions.MaxFrames` 的稳定线，纯托管、无原生依赖。判定依据已写进 `THIRD_PARTY_NOTICES.md` 与
  `Directory.Packages.props`。
- 客户端未新增字符串键（复用预览既有文案），三份 `strings.xml` 仍 295 键且键集一致。

## 进程属主显示为 null 的修复（2026-09-24）

- **现象**：手机端「管理 → 进程」每一行的细节都是 `CPU 0.7% · 2.91 GB · null`。
- **`userName` 确实是 null**：服务端只有 Linux 才解析 `/proc/<pid>/status` 的 `Uid:` 并映射 `/etc/passwd`；Windows 走的
  `ProcessSampler` 拿到的是 `LinuxProcessDetails(null, 0)`，所以线上的字段本身就是 null，这是既有设计
  （[`RelaxKonOS.TaskManager.md`](../../../docs/applications/RelaxKonOS.TaskManager.md) §319/§334）。
- **屏幕上那四个字母是客户端读出来的**：`RelaxKonApi` 原来写的是 `item.optString("userName").takeIf { it.isNotBlank() }`，
  而 Android 的 `org.json` 对 JSON null 返回的是**字面字符串** `"null"`（`JSON.toString(JSONObject.NULL)` →
  `String.valueOf(NULL)`；参考实现 `org.json:json` 在同一输入上返回空串），非空判断拦不住它，于是 `"null"` 进了
  `RemoteProcess.userName`，再被细节行的 `listOfNotNull` 原样拼上屏幕。
- **改法**：新增 `JSONObject.optNullableString(name)`（`if (isNull(name)) null else optString(name).takeIf { it.isNotBlank() }`），
  与既有 `optNullableLong` 同处、同形状；`isNull` 在 Android 与参考实现上语义一致（缺键与 JSON null 都算 null），
  因此这条修复不依赖运行时怪癖。5 处可空字符串读取全部换过去：`userName`、`mimeType`
  （`FileSystemEntryDto.MimeType` 可空，此前文件详情的 MIME 同样会显示 `null`），以及 RFC 7807 判定用的
  `type` / `problemCode` / `traceId`。
- **顺带修掉一个真缺陷**：`problemCode` 被判成 `"null"` 时 `ProblemCodes.namesContractCode` 会认为服务端**已经给出**
  契约码，于是 `readsAsProblem` 把没有问题的 5xx 读成 `Problem`——违反 §5.8.2（服务端未给出问题码时，5xx 不构成
  凭据判定，也不该被当作契约答复）。
- 界面不需要改：细节行是 `listOfNotNull(CPU, 内存, userName).joinToString(" · ")`，属主为空时这一段自然消失。
  Windows 服务端上属主恒为空，要让那里也显示属主要求服务端补 WMI / P/Invoke（TaskManager 文档 §334）。
- 未新增字符串键，三份 `strings.xml` 仍 295 键且键集一致。

## 登录身份的执行资格（2026-09-26）

- **背景**：服务端现在把"这个登录身份能否执行普通文件/终端/Git 操作"作为登录响应的一部分下发
  （`LoginResponse.executionEligibility` → `ServerExecutionEligibilityDto { available, reason }`），桌面端已按同一契约落地。
  Android 在此之前既不消费这个字段，也不认识随之新增的两个问题码，于是用 root 登录后打开文件只会看到通用的一句
  「服务器拒绝了该请求」——看不出原因，也看不出出路。
- **契约镜像**：`core/net/Models.kt` 新增 `ExecutionEligibility` 与 `ExecutionEligibilityReasons`（六个原因码逐字对齐
  `ServerExecutionEligibilityReasons`）；`LoginSession` 增加 `executionEligibility`；`ProblemCodes` 增加
  `identity-not-eligible` / `identity-not-executable`，与 `UserExecutionProblemTypes` 的后缀逐字对齐（拼写由断言钉住）。
- **解析**：`RelaxKonApi.parseLogin` 里 `executionEligibility` 是**必填对象**（与 `server`、`tokens` 同级）。这个答案决定首页
  要不要提前告知，缺字段属契约破坏，不能退化成"默认可执行"；`available` 用 `getBoolean` 严格读，`reason` 走
  `optNullableString`（身份可用时服务端下发的就是 null）。
- **状态**：由 `SessionState.Active.executionEligibility` 承接，`AuthSession.adopt` 原样透传；两条登录路径（手输密码、
  已存密码）都经过 `adopt`，因此不存在"只有一条路径拿得到答案"。
- **呈现**：首页 `IdentityCard` 之下新增 `ExecutionEligibilityNotice`（`ui/common/ErrorBanner.kt`）。它刻意**不是** `ErrorBanner`：
  还没有失败，没有可重试的动作，而这是会话事实、不该被「忽略」掉；用 Warning 而非 Danger 色调，因为会话本身可用——
  指标、进程照常，只有用户接下来最可能点的文件/终端/Git 不可用。
- **文案**：`problemMessage` 增加两码映射（第一次 503 也说得清楚），新增 `executionEligibilityMessage(reason)`。原因码只区分
  两种出路（换账户 vs 修部署），其余（含客户端不认识的新码与 `unsupported-platform`）一律落到"该身份不可用"那一句：
  `available = false` 是权威结论，静默才是要修的那个 bug。
- **字符串**：三份 `strings.xml` 各新增 `error_identity_not_eligible`、`error_identity_not_executable`，344 → **346 键**，键集一致。
  英文条目不含撇号，沿用该文件既有约定（全文件零撇号），避免转义分歧。
- **测试**：`ProblemCodesTest` 新增 4 组（wire 拼写、两码各对应哪一句、原因码→句子、未知原因不得静默）；`AuthSessionTest`
  新增 2 个（服务端答案原样进入 `Active`、服务端未声明时读作可用）。
- **既有限制**：`parseLogin` 本身仍无单元测试——本工程单测跑在被 stub 的 `org.json` 上（见
  [`RelaxKonOS.Mobile.BulkUpload.Design.md`](./RelaxKonOS.Mobile.BulkUpload.Design.md) 的说明），JSON 解析只能在真机上验证。

## 启动图标与桌面端一致（2026-09-26）

对应要求：「将 Android 应用的图标也修改为和桌面一致」。此前 `AndroidManifest.xml` 没有声明 `android:icon`，桌面与
任务栏用品牌标记、手机上却是系统默认图标。业务逻辑、导航与资源键未改动。

- **声明图标**：`android:icon="@mipmap/ic_launcher"` 与 `android:roundIcon="@mipmap/ic_launcher_round"`。API 26
  及以上解析到 `mipmap-anydpi-v26/` 的自适应图标，API 23–25 解析到 `mipmap-*dpi/` 的整块位图，与 minSdk 23 对齐。
- **仍然只有一个来源**：启动图标同样由 [`Tools/Mobile/sync-desktop-icons.py`](../../../Tools/Mobile/sync-desktop-icons.py)
  从桌面端 `Assets/RelaxKonOS-client-icon.png` 派生（`ic_app_brand` 用的就是同一张图），不会出现「应用内是品牌标记、
  桌面图标是另一张图」。脚本按 mdpi/hdpi/xhdpi/xxhdpi/xxxhdpi 生成三套 PNG（`ic_launcher`、`ic_launcher_round`、
  `ic_launcher_foreground`）与两个自适应图标 XML，`--check` 一并覆盖这些文件。
- **透明背景的标记必须自带一层背景**：桌面图标是透明背景的标记，而自适应图标的前景与背景是两层。背景取应用自己的
  背景渐变（`ui/theme/Palette.kt` 的 `backdropStart` `#F7F9FE` → `backdropEnd` `#E7EEFA`，落在
  `res/drawable/ic_launcher_background.xml`）——桌面端也正是把标记画在这层浅色表面上（标题栏 26dp 槽位、登录页
  58×48 横幅），两边观感因此一致。用主色 `#1F5FA9` 作底会让标记中段的深蓝 `#103EBA` 糊进背景，这是刻意避开的。
- **标记按遮罩反推尺寸**：标记的墨迹从中心最远到自身边长的 0.67 处，所以「占画布多少」实际是「哪个遮罩装得下」。
  自适应图标是 108dp 画布、72dp 遮罩，取 0.48（墨迹落在遮罩圆内约 1dp）；API 25 及以下的圆角方形整块位图没有
  遮罩，取 0.70；圆形整块位图丢掉方形能留的角，取 0.68 才与方形读起来一样满。取 0.58 时紫色尖角会被圆形遮罩切平，
  已修正。圆角方形位图的圆角半径取边长 0.22，与设计文档 §6「圆角四档」的 10dp 档同一量级。
- **顺带补齐 6 个此前未被镜像的图标**（`ic_app_server_center`、`ic_app_ssh_files`、`ic_sys_file_link`、
  `ic_sys_toolbar_download`、`ic_sys_toolbar_upload`、`ic_sys_toolbar_upload_folder`）：桌面端在 SSH 合并里加了这些
  素材，Android 侧漏跑脚本。Kotlin 目前尚未引用它们，但补齐前 `--check` 是失败的。

校验：完整跑通 `:app:assembleDebug`（Gradle 9.7.1、JDK 21、`--offline`）产出 `app-debug.apk`；
`aapt2 dump badging` 输出 `application: icon='res/mipmap-anydpi-v26/ic_launcher.xml'`（120–640 dpi 六档一致），
`aapt2 dump resources` 确认 `mipmap/ic_launcher`、`mipmap/ic_launcher_round`、`mipmap/ic_launcher_foreground`
与 `drawable/ic_launcher_background` 均已登记。

**尚未真机确认**：圆形 / 圆角方形 / 圆角矩形三种启动器形状下的实际观感，以及深色壁纸上近白底图标是否过淡。

## 编译阻塞修复：ServerCenterScreen 的 weight 导入（2026-09-26）

`:app:assembleDebug` 此前编译失败：

```
e: ServerCenterScreen.kt:10:43 Cannot access 'val RowColumnParentData?.weight: Float': it is internal in file.
```

`ServerCenterScreen.kt` 第 10 行写了 `import androidx.compose.foundation.layout.weight`。Compose 里 `Modifier.weight()`
是 `RowScope` / `ColumnScope` 的作用域成员，没有可导入的顶层同名函数；该包中确实存在一个名为 `weight` 的声明，但它是
`RowColumnParentData` 的 internal 扩展属性（布局内部用的数据槽），于是这一行导入解析到了它，报「internal in file」。
全模块只有这一个文件这么写，其余文件都在 `Row` / `Column` 内部直接用 `Modifier.weight(...)`。

修法是删掉该导入：两处调用点（第 135、136 行）本来就在 `Row` 的 lambda 里，`weight` 由隐式 `RowScope` 接收者提供。
这与仓库 API 演进策略一致——不留兼容垫片，直接把当前接口用对。该文件是 SSH 合并提交 `f0696d5f` 带进来的，
与启动图标改动无关。

## 构建修复：模板平台文案的未转义单引号与 Docker 页编译（2026-09-27）

`903fa86b`（Docker 管理入口）带进来两类阻断，`:app:assembleDebug` 无法通过。

**一、资源合并失败**：`app/src/main/res/values/strings.xml` 的 `catalog_blocked_platform` 里 `this server's` 用了
未转义的单引号。`mergeDebugResources` 把同一个字符串的解析失败报成
`Failed to flatten XML for resource 'catalog_blocked_platform' with error: Invalid unicode escape sequence in string`
——该措辞指错了方向：字符串里没有任何 `\u`，`aapt2` 的真实诊断是 `unescaped apostrophe in string`。
绕开 Gradle 直接编译资源能看到最直白的报错，也是这类问题的首选定位手段：

```bash
aapt2 compile --dir app/src/main/res -o <已存在的目录>/res.zip   # build-tools 36.0.0
```

修法是写成 `server\'s`（与 Material3 自带资源里的 `%1$d o\'clock` 一致）。三语文案里只有英文这一处，
中文（`此模板不支持该服务器的操作系统或架构。`）与日文不受影响。

**二、Kotlin 编译失败**（`ui/manage/docker/DockerScreen.kt`），两类错误：

1. 第 14 行 `import androidx.compose.foundation.layout.weight` —— 与 2026-09-26 `ServerCenterScreen.kt`
   完全同一个坑：`Modifier.weight()` 是 `RowScope` / `ColumnScope` 的作用域成员，这一行导入解析到
   `RowColumnParentData` 的 internal 扩展属性。第 180 行的调用本来就在 `Column { }` 内，删掉导入即可。
   **同一个坑已出现两次**，新增界面不要再写这行导入。
2. 第 169、170 行把 `stringResource(...)` 写在 `DockerStacks` / `DockerContainers` 的**非 composable** 回调
   （`(DockerStack) -> Unit`、`(DockerContainer, String) -> Unit`）里，报
   `@Composable invocations can only happen from the context of a @Composable function`——确认对话框文案不可能
   在点击处生成。修法与 `DeploymentsScreen` 既有做法一致：状态里只存**领域对象**（新增私有类型
   `DockerRemoval.Stack(name)` / `DockerRemoval.Container(name)`），文案交给 `removalMessage()` 在 `AlertDialog`
   的 composable 作用域里解析。

校验（2026-09-27）：

- `:app:assembleDebug` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL；**41 个测试类、401 个用例，
  0 失败 / 0 错误 / 0 跳过**；产物 `app/build/outputs/apk/debug/app-debug.apk` 19,410,463 字节。
- 三份 `strings.xml` 均为 **511 键**且键集一致、无重复；`aapt2 compile --dir app/src/main/res` 零错误。
- 环境（本机现状）：Gradle 9.7.1（`D:\environments\Android\gradle-9.7.1`）、JDK 21（`D:\environments\JDK\jdk-21`）、
  工作树的 `local.properties` 与 `gradle-wrapper.properties` 的 `distributionUrl` 都已指向 `D:` 下实际存在的
  路径（相对 2026-09-26 的记录已改变，不需要再临时改写路径）；`GRADLE_USER_HOME` 用仓库内
  `.workbuddy/gradle-home`，避开可能在跑的 daemon 对 `D:\environments\Android\.gradle` 的锁。
- **未做真机验证**：Docker 页的资源列表、Stack 服务状态、容器/Stack 启停与删除确认、Compose 导入与部署流程。

## 已知限制

- 连接保险箱的密码被服务端拒绝时**不**删除（§7.3）。代价是：用户已在服务端改密后，本机那条旧密码会一直失败，直到手动输入新密码并在成功后保存覆盖它。这是有意选择——删除只在用户显式「忘记密码」或「删除登录记录」时发生。
- 终端与守护进程管理尚未实现，对应入口不会出现。**Docker 管理**（`manage/docker`，`903fa86b` 引入）集合状态、
  Stack 服务、容器/镜像/卷/网络列表与容器/Stack 启停删除（删除需确认）于一体；**应用部署**已有只读详情与创建、
  启停、回滚写操作。两者都按服务端能力门控，且均未做真机验收。
- 连接保险箱在 `WEAK_ONLY` 设备上只能用**设备凭据**（锁屏 PIN/图案/密码）解封：Android Keystore 不存在
  "弱生物识别"标志位，`setUserAuthenticationParameters` 只接受 `AUTH_BIOMETRIC_STRONG` 与 `AUTH_DEVICE_CREDENTIAL`。
  仅有弱生物识别且未设置锁屏的设备无法创建窗口密钥，此时降级为输入密码，且**不删除**任何已存记录。
  详见 `RelaxKonOS.Mobile.V1.Design.md` §5.4。
- **完全没有锁屏的设备上，「保存密码」只由 debug 构建的明文兜底提供**（设计文档 §8.1 / 决策 D10）：该记录以**明文**
  存于 `noBackupFilesDir`，界面上每个出现处都标注「未加密」。release 构建与有锁屏的设备一律不具备这条路径。
- `androidx.lifecycle:lifecycle-viewmodel-compose` 固定在 `2.10.0`：`2.11.0` 起声明 `minCompileSdk 37`，而本模块编译于
  `compileSdk 36`。升 `compileSdk` 到 37 后应一并升回。
- 真机、横竖屏旋转、分屏、软键盘、后台恢复与 Insets 仍需设备矩阵验证。
- 若设备已由另一签名安装相同包名，`adb install -r` 会报 `INSTALL_FAILED_UPDATE_INCOMPATIBLE` 并保留旧包；
  需要先执行 `adb uninstall app.relaxkonos.mobile`。

## 构建与校验

- 使用 [`Tools/Mobile/Build-Android.ps1`](../../../Tools/Mobile/Build-Android.ps1) 运行 Gradle `:app:assembleDebug`；
  需要 Android SDK、JDK 21 与 Gradle 9.7.1。
- 使用 Gradle `:app:testDebugUnitTest` 执行单元测试：登录决策与身份键、凭据四态与显示投影、布局断点、
  认证状态机、保险箱加解密与 AAD 绑定、提权单次重试、生物识别能力映射、wire 时间戳解析、导航栈、能力门控、
  桌面端文件图标判定顺序、应用语言归属。
- 图标资源来自桌面端，不手工维护：改了 `Client/RelaxKonOS.Client/Assets` 下的图标后，运行
  [`Tools/Mobile/sync-desktop-icons.py`](../../../Tools/Mobile/sync-desktop-icons.py) 重新镜像到 `app/src/main/res/drawable-nodpi/`，
  并重新派生 `app/src/main/res/mipmap-*dpi/`、`mipmap-anydpi-v26/` 与 `drawable/ic_launcher_background.xml` 的启动图标；
  提交前可用 `--check` 让它只报差异而不写文件（有差异时返回非零退出码）。

最近一次校验（2026-09-23）：

- `:app:assembleDebug` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL；单测 158 个、0 失败；
  产物为 `app/build/outputs/apk/debug/app-debug.apk`。
- 真机（SM-S9380）已安装该 APK；指纹链路排障用 `adb logcat -s RelaxKonVault:D`。
- 单元测试 12 个测试类、107 个用例，0 失败 / 0 错误 / 0 跳过：`CredentialVaultTest` 18、`AuthSessionTest` 14、
  `ElevationRepositoryTest` 13、`WireTest` 12、`BiometricCapabilityTest` 11、`ConnectionProfileStoreTest` 9、
  `MobileNavigatorTest` 10、`ProblemCodesTest` 8、`LayoutStateTest` 4、`TopDestinationTest` 4、`FilesRepositoryTest` 3、
  `RecentOperationJournalTest` 1。
- 密码可见性两态与主机指标按需采样落地后再次校验（同日）：`:app:assembleDebug` BUILD SUCCESSFUL，产物同上；
  `:app:testDebugUnitTest` 112 个用例，111 通过，其中 `ProblemCodesTest` 新增的 5 个用例（4xx/5xx 判定、命名问题码、
  凭据判定边界）全部通过。唯一失败的是 `MobileNavigatorTest` 的「首个子路由 push 触发路由观察者」用例——它来自工作树中
  尚未完成的导航改动，与本轮改动无关。
- 残留警告一处：`app/build.gradle.kts` 的 `resourceConfigurations` 在 AGP 9.4.1 已弃用，官方替代是
  `androidResources.localeFilters`。**该警告已在下一节「图标与语言」中随迁移消除**。
- 以上均为本机 JVM 单元测试与打包验证；设备矩阵验证仍未执行（见上）。

最近一次校验（2026-09-23，登录决策与本地凭据模型落地后）：

- `:app:assembleDebug` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL，Kotlin 编译零警告；
  产物为 `app/build/outputs/apk/debug/app-debug.apk`。
- 单元测试 15 个测试类、149 个用例，0 失败 / 0 错误 / 0 跳过：`CredentialVaultTest` 25、`AuthSessionTest` 16、
  `ConnectionProfileStoreTest` 16、`ProblemCodesTest` 15、`ElevationRepositoryTest` 13、`WireTest` 12、
  `BiometricCapabilityTest` 11、`MobileNavigatorTest` 10、`LoginDecisionTest` 7、`SavedCredentialStateTest` 6、
  `SelectedLoginTest` 6、`LayoutStateTest` 4、`TopDestinationTest` 4、`FilesRepositoryTest` 3、
  `RecentOperationJournalTest` 1。上一轮记录中失败的 `MobileNavigatorTest` 用例（工作树里未完成的导航改动）
  本轮已随该改动完成而通过。
- 本轮新增覆盖：§5.1 决策表逐行（含「字段不全优先于一切」「手动输入的密码覆盖已保存凭据」）；凭据四态与状态行映射；
  身份键归一化不折叠大小写、同服务器不同账号不碰撞；按对删除只影响一条登录且不触碰同服务器其他账号；
  `markInvalidated` 后记录与密文仍在、两个读取入口都被拒绝、重新保存可替换；保险箱格式升版后旧文件降级为空；
  认证失败不触发登录后的凭据步骤；限流既有专门文案也不被当成凭据拒绝。
- 仍未做设备矩阵验证：指纹授权、取消、失败、锁定、指纹变更后的「标记作废」提示都需要真机确认（见「后续步骤」）。

最近一次校验（2026-09-23，登录链路修复与 debug 明文兜底落地后）：

- `:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest --rerun-tasks` 均 BUILD SUCCESSFUL，
  Kotlin 编译零警告（release 变体一并编译，确认 `BuildConfig.DEBUG` 为假时 `debugCredentials` 的空分支成立）。
- 单测 21 个测试类、194 个用例，0 失败 / 0 错误 / 0 跳过：`CredentialVaultTest` 26、`AuthSessionTest` 18、
  `ConnectionProfileStoreTest` 16、`ProblemCodesTest` 15、`DebugCredentialStoreTest` 13、`ElevationRepositoryTest` 13、
  `WireTest` 12、`BiometricCapabilityTest` 11、`MobileNavigatorTest` 10、`SavedCredentialStateTest` 10、
  `LoginDecisionTest` 7、`DesktopIconsTest` 7、`SelectedLoginTest` 6、`VaultAccessTest` 6、`AppLanguageTest` 6、
  `LayoutStateTest` 4、`TopDestinationTest` 4、`ServerEndpointDiscoveryTest` 3、`VaultDiagnosticsTest` 3、
  `FilesRepositoryTest` 3、`RecentOperationJournalTest` 1。
- 产物 `app/build/outputs/apk/debug/app-debug.apk` 14.4 MB；三份 `strings.xml` 均为 285 键且键集一致。

最近一次校验（2026-09-24，文件传输落盘与回执归属修复后）：

- `:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest --rerun-tasks` 均 BUILD SUCCESSFUL，
  Kotlin 编译零警告。
- 单测 22 个测试类、201 个用例，0 失败 / 0 错误 / 0 跳过：本轮新增 `DownloadStoreTest` 6（重名追加计数、扩展名
  拆分、点文件、服务端名取末段路径）与 `FilesRepositoryTest` 的下载用例 1（sink 落字节 + 进度回调 + 不请求提权），
  `FilesRepositoryTest` 由 3 增至 4。
- 产物 `app/build/outputs/apk/debug/app-debug.apk` 14.4 MB；三份 `strings.xml` 均为 288 键且键集一致。
- **尚未真机验证**：`MediaStore` 落盘后 `Download/RelaxKonOS` 在系统「文件 / 下载」中的可见性、重名追加计数的实际
  文案，以及大屏与手机两端「点下载立即出现进度卡、完成后横幅报出落盘路径」。

最近一次校验（2026-09-24，图片预览与预览缓存落地后）：

- `:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL，Kotlin 编译零警告。
- 单测 24 个测试类、230 个用例，0 失败 / 0 错误 / 0 跳过。本轮新增：
  - `ImagePreviewCacheTest` 15：键对同一修订稳定、大小或修改时间变化即换键、不同服务器不同键、远端路径里的 `\` 与 `:`
    不会进入文件名；长度不符、空文件不算命中，未报告长度但有字节仍算命中；超预算时按最久未用先淘汰且满足预算即停、
    单个超过整个预算的条目被淘汰、`trim` 只删自己命名的文件（同目录下的其他文件不动）。
  - `ImageDecoderTest` 14：比显示框小的图不放大、4000×3000 取 1/2、8000×6000 取 1/4、结果不小于显示框且再降一级就
    小于显示框、全景图与 50000×50000 扫描图受 400 万像素上限约束、显示框或图片尺寸为 0 时退回原尺寸；扩展名与
    `mimeType` 的判定优先级、宿主不可解码的图片类型被排除、目录永不预览。
- 产物 `app/build/outputs/apk/debug/app-debug.apk` 14.4 MB；三份 `strings.xml` 均为 295 键且键集一致。
- **尚未真机验证**：先缩略图后详细图的实际过渡观感与大图首次载入耗时、EXIF 方向（竖拍照片应正立）、
  受保护路径的「授权并预览」只在点击后弹窗、预览缓存在 `cacheDir` 中的实际占用与淘汰效果。

最近一次校验（2026-09-24，服务端缩略图落地后）：

- 服务端：`dotnet build RelaxKonOS.Server.Tests` 成功；`RelaxKonOS.Server.Tests.exe --thumbnails-only` 22 条
  `PASS THUMBNAILS:` 全绿，全量序列（已在 `FileServiceChecks` 之后插入 `ImageThumbnailChecks.Run`）以
  `RelaxKonOS.Server backend verification passed.` 结束。
- 客户端：`:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL，
  Kotlin 编译零警告；单测 24 个测试类、232 个用例，0 失败 / 0 错误 / 0 跳过。本轮 `FilesRepositoryTest` 由 4 增至 6：
  透传 `maxEdge` 并回传字节而非字节数、受保护路径按**文件自身路径**以 `read` 提权一次后重试（范围不扩大到目录）。
- 产物 `app/build/outputs/apk/debug/app-debug.apk` 14.5 MB。
- **尚未真机验证**：320 px 小图放进 220 dp 卡片的实际观感（偏软就该调大）、`thumbnail-unsupported` 的回落路径、
  受保护路径上「预取不弹窗、下载卡片才弹窗」、以及「小图先到 → 详细图替换」的过渡。
- `ImageDecoder.decode(bytes)` 在 JVM 单测里无法覆盖（`BitmapFactory` 在测试运行时是 stub），只能真机验证或引入
  影子实现，本轮没有为它造这个轮子。

最近一次校验（2026-09-24，可空字符串读取修复后）：

- `:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL，Kotlin 编译零警告。
- 单测 24 个测试类、232 个用例，0 失败 / 0 错误 / 0 跳过；产物 `app/build/outputs/apk/debug/app-debug.apk` 14.5 MB；
  三份 `strings.xml` 均为 295 键且键集一致。
- 本轮**没有**新增用例，这是有意的：这条缺陷只在 Android 运行时复现——参考实现的 `org.json` 对 JSON null 返回空串，
  只有 Android 的 `JSON.toString(JSONObject.NULL)` 才给出 `"null"`，因此 JVM 单测区分不了修复前后（写出来是一个恒过的
  断言）。防护放在唯一入口 `optNullableString` 与其注释上：可空字符串不再有第二种读法。
- **待真机确认**：进程行的属主段消失（Windows 服务端恒无属主）、文件详情的 MIME 不再显示 `null`。

最近一次校验（2026-09-26，登录身份执行资格落地后）：

- `:app:testDebugUnitTest` BUILD SUCCESSFUL：**357 个用例、36 个测试类，0 失败 / 0 错误 / 0 跳过**。
  本轮新增的 6 个（`ProblemCodesTest` +4、`AuthSessionTest` +2）全部通过。三份 `strings.xml` 均为 **346 键**且键集一致。
- **顺带修掉一个既存编译中断**（与本次需求无关，但会让整仓 Android 无法构建）：`ui/servercenter/ServerCenterScreen.kt`
  显式 `import androidx.compose.foundation.layout.weight`，在 Kotlin 2.2.10 + 当前 Compose BOM 下解析到
  `RowColumnParentData.weight` 这个 **internal** 声明，报
  `Cannot access 'val RowColumnParentData?.weight: Float': it is internal in file.`。该文件由 2026-09-25 的提交
  `f0696d5f` 引入，晚于上一次校验记录，因此从未编译过。修法是删掉这行 import——两处 `Modifier.weight(1f)`
  都在 `Row { }` 内，本来就该走 `RowScope.weight` 这个成员。**这是本模块目前唯一的编译阻断点，已解除。**
- 环境说明：本次用 Gradle 9.6.0 + JDK 21（`C:\Program Files\Android\openjdk\jdk-21.0.8`）+ SDK `E:\environments\Android\Sdk`。
  仓库**跟踪**了 `local.properties`，其 `sdk.dir` 仍指向已不存在的 `D:\environments\Android\Sdk`，跑之前需临时改成实际路径、
  跑完还原（`gradle-wrapper.properties` 的 `distributionUrl` 同样指向 D:，只有用 wrapper 启动时才受影响）。
  另：**不要加 `--offline`**，Android Gradle Plugin 不在本机 Gradle 缓存里，离线会直接解析失败。
- **未覆盖**：`parseLogin` 无法单测（单测跑在被 stub 的 `org.json` 上），`executionEligibility` 的解析路径只能在真机验证。

## 后续步骤

- 按设计文档 §8 的最小设备矩阵补齐真机验证：一台手机（竖/横屏）、约 8 英寸与约 11 英寸平板，逐台验证指纹登录
  （成功/取消/失败/锁定）、**新录入指纹后记录变为「已失效」而不是消失**、删除记录后重建的指纹、指纹提权、软键盘遮挡、
  旋转、后台恢复与危险操作确认文本。
- 真机验证时顺带确认窗口模式（`WEAK_ONLY` / `DEVICE_CREDENTIAL_ONLY`）下五分钟内免二次确认，以及窗口过期后回落为
  正常授权提示而不是「已失效」。
- V1-C 终端：SignalR 客户端与 PTY 渲染。
- V1-E 其余域：Docker、部署、守护（按服务端能力门控）。

## 2026-09-29：Windows 权限提示与授权边界

主页根据服务端 `executionEligibility.reason` 分别显示 Windows profile、Linux 保留身份/系统账户和缺少家目录原因；Windows 登录不再显示 Linux UID/root 的泛化文案。Android 的业务授权保留在客户端；宿主 UAC 只用于部署/启动特权 Helper，日常操作不要求用户操作 Windows。文件沿用专用路径授权；Windows 受管 Nginx/FRP 的执行规则由 [Server/Helper 运维契约](../../../docs/platform/RelaxKonOS.PrivilegedOperations.Operations.md) 定义。当前 Android 未提供 FRP 生命周期管理页面；该能力的 Server/桌面调用者已更新，不把它记为移动端功能完成。
