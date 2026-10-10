# RelaxKonOS 设置系统与设置应用

> 当前界面方向：参考 Windows 11 设置，以清晰分类、卡片分组、常用项直接可见和低频项折叠降低负担，不要求沿用原有交互。普通偏好自动保存；主机名和时区在页内明确应用，草稿切页保留，可直接重置，不增加离页确认弹窗或首页待办中心。底层协议约束继续有效，界面不展开其实现细节。

本文描述设置应用的当前行为。范围、安全契约与完整验收矩阵见 [设置设计](./RelaxKonOS.Settings.Design.md)。宿主写入的实现与实机验收状态分别列出。
最新测试范围、发现的问题及实机部署限制见 [设置验收记录](./RelaxKonOS.Settings.Acceptance.md)。

## 服务与真源

设置应用是入口，偏好读写服务可独立调用。Client 的 `Services/WorkspaceSettings/IWorkspaceSettingsService` 供 Settings、Shell、Explorer、SDK、编码设置及默认程序入口共同使用。`WorkspacePreferencesEditor` 管理冻结草稿、300ms 防抖、目标绑定及重试；窗口关闭不会取消保存，连接变化清理旧目标草稿。

`PreferencesSync` 负责登录加载与设置变化订阅，更新 ShellSettings 和 DefaultAppRegistry。订阅绑定连接目标，重连先订阅再重取快照；有草稿时保留草稿，避免远端变化覆盖编辑。

Server `Settings/IWorkspaceSettingsService` 管理偏好验证和版本比较。数据来源为配置注册表 `Workspace\Desktop` 的 `(Default)` JSON 值；当前 SQLite 缓存延迟落盘，客户端在后台跟踪 `persistedRevision`，界面不展示保存中、已接收或持久化成功提示；失败显示五秒 toast 并保留日志与草稿。损坏的偏好返回错误并保留原数据。

`GET /api/v1.0/workspaces/{id}/preferences` 返回 `WorkspacePreferencesDto.revision`；PUT 使用同一 DTO，必须携带编辑基线 revision。缺失返回 428，冲突返回 409。不能先读取新 revision 再给旧草稿换版本强行保存。Workspace 归属由认证身份校验；AppSettings 仍仅存应用私有偏好，不能存 OS 配置。

## 实时变化

`/hubs/settings-changes` 使用现有 SignalR 与 JWT 认证。`Subscribe(workspaceId)` 验证认证用户拥有该 Workspace；服务端每秒观察已订阅资源的 revision 与持久 revision，因此普通偏好 API、壁纸更新和注册表编辑器的写入均能触发通知。通知只包含 Workspace 标识与版本，不携带偏好值，接收端使用授权 GET 重读。连接过期关闭，客户端重连重新订阅并读取；另有 30 秒只读恢复检查，修复丢失通知或失败读取，不排队重放写操作。

## Windows 11 风格界面改造

应用信息同样使用统一导航：“应用 › 应用名”，权限页为“应用 › 应用名 › 应用权限”。应用列表、信息和权限拥有独立路由，历史包含应用 ID，恢复对应应用及滚动位置；详情内部的小返回入口和重复“应用信息”标题已移除。权限在页面内编辑，只有显式保存才写入授权，取消不提交，返回同一应用保留未提交草稿；清除数据和卸载仍使用确认对话框。

设置顶栏现在使用窗口框架的 `TitleBarContent` 入口，与内容区融合：左侧为返回箭头和“设置”，中央为统一搜索框，窗口按钮仍由宿主管理。搜索不再放在侧栏，也不随页面内容滚动；Ctrl+F 聚焦顶端搜索，Escape 清除搜索并恢复页面位置。宽窗口保留侧栏，窄窗口继续使用导航抽屉。

内容区使用可点击的大面包屑代替小型“返回 / 路径”行及重复标题。个性化详情、默认应用、系统偏好和网络适配器详情均可通过父级面包屑返回。网络卡片进入 `network/adapter` 独立详情页，标题显示实际网卡名；父级“网络”回到列表，顶栏箭头沿历史返回。网卡编辑与未确认操作由原服务继续管理，切页不取消连接恢复保护。 网络页移除重复的“服务器 IP 地址”汇总卡片和额外地址查询，IP 信息统一在网卡详情中查看；连接状态、连接测试及侧栏连接地址继续保留。

改造进度、阶段边界和验收清单见 [Windows 11 风格改造进度](RelaxKonOS.Settings.Windows11.Progress.md)。已实现首页、分类与详情导航分离、统一页面标题、个性化四个详情页、设置项搜索定位与范围说明。时间语言页与完整根窗口通过 headless 交互和小视口可达性检查；原生人工交互、系统缩放和屏幕阅读器验收尚未执行。

新增“辅助功能”页和“系统 → 通知与启动”页：文字/界面缩放、减少动画、高对比度、自动化通知横幅/免打扰、登录后恢复运行中的终端、全屏连接栏固定均为设备偏好，保存在本地 `RelaxKonOS/desktop-device.json`。普通开关即时生效；终端恢复开关在下次桌面入口执行，不结束已有终端。设置页标题可固定当前页到首页。个性化各详情、日期/时间格式、默认应用以及设备设置提供分组恢复默认值。

这些新增功能已通过本轮 headless 自动检查；原生视觉与实机行为验收仍待执行。通知只覆盖自动化任务，启动恢复只覆盖服务端运行中的终端，宿主操作恢复沿用当前页面的操作详情。

## 当前页面与保存状态

连接地址在宽侧栏和窄屏抽屉中直接显示，可选择复制。个性化概览使用渐变横幅、实时主题色预览及四个矢量图标卡片。普通偏好后台保存，成功与持久化过程不显示状态卡片；失败只显示五秒 toast，保留诊断日志及草稿。

网络页仅管理当前连接的远程主机，没有本机系统设置入口。现代卡片按以太网、Wi-Fi 和其他适配器展示连接状态，并提供 IPv4/IPv6 地址、网关、DNS、MAC 和链路速度详情。支持在 Windows 及 Linux NetworkManager 管理的活动以太网／Wi-Fi 网卡上修改 IPv4 DHCP／手动地址、前缀长度、网关与自动／手动 DNS；其他 owner、复杂配置和不支持的网卡明确只读。当前未提供 Wi-Fi 扫描、加入新 SSID、IPv6 写入或 systemd-networkd 配置。

网络写入走 `GET /host-settings/network`、`POST /host-settings/network/apply` 与 `/confirm`，由独立连接绑定客户端服务调用。修改需要精确 `host/network` 的 `HostNetworkChange` 授权，Helper 再验证结构化输入和快照 revision。

客户端先生成操作 ID，丢失响应不重放写入。服务端完成 Helper 写入前不能确认，确认只允许原操作账户。Windows 先注册 SYSTEM 计划任务保存原 IP/DNS，Linux NetworkManager 先创建检查点；未确认时由远程系统在 120 秒后恢复，不依赖客户端或 Server 仍在线。

客户端提供 90 秒保留确认窗口，确认后移除恢复任务／检查点。关闭设置或断开连接不会取消恢复保护。

实现依据：[Windows netsh](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-interface)、[Windows 计划任务](https://learn.microsoft.com/en-us/powershell/module/scheduledtasks/register-scheduledtask)、[NetworkManager 检查点](https://networkmanager.pages.freedesktop.org/NetworkManager/NetworkManager/gdbus-org.freedesktop.NetworkManager.html)。

自动化验收使用模拟服务与 Helper，覆盖授权、参数、版本冲突、操作归属、不重放和三语言窄窗口；真实网卡变更及恢复仍需隔离环境验收。
设置请求失败自动写入客户端 `%LOCALAPPDATA%/RelaxKonOS/logs/workspace-preferences-YYYYMMDD.jsonl`（每文件上限 2 MiB，最多保留一个轮换文件，清理七天前日志）。记录操作、Workspace、预期 revision、异常类型、HTTP 状态与服务端返回的 correlation ID；不记录令牌、请求/响应正文或异常消息。

界面按网络、登录/权限、无效设置、需要重载及服务端失败显示本地化提示，仍保留草稿；冲突必须显式重载，不自动覆盖远端版本。令牌刷新后按稳定服务、登录会话和 Workspace 确认保存 revision，避免下一次保存误用旧版本。

开发服务端未配置 `Observability:LogDirectory` 时默认写入输出目录 `data/logs/runtime-YYYYMMDD.jsonl`；安装版使用配置的运行日志目录。所有 HTTP 4xx/5xx 请求均记录完成事件，不受成功请求采样率影响。客户端和服务端日志可按 correlation ID 对照。历史上未开启文件日志的开发请求无法追溯；这些默认值仅对重启后的开发服务端生效。

回归验证：`dotnet run --project Tests/Client/RelaxKonOS.WorkspacePreferences.Tests` 覆盖令牌刷新后连续保存、旧登录/其他服务响应隔离、HTTP 错误分类、草稿保留、取消请求不能覆盖重试及客户端日志不含令牌/正文；`dotnet run --project RelaxKonOS.Server.Tests -- --settings-only` 覆盖完整调色板导入和 HTTP 保存、失败请求零采样仍记录关联日志及原有并发/持久化检查。

内置桌面布局的 Workspace 与设备本地选择均只保存 `shellId`；描述器的内置实现版本不属于外部包信息。Settings 与桌面快捷切换共用 `ShellSettings.SelectShell`，外部桌面继续携带包 ID 和版本。服务端拒绝无效偏好时返回 `invalidField`，并记录同一 correlation ID 的 `input.rejected` 事件，只包含固定字段路径，不包含字段值。回归覆盖真实个性化页切换三种内置布局，再修改颜色/壁纸，以及外部包元数据保留。

当前一级分类为首页、系统、网络、个性化、应用、账户与安全、时间和语言、辅助功能、开发者。默认应用归入应用详情；关于保留独立页面并置于导航底部。环境变量详情页及已有壁纸、调色板、系统风格和 Shell 布局能力继续保留。Docker Hub 镜像源属于 Docker 管理器的“镜像源”页，不在设置应用中展示。

个性化已改为概览和“颜色与模式”“系统风格”“桌面布局”“背景”四个详情页（见下节）。保存状态支持中文、英文、日文；失败保留草稿并可重试，冲突保留草稿，提供明确的“放弃草稿并重载”操作；重载失败仍保留草稿。首页已实现常用入口与固定页；辅助功能已实现，原生验收待补。不扩展逐字段冲突合并界面或首页待办中心。

分类与详情页使用注册的 Route 导航，共用单色矢量图标；持续显示账号、Workspace 与分类路径，连接地址直接显示并可选择复制；每页提供作用范围说明，混合范围页面在关键分区注明设备、工作区或远程主机。返回历史保存详情路由与搜索词，页面缓存滚动偏移，连接变化清理旧导航上下文。

小于 760 个逻辑像素时折叠侧栏，使用可展开的导航抽屉；选择分类后收起，Escape 关闭并恢复菜单焦点，Ctrl+F 关闭抽屉并聚焦可见搜索。搜索先查询本地不可变索引，再异步合并远程目录；包括标题、关键词和同义词，显示分类、范围及服务端能力原因，连接切换清除旧目录。Ctrl+F 聚焦搜索、方向键浏览、Enter 或双击打开、Escape 退出搜索。

搜索结果进入所属页面后按视图登记的 settingId 定位、滚动、聚焦并临时高亮卡片；不可见项提供解释，禁用项不被启用。环境变量定位到系统页的现有子窗口入口。个性化目录使用当前详情路由；其他详情页拆分与完整窗口交互验收仍待完成。标题与内容共用滚动区域；时间语言页和根窗口已验证小视口可达性。全部页面的窄布局、原生 200% 缩放及屏幕阅读器体验尚未完整实测。

保存反馈带有内存中的修改来源（应用与页面）。来源页就地显示状态与重试/放弃操作；切到其他页后，未完成或失败的保存显示来源摘要并提供“查看修改”。已完成的其他页面保存不再常驻显示。来源不改变偏好协议或写入目标，Workspace/登录变化清理来源；服务端接收仍不等同已持久化。`Schedule(preferences, source)` 的非设置窗口调用者显式传 `null`。

## 个性化页：颜色、系统风格与桌面布局

个性化概览提供颜色、系统风格、桌面布局、背景四个详情入口。前三个详情页把原先混为一谈的“主题”拆成三个独立设置分区，对应 `DesktopExperiencePreferencesDto` 的三个字段：

1. **颜色与模式**：`ThemeKind` 模式、调色板 ID、强调色覆盖与自定义调色板，写 `DesktopExperience.Appearance`。
2. **系统风格**：风格下拉（`SystemStyleChoices`）、不可用提示与“使用推荐风格”按钮，写 `DesktopExperience.SystemStyleId`；已经使用当前桌面推荐风格时隐藏按钮，不展示尺寸参数和实现说明。
3. **桌面布局**：Shell 选择，写 `DesktopExperience.Shell`；卡片仅保留标题和选择控件。

关键约定：

- **颜色与形状互不牵连。** 改调色板不会改变菜单布局或窗口控制按钮位置；改系统风格不会篡改调色板。
- **可用性是设备本地事实。** 若本机缺少所选风格，`SystemStyleRegistry` 保留该条目与原因，页面显示“此设备未安装”并继续使用最近有效的可渲染风格；**不静默改写用户的偏好**。
- **推荐映射只是按钮。** “使用推荐风格”由 `SystemStyleIds.RecommendedForShell` 驱动，需用户显式点击，不随 Shell 切换隐式生效。
- 本地搜索条目由 `workspace.theme` / `workspace.shell` 改为 `workspace.colors` / `workspace.systemStyle` / `workspace.desktopLayout`（含中英日同义词）。
- 三语言 `settings.json` 已补齐 `settings.colors_and_mode`、`settings.palette_scope_hint`、`settings.system_style.*`、`settings.desktop_layout` 与全部 `systemstyle.*` 问题码文案；移除了系统风格和桌面布局卡片不再显示的说明与尺寸参数标签。

系统风格层本身的令牌、recipe、清单校验与运行时链路见 [`RelaxKonOS.SystemStyle.md`](./RelaxKonOS.SystemStyle.md)。自动化检查覆盖编译、契约与部分桌面布局；完整视觉、键盘与辅助功能验收仍待完成，见 [内置应用 UI](./RelaxKonOS.BuiltInApps.UI.md)。

## 范围与宿主权限

- ClientDevice：此客户端设备的布局、开发模式和辅助功能。
- Workspace：当前用户 Workspace 的外观、系统风格、语言、默认应用及后续环境覆盖。
- AppPrivate：AppSettings 隔离的应用私有配置。
- HostUser：认证映射的远程 UID/SID，不能使用 Server 服务账户的用户环境。
- HostMachine：远程机器配置，不归某个 Workspace 所有。

允许设置远程环境变量、时区、主机名和受支持 DNS；旧“Settings 不触及宿主配置”“环境变量操作一律禁止”限制已废止。Server 保持非特权，通过现有 Helper 封闭操作、身份绑定、授权、审计、读回及恢复实现。环境配置数据不得注入 Helper 或特权子进程启动环境。DNS 写入前必须具备不依赖 Client/Server 存活的宿主恢复任务。

远程配置 provider 与全部宿主验收尚未完成；必须在明确指定的远程测试主机或隔离 VM 验证，不得在开发机实验后声称远程验收通过。

## 宿主主机名服务（实现，尚未实机验收）

新增 `/host-settings/identity` 的 GET/preview/apply，并复用操作查询与回滚。`HostIdentityState` 同时给出生效名称与待生效名称，以及平台上报的名称长度上限；客户端因此使用远程上限校验草稿，不按本机平台猜测规则。预览计划持久加密并绑定 actor、目标、名称摘要、观测 revision 与五分钟期限；应用需要精确 `host/identity` 的 `HostIdentityChange` 授权，读回确认待生效名称后才报告 Applied。

Windows provider 只读固定 `ComputerName` 注册表位置并用 `SetComputerNameEx` 暂存新名称，重启后才成为系统名称，快照的 `EffectiveState` 为 `HostRestart`；Linux 通过固定 `hostnamectl` 立即生效。系统页据此显示“当前生效名称/待生效名称”，待生效与生效不同时提示重启，并在结果为未知或需要恢复时明确要求管理员在主机上手动设置名称。客户端 `Services/HostSettings/IHostIdentityService` 独立于窗口，沿用相同的连接冻结与不重放写入约定。

三语言文案、目录条目、搜索关键词与 DevCli `hostname` / `preview-hostname` / `apply-hostname` 已接入。真实 Windows 重启后生效、域策略拒绝与 Linux `hostnamectl` 写后读回均未在指定远程测试目标验证；受控 provider 行为测试与编译不构成平台验收。

## 宿主时区服务（实现，尚未实机验收）

新增目录和时区 GET/preview/apply，以及操作查询与回滚 API。Server 通过原有 Helper 执行 Windows tzutil / Linux timedatectl；预览计划持久加密，应用需要精确 `host/time` 授权，读回成功才报告 Applied。

外部版本变化会阻止应用或回滚；丢失结果为 Unknown，不自动重放。客户端 `Services/HostSettings/IHostTimeService` 独立于窗口，冻结 Server URL、用户和会话身份，发送前后检查连接，不自动重定向或重试写请求。时间和语言页现已接入远程快照、目标/身份、远程时区枚举、单步应用与必要授权、按原 planId 查询；不再用客户端 `TimeZoneInfo.Local` 冒充宿主值。宿主编辑不触发 Workspace 防抖保存。

授权通过既有 `/privileged/elevation` 和本地渲染的宿主密码对话框；有效的精确资源授权可复用且不延长到期时间。连接切换清除草稿、计划和旧请求结果；窗口关闭不影响 Server 已持久化的操作。三语言按钮与操作状态已接入，但完整错误映射、原生布局/键盘和远程实机验收仍待完成；草稿切页保留且不增加离页确认，恢复沿用页内操作详情，不能将构建通过视为完整时区交付。

## 宿主环境客户端服务

`IHostEnvironmentService` 已注册为独立 typed HttpClient，提供目标解析、直接读取完整变量值、预览、按 planId 应用、操作查询和带 revision 回滚。读取、修改分别请求 `HostEnvironmentRead`、`HostEnvironmentChange` 精确资源授权；调用者按需要依次请求，服务不隐式扩张权限或缓存密码、原始环境值。

新增 `GET /api/v1.0/host-settings/environment/target?scope=hostUser|hostMachine`，只返回当前认证用户经 Server 验证映射的 `SettingsTarget`，不读取环境、不调用 Helper、不授予权限，响应禁止缓存。

客户端通过此入口取得授权目标，不从本地设备猜测远程 SID/UID。Windows 当前认证用户自己的环境 store 经 canonical SID 归属检查后无需管理员认证；系统 store 的读取/修改需管理员资格或环境授权；桌面双列表编辑器先请求机器环境修改授权，再加载两组变量。

精确资源授权与 Windows 双 store 授权范围以服务端验证结果为准；读取原值不另设揭示 capability。

时区和环境服务共用 `HostSettingsService` 的连接冻结与 HTTP 流程：取得 token 前后及响应解析后校验 Server/用户/会话，禁用重定向和写请求重试，不经过可重放的认证 handler。环境服务已接入设置编辑 UI；SDK `ISettingsNavigation.OpenEnvironmentAsync()` 与终端菜单复用 `relaxkonos://settings/system/environment` 打开同一宿主编辑器，不授予读写权限。外置应用宿主写 API 尚未开放。

DevCli 现已接入 `environment-target`、`environment`、`preview-environment`、`apply-environment`，并复用 `operation`、`rollback`。变更读取 UTF-8 JSON 文件或标准输入，不接受变量值命令行参数；读取直接返回完整变量值。

它依赖已有宿主 JWT 的短期授权，缺少时返回结构化错误，不打开密码窗口。完整命令和格式见 `Tools/RelaxKonOS.DevCli/README.md`。环境编辑 UI 已接入（含 PATH 分项编辑），Linux `/etc/environment` provider 已由 Helper 分派并做字节 revision 条件化的原子替换；Workspace 环境授权 GET/PUT 与加密持久化已实现；Workspace 环境客户端编辑已实现；尚未完成的是非特权工作负载启动边界接入、宿主设置实时通知，以及外置应用宿主写 API。

## 环境变量页面

环境编辑器由系统页入口或 `relaxkonos://settings/system/environment` 打开。Windows 使用用户变量与系统变量两组列表，授权读取后直接显示原值；新增、编辑和删除在子对话框确认后通过预览/应用服务提交并重新读取。Linux 只提供机器范围的 PAM 登录环境，逐项编辑后暂存为批次，通过底部“应用”提交；取消关闭会丢弃未提交草稿。没有额外揭示授权或掩码读取流程。

变量与 PATH 共用编辑对话框；PATH 按远程平台使用 `;` / `:`，支持分项新增、替换、删除与排序，保留重复、空项和顺序。删除最后一个路径项表示空 PATH，删除变量仍使用独立删除动作。远程路径存在性尚未检查。原始值不经 shell 求值；切换会话或关闭编辑器清除内存数据。

真实宿主授权/读写与完整视觉验收仍待完成。Workspace 分区、工作负载环境构造、环境提交结果未知的持久查询交互和外置应用宿主写 API 仍待实现；不能把时区/主机名已经具备的恢复界面描述成环境编辑器已有能力。

宿主设置查询对过期且仍为 Prepared 的计划在协调锁内写入 Failed/`settings.plan_expired`；延迟 apply 返回该终态，不执行旧写入。此规则同样适用于桌面和 Android；Applying/Unknown 不因超时被当作未执行。HostSettings 拒绝响应具有稳定 `problemCode` 扩展。

桌面远程时区编辑支持按城市、系统时区名称、ID 或 UTC 偏移搜索，并展示系统时区名称与远程 ID。名称解析仅用于显示；可提交值始终来自远程目录，无法在客户端解析的 ID 原样展示。页面分别标注当前时区和新时区，选择后直接应用，后台执行计划准备与必要授权；提交后显示查询操作，成功后可继续选择时区，需要复原时手动选择原值。不显示预览或计划期限，也不显示计划 ID、操作详情或成功提示（结果只体现在「当前时区」行）；错误仅在有内容时显示，失败另以对话框提示。

时区授权后显示执行进度；远程确认 Applied / RolledBack 后不再显示完成卡片，页内不出现成功文案（仅当远程未回传确认 revision、仍需查询时保留文字状态行），并只用远程确认的 revision 更新编辑快照（「当前时区」行随之更新），允许继续选择。页面不提供专用回滚按钮，恢复原时区作为普通修改提交；页面不展示计划 ID 或操作详情，失败与结果未知都以对话框给出可读原因。结果未知时保留待查询计划并锁定编辑，查询确认成功后进入同一完成状态，同样不显示提示。

主机名编辑采用相同的单步应用与执行进度。确认成功后不在页内显示完成卡片或成功文案，也不把计划 ID 放进页内操作详情，只用远程确认的 revision 恢复编辑；页面不提供专用回滚按钮，恢复原名称作为普通修改提交。即时生效时更新当前名称；需主机重启时保留当前名称、更新待生效名称，并明确提示重启后生效。未知结果保持编辑锁定，查询确认后进入完成状态。

名称输入显示当前字符数及远程提供的长度上限（当前 Windows 为 15、Linux 为 63）；超长时明确提示实际长度与上限，其他语法错误使用格式提示。

### 使用记忆（设备本地）

“设置 → 系统 → 使用记忆”可清除当前服务器下当前账户的交互默认值。桌面客户端在当前操作系统用户的 LocalApplicationData/RelaxKonOS/usage-memory.json 保存最后一次成功提权的管理员用户名和各用途最后确认的选择目录，重启后恢复。密码和提权令牌不进入此存储，记忆不跨设备同步。

管理员用户名按稳定 ServiceId 和用户 ID 隔离，通用宿主提权、系统设置、SMB 与文件操作共享。文件选择目录再按应用用途及打开/保存/选择文件夹区分；本地目录和远程目录分开，远程目录另外按工作区隔离。临时 SSH 隧道 HTTP 地址不作为保存键。

明确指定的初始目录优先于记忆。文件选择记住父目录，文件夹选择记住所选目录，保存文件记住目标目录。取消选择、浏览目录及失败提权不覆盖成功记录。失效或无权访问的记忆目录回到默认位置；自动恢复远程目录不触发提权。清除记忆、切换账户、服务器或会话后，旧交互不再写入记录。未登录时选择器仍可使用，但不保存账户记忆。

## 平台入口与简易宿主编辑（2026-10-05）

时区和主机名均使用“编辑 → 应用 → 结果”流程；点击应用后自动准备不可变计划、按需授权并提交。普通编辑不发起写请求，取消授权保留草稿。结果未知时保留计划，禁用编辑、重置和重新读取，通过查询确认结果；不重复生成写操作。主机名与时区都不设页内操作详情，两者确认成功后都不在页内显示完成提示（时区由「当前时区」行体现，主机名由名称行与重启提示体现），恢复按普通修改提交，失败只以对话框提示。Windows 改名后显示待重启，Linux 按远程有效状态显示结果。

SDK 和终端环境入口只负责导航；授权仍由设置宿主与领域服务决定，不将 JWT、密码或 Helper transport 暴露给应用。Workspace 环境覆盖和 DNS 安全恢复仍按设计文档的后续顺序推进。

## Workspace 环境服务（存储已实现，消费未接入）

`WorkspaceEnvironmentService` 与 `/api/v1.0/workspaces/{id}/environment` 提供独立读取与条件批次更新；归属沿用 Workspace 授权，返回禁止缓存，版本缺失/冲突分别为 428/409。Set 空字符串与 Delete 撤销覆盖分开处理，PATH 模式显式保存；高影响确认、单批次与全快照限制均由服务端验证。

数据来源为 `data/workspace-environment/environment.db`，每次成功写入在 SQLite 事务提交后返回；文档通过宿主 Data Protection 加密并绑定用户与 Workspace。损坏或无法解密时保留数据并返回错误。宿主配置与外观偏好不受影响。对应服务端检查已验证重启、并行写入冲突、数据隔离、密文与损坏保留，以及生产 HTTP 路由的归属、禁止缓存和错误契约。

当前已有系统页的 Workspace 环境编辑器和连接绑定客户端服务；实时同步和终端消费链尚未接入；读取结果中的展开值仅为 Workspace 覆盖预览，不能代表主机加工作负载的最终环境。下一步需在普通用户进程创建边界读取最新快照，并确保 Helper、管理进程和管理员终端完全不消费这些覆盖。

系统 → Workspace 环境变量提供独立编辑器：暂存变量、撤销覆盖、选择 PATH 模式后统一应用；高影响变更需确认，错误保留草稿，未知结果禁止重复提交。重新读取明确丢弃本地草稿，成功读取后恢复编辑。中文、英文、日文文案和搜索入口已接入。当前只保存覆盖配置，界面明确说明终端还不使用这些值。

工作负载纯构造器已验证平台名称比较、Windows 用户 PATH 合并、Workspace Append/Replace、空值与字面数据、来源隔离及循环展开拒绝。它尚未被 PTY 调用，不能据此宣称终端环境已生效。
