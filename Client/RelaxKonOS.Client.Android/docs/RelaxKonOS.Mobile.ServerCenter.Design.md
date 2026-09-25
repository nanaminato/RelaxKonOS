# Android 服务器中心接入设计

> 状态：设计完成，待实施。跨客户端部署契约、宿主安全与验收以 [ServerCenter Goal](../../../docs/platform/RelaxKonOS.ServerCenter.Goal.md) 为准；本文只规定现有 Android App 如何接入。
>
> 基线：独立 Kotlin / Jetpack Compose / Material 3 工程；`MainActivity` 在未认证时显示 `LoginScreen`，认证后显示五类导航的 `ShellScaffold`。当前没有内置 SSH/SFTP 或服务器部署页面。

## 1. 体验原则

「安装服务器」是**开始使用 RelaxKonOS 之前就可能发生**的任务，不属于登录后的 `Manage` 能力目录。用户原本只想连接一台已安装的服务器时，登录表单仍然是首屏：地址、标识、密码和保存密码状态行不变。服务器中心作为可发现的次级入口出现；安装成功后回到同一表单。

保留现有五项顶级导航（主页 / 文件 / 终端 / 管理 / 更多）和 `TopDestination.visible(capabilities)`。不要新增第六个底栏项，也不要让 Server Capabilities 决定安装入口是否存在。未认证、服务端停止和卸载后都必须能进入服务器中心。

## 2. 现有落点与目标交互

| 现有落点 | 加入的入口 | 不改变的行为 |
|---|---|---|
| `ui/connect/LoginScreen.kt` | 登录按钮下的「安装或管理服务器」次级按钮；可带入当前合法的地址，但不提交登录。 | 单形态三字段、凭据状态行、手动密码优先、保存凭据的生物识别决策。 |
| `ui/connect/ConnectionListScreen.kt` | 每条登录行的更多操作中可进入其**已明确关联**的宿主详情；未关联时提供「设置 SSH 管理连接」，不增加第四个常驻文字按钮。 | “使用 / 忘记密码 / 删除登录记录”仍只作用于该 `(serverUrl, identifier)`。 |
| `ui/more/ConnectionsScreen.kt` | 在登录资料组之后增加「服务器维护」区，显示当前宿主的安装状态和「打开服务器中心」。 | 已登录时不从此页切换账号；登出仍是切换服务器登录的显式路径。 |
| `ui/home/HomeScreen.kt` | 只在已核实当前安装有更新或异常时显示轻量提示，点入详情。 | 首页 hero 的身份、主机与指标信息；不把部署进度塞进指标卡。 |
| `ui/nav/Routes.kt`、`MobileNavHost.kt` | 已认证层仅增加通往宿主流程的入口；宿主列表、详情和操作步骤由独立的服务器中心导航状态管理，路由不携带凭据、路径或操作 JSON。 | 五个 `TopDestination`、能力门控、Compact/Medium/Expanded 的导航策略。 |
| `MainActivity.kt` / `AppContainer.kt` | `MainActivity` 的根界面在登录/Shell 之外呈现服务器中心；`AppContainer` 持有协调器和当前宿主流程状态，退出时返回来源页面。 | `AuthSession` 是登录真源；主题背景和现有 `ElevationDialog` 不变。 |

未登录时次级入口始终显示，即使没有 `SavedLogin`。选中已有登录记录只预填目标，不因两个 URL 指向同一 IP 就自动认定同一安装。部署完成时返回同一登录表单，显示经验证的宿主名称；直连时可回填持久 API URL，隧道时只把本次 loopback URL 交给连接解析器，不持久化它。`identifier` 仍由用户决定；离开表单时清除本次 `passwordText`，不能把登录密码留在部署导航状态中。登录成功后才写入登录记录。

### 2.1 页面与自适应布局

```text
未登录：LoginScreen ─ 安装或管理服务器 ─ 宿主列表 ─ 宿主详情 ─ 步骤/进度 ─ 返回登录
已登录：更多 → 连接 ─ 当前宿主详情 ─ 步骤/进度 ─ 重新探测/重新登录
           主页异常或更新提示 ────────┘
```

- **Compact (<600dp)**：全屏详情和逐页任务；步骤标题、目标主机、阶段与主操作固定可见。手机返回键先关闭确认，再回上一层；执行临界阶段返回只离开视图，不取消远端操作。
- **Medium (600–839dp)**：沿用 rail，宿主列表与详情仍按页切换。软键盘弹出时主机指纹确认和卸载最终确认不可被遮挡。
- **Expanded (≥840dp)**：沿用左 rail，宿主列表与详情双栏，右侧可展示阶段记录。桌面式多窗口或第六导航项都不需要。
- 使用现有 `ScreenHeader`、`SectionGroup`、`ListRow`、`StatusChip`、`ErrorBanner`、`AppBackdrop`、`Spacing/Radius` 令牌和桌面镜像图标语义。主机指纹是专门确认页；卸载的「保留数据/删除数据」和输入主机名确认是两个明确步骤。英文、中文、日文字符串通过三套 `strings.xml` 同步提供。

## 3. 三种身份与本地资料

| 资料 | 用途与键 | 持有者 |
|---|---|---|
| `SavedLogin` | 当前键为 `(serverUrl, identifier)`；隧道接入前演进为稳定 `(serviceId, identifier)` | `ConnectionProfileStore` 与 `VaultKind.Connection` |
| `HostTarget`（新设计名） | SSH 目标、已确认主机密钥、受管安装 ID、部署模式和最近验证状态；可在没有 `SavedLogin` 时存在 | 新的设备本地宿主仓库 |
| SSH 凭据（可选保存） | 只用于连接宿主 SSH；以主机密钥身份、端口和 SSH 用户绑定 | 独立 Keystore 凭据域；不可重用 `Connection` / `Elevation` 记录 |
| 宿主提权凭据 | Linux sudo / Windows 管理权限预检与操作；不是 RelaxKonOS API 提权 token | 本次操作的最小生命周期；如设计保存，另立规则，不自动读取现有 `Elevation` 保险箱 |

`VaultKind.Connection` 保存的是 RelaxKonOS 登录密码，`VaultKind.Elevation` 服务于现有 `/privileged/elevation`；两者的 AAD 都按服务器 URL 与账号绑定，不适用于 SSH 的主机密钥身份。若扩展现有 `CredentialVault`，须新增独立 kind、文件与 Keystore alias，并同步更新每一个 `when`、保险箱管理页面和测试。现有 debug-only 明文登录兜底不能用于 SSH 凭据。主机指纹可明文保存，但必须抗意外覆盖并在变化时阻断写操作。

### 3.1 稳定身份与动态隧道端口

当前 `SelectedLogin`、`ConnectionProfileStore`、`CredentialVault`、`AuthSession` 和 `RelaxKonApi` 都将 `serverUrl` 同时作为**身份**和**请求地址**。隧道绑定的本地端口可能变化，直接存 `http://127.0.0.1:<port>` 会创建新的 `SavedLogin`，旧保险箱也无法命中。因此接入隧道之前，必须直接调整本地模型与所有调用者：

- 直连的 `serviceId` 为规范化的持久服务器 URL；受管隧道的 `serviceId` 为 SSH 主机密钥与安装清单共同验证过的安装 ID。登录与保险箱以 `(serviceId, identifier)` 为键，不以当前端口为键。
- `effectiveBaseUrl` 是当前会话的实际 HTTP 地址。直连时等于持久 URL；隧道时由连接解析器建立并持有。`AuthSession`、文件、指标、提权等所有 `RelaxKonApi` 请求都从同一解析器取得地址。隧道断开时先重建并核对主机身份，换端口后只更新传输地址。
- 登录表单的“服务器”字段在直连模式仍可编辑原 URL；选择受管隧道时显示宿主名称和「通过 SSH 连接」说明，编辑入口回到宿主详情。不能把临时 loopback 地址伪装为用户应保存的服务器地址。
- 这是发布前的本地接口演进；同一变更要更新 `SelectedLogin`、档案存储、保险箱 AAD、登录决策、会话、API 调用、测试与 `RelaxKonOS.Mobile.LoginCredentials.Design.md`。不增设依赖旧端口键的兼容分支。对已保存的直连登录要有明确的本地数据处置与验证，不能默默丢失密码记录。

在宿主目标中记录由部署引擎签发/读取的安装 ID；连接到 API 后，再用经验证的安装信息把目标与一个或多个 `SavedLogin` 关联。**删除登录记录不删宿主，忘记密码不删 SSH 凭据，卸载服务端不批量删除登录记录。** 删除宿主资料时要提示其关联的登录记录和 SSH 凭据分别如何处理，不能把三件事合成一个按钮。

## 4. 操作状态的归属

`LoginViewModel` 和 `MobileNavHost` 只负责各自现有界面。新增 `ServerCenterCoordinator` 由 `AppContainer` 持有，负责 SSH 会话、阶段事件、宿主锁/操作 ID、通知和重连；`MainActivity` 的根界面按当前流程选择登录、Shell 或服务器中心。进入服务器中心时记录来源，返回时恢复原页面。任务在页面销毁、旋转或语言切换时不因 Composable 被移除而丢失。

远端操作记录是权威结果，本地另建**不含凭据**的恢复记录：安装 ID 或待安装主机指纹、操作 ID、阶段序号、启动时间、目标版本与最近核验时间。恢复时先重新验证 SSH 主机密钥，再读取远端结果，不能用本地缓存直接宣称成功。`RecentOperationJournal` 只保存本进程内的简短成功操作，不承担部署恢复或审计；文件上传的 `UploadResumeJournal` 记录的是 API 上传会话，也不能拿来复用部署操作 ID。

Android 后台可中断客户端进程。包上传等需要持续本地传输的阶段，应仿照现有 `UploadForegroundService` 的可取消、可见通知方式实现独立传输任务；进入远端受控执行后，由远端任务存活，通知只展示进度与最终结果，不把客户端常驻当作成功条件。上传/下载包通过 Storage Access Framework 选择或保存用户文件，不申请整个存储空间权限。

状态区分 `SSH 不可达`、`SSH 已认证`、`宿主已安装`、`API 健康`、`RelaxKonOS 已登录`；`ServerEndpointDiscovery` 仍只是登录表单的地址提示，不能作为部署预检门禁。卸载后 `AuthSession` 失效是预期结果，服务器中心必须留在可访问的未登录层。

## 5. 验收与实施顺序

1. 先落地跨客户端部署契约、稳定 `serviceId` / 动态 `effectiveBaseUrl` 的 Android 模型和 `HostTarget` / SSH 信任存储测试，再接入内置 SSH/SFTP；首个竖切实现「未登录预检 → 安装 → 返回现有登录表单」。
2. 接入「更多 → 连接」与主页轻量提示，再完成更新、修复、卸载以及后台恢复；不抢先重画整个 Shell。
3. 对 `LoginViewModel` 的现有登录决策、`ConnectionProfileStore` 同服多账号、两种保险箱、`TopDestination.visible` 与三种 `LayoutState` 做回归。增加未安装、SSH 不可达、主机密钥变化、后台回收、更新断线和卸载保留数据的状态测试。
4. 在手机竖/横屏、约 8 英寸与 11 英寸平板上核验软键盘、长指纹、长主机名、大字号、系统返回、语言切换和通知权限被拒时的进度可见性。未经真机核验不把“后台任务持续”或“远程 Windows 管理员可用”标为已完成。

服务器中心首次发布时，`LoginScreen` 直接连接已安装服务器的路径和已有登录记录的密码操作必须原样可用；没有 SSH 权限的用户仍可按现有方式登录工作区。
