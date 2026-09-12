# RelaxKonOS 设置系统与设置应用升级（Goal 执行版）

> 日期：2026-09-07。状态：执行中，G0 基线已记录，G1 平台底座实施中。未有证据的条目仍是目标契约，不代表已完成。
> 本轮执行实现与验证；仅在明确指定的远程测试主机/隔离 VM 上执行真实主机配置变更。当前未提供该测试目标，不使用开发机代替。

## 1. 目标与决策

把设置建设为 RelaxKonOS 的平台能力：提供可发现、可读写、可授权、可订阅、可恢复的配置服务，设置应用负责其中一个交互入口。参考 Windows 的分类导航、搜索、详情页、关联入口和定向打开设置页的方式，结合远程主机管理的实际需要设计。

必须支持直接修改远程宿主的受支持配置，包括环境变量、时区、主机名与 DNS；需特权的请求通过现有 PrivilegedHelper。不得因为旧文档把设置限定为桌面偏好而省略这些功能。目标不是让用户复制命令到终端才能完成常规设置。

本决策替换“Settings 不触及宿主配置”“环境变量操作一律禁止”的旧限制。保留非特权 Server、宿主身份认证、应用权限隔离、封闭 Helper 操作集、审计与本地渲染。允许结构化的环境配置数据，不允许把这些数据作为 Helper 自身或其管理子进程的启动环境注入。

遵守根目录 `AGENTS.md`：首个正式版本之前直接升级内置接口，同时修改仓库内调用者、测试、示例及文档；不增加旧路由别名、双格式解析或兼容适配层。

## 2. 已核实基线与缺口

| 代码/文档 | 当前状态 | 本 Goal 的改动 |
| --- | --- | --- |
| `Client/RelaxKonOS.Client/Apps/Settings/ViewModels/SettingsViewModel.cs` | 实际有系统、个性化、时间语言、网络、应用、镜像源、默认应用、开发者八页；整体偏好防抖保存，保存失败被吞掉 | 注册式导航、领域服务、明确保存状态、冲突处理 |
| `Client/RelaxKonOS.Client/Apps/Settings/Views/SettingsView.axaml` | 固定 220px 导航与页面模板，无统一搜索和面包屑 | 响应式导航、搜索、详情页及一致卡片 |
| `Client/RelaxKonOS.Client/Services/SettingsNavigationService.cs` | SDK 入口仅提供打开应用页；Settings VM 另有个性化与应用权限定位 | 统一类型化路由与参数校验 |
| `ShellSettings`、Workspace preferences | 桌面运行态与跨设备偏好 | 保留职责，由独立设置服务驱动，消除 Settings 窗口生命周期依赖 |
| `docs/development/RelaxKonOS.AppSettings.md` | 按用户、Workspace、设备隔离应用私有 JSON，已有 revision | 继续用于应用私有偏好，不用来存宿主配置 |
| `Shared/RelaxKonOS.Protocol/Privileged/` | 封闭操作与短期 capability 授权；注释仍禁止 environment operation | 新增专用环境、时区、主机名与 DNS 操作、授权目标与结果 |
| `RelaxKonOS.PrivilegedHelper` | Linux one-shot，Windows 认证命名管道、服务与开发控制台 | 扩展现有分派器及平台实现，不新建万能管理员执行器 |
| `docs/desktop/RelaxKonOS.Settings.md` | 仍按五页介绍，并声明宿主只读 | 作为历史实现说明，实施阶段整体更新 |
| `docs/platform/RelaxKonOS.PrivilegedOperations.Goal.md` | 部分当前状态描述已落后于代码 | 复核现状，保留安全模型并纳入本次允许的结构化操作 |

执行开始时记录 commit、工作区改动、测试项目与实际服务注册位置。代码事实优先于旧文档的“已实现/待实现”标签；不得重写已完成的 Helper transport。

## 3. 设置范围与真源

每个设置在 UI 与协议中明确目标、作用域、来源、权限、持久化与生效时间。页面顶部持续展示当前远程主机与连接身份；本机配置标注“此客户端设备”。切换连接必须清空旧主机草稿与授权引用。

| 范围 | 真源与身份 | 例子 | 写入/生效 |
| --- | --- | --- | --- |
| ClientDevice | 当前客户端设备的既有受管本地存储 | 窗口布局、客户端辅助功能、开发模式 | 本机运行态；不能冒充宿主设置 |
| Workspace | 服务端当前用户拥有的 Workspace | 主题、壁纸、语言、默认应用、Workspace 环境覆盖 | 服务端保存，通知所有连接设备 |
| AppPrivate | AppSettings 现有隔离键 | 编辑器、终端显示偏好 | 应用服务负责解释 |
| HostUser | 认证身份映射到远程 UID/SID 的宿主配置 | 用户环境变量 | 在正确用户身份下写入；不可误写 Server 服务账户 |
| HostMachine | 远程 OS 的真实配置 | 机器环境、主机名、时区、DNS | 宿主 provider；受保护写入走 Helper |

HostMachine 不归某个 Workspace 所有；读写权限由宿主策略和认证决定。设置目录只返回调用者可获知的描述与能力状态，不泄漏其他用户变量或秘密。持久状态以 provider 读回的 OS 配置为准；数据库只保存操作记录、受保护恢复材料、版本元数据，不能把 JSON 写成功当作系统修改成功。

## 4. 产品结构与 UI 验收

| 一级导航 | 子页与主要能力 | 第一轮范围 |
| --- | --- | --- |
| 首页 | 主机/Workspace 卡片、常用入口、搜索、待生效操作、Helper 状态 | 必做；不加入广告或无数据推荐 |
| 系统 | 关于、主机名、环境变量、存储概览、服务与恢复入口 | 环境、主机名可写；存储复用既有读取能力 |
| 网络与 Internet | 网卡详情、地址、DNS、连接诊断、代理、防火墙 | DNS 可写；代理与防火墙复用现有领域服务 |
| 个性化 | 主题、壁纸、Shell、桌面表现 | 保留已有完整功能并统一布局 |
| 应用 | 已安装应用、权限、默认应用、镜像源、应用设置入口 | 保留能力，避免全部堆在一个长页面 |
| 账户与权限 | 当前宿主身份、会话、应用授权、特权助手状态 | 复用认证；不另造账号或密码库 |
| 时间和语言 | 显示格式、语言、区域、远程时区、时间同步状态 | 时区可写；手动改系统时钟不是本轮要求 |
| 辅助功能 | 客户端字号、动画、对比度、键盘可达性 | 有真实绑定效果；不假装控制服务器显示器 |
| 开发者 | 环境变量关联入口、开发模式、包工具、诊断 | 环境页单一实例/路由；保留已有开发工具 |

UI 要求：

- 宽窗口使用侧栏、内容标题、面包屑与分组卡片；窄窗口折叠导航。验证 640×480、1024×768、1440×900 及 200% 缩放，无关键按钮被裁切、无全页横向滚动。
- 统一图标与现有主题资源，不以 emoji 作为核心导航图标；亮/暗主题、焦点、高对比度、屏幕阅读名称和键盘顺序完整。中文、英文、日文文案同步。
- 每项提供标题、说明、当前值、范围、生效提示与状态。详情页有相关设置和返回入口；搜索支持标题、关键词、同义词（例如 PATH/路径/环境变量），结果显示分类路径、范围与不可用原因。
- 目录加载不阻塞本地搜索；普通已缓存搜索目标为 150ms 内呈现，基于至少 200 个设置项测量。网络探测异步更新，不让首页串行等待所有 provider。
- 偏好可即时预览并防抖保存，显示“保存中/已保存/失败可重试”；失败保留草稿但不伪装为已保存。离开有未提交页面给出保留/放弃选择。
- 宿主修改采用编辑草稿、预览差异、应用、结果读回；不对每次键入发起特权操作。有效短期授权范围内复用认证，高影响修改仍展示准确变更内容。
- 区分未登录、离线、加载失败、无权限、需要提权、Helper 不可用、平台不支持、外部策略锁定；不得统一变成灰按钮。离线宿主写入不排队自动重放。

## 5. 独立平台能力

目标依赖关系：

```text
Settings UI / Shell 快捷入口 / 内置应用 / 授权 SDK / DevCli
    → 类型化客户端服务与设置目录
    → Server 授权 + 领域服务 + 操作协调器
    → Workspace/AppSettings 存储 或 宿主平台 provider
    → 需要特权时复用 Privileged transport → Helper 封闭操作
```

新增职责（名称为设计建议，实施时一次性落实）：

- `SettingsCatalog`：稳定 settingId、分类、资源键、路由、作用域、值类型、可读写状态、能力原因与生效方式。目录提供发现，不接受任意路径或任意对象写入。
- `IWorkspaceSettingsService`：偏好读取、变更、revision 与同步；从 SettingsViewModel 移出保存与默认应用传播。
- `IHostEnvironmentService`、`IHostTimeService`、`IHostIdentitySettingsService`、`IHostNetworkSettingsService`：强类型领域契约，独立于 Avalonia、页面 VM 和 Shell。
- `SettingsOperationCoordinator`：预览、授权校验、并发控制、持久操作状态、恢复与通知。领域提供者负责真实读写；不得把所有领域逻辑塞进协调器。
- `SettingsNavigationService`：使用既有激活体系扩展 `relaxkonos://settings/...`，支持目录定位和 settingId 聚焦；保留语义仍有效的已有路径。若接口替换则同步所有调用者，不留兼容别名。
- 外置应用经 SDK 请求粒度化 capability，宿主绑定 appId、用户、目标主机与 scope，不暴露 JWT 或 Helper IPC。打开设置页不授予写权限。

至少接入三个非设置窗口入口：Shell 快捷项、终端“环境变量”入口、DevCli 的读取/预览/应用/查询。关闭设置应用后调用 API 仍可写入，Shell 与其他客户端仍能收到变化。高权限自动化沿用认证与目标范围；无交互且缺少授权时返回结构化错误，不启动密码对话框或降级绕过。

## 6. 协议、并发和操作状态

在 `Shared/RelaxKonOS.Protocol` 定义 DTO、枚举和路由常量；沿用当前 API 前缀。以下为拟定新路由，不代表已有实现：

| 路由（相对于 `/api/v1.0`） | 语义 |
| --- | --- |
| `GET /settings/catalog` | 当前连接可见目录、能力和平台原因 |
| `GET /host-settings/environment?scope=hostUser|hostMachine` | 当前绑定目标的环境快照 |
| `POST /host-settings/environment/preview` | 强类型环境变更预览 |
| `POST /host-settings/environment/apply` | 根据预览应用环境变更 |
| `GET /host-settings/time`、`identity`、`network` | 各领域快照；network 支持按网卡 ID 查询 |
| `POST /host-settings/{domain}/preview`、`apply` | domain 仅注册 time、identity、network，各自 DTO/验证器 |
| `GET /settings/operations/{id}` | 有权限的调用者查询结果、恢复与待生效状态 |
| `POST /settings/operations/{id}/confirm` | 在期限内确认网络连接仍可用 |
| `POST /settings/operations/{id}/rollback` | 授权后回退该操作可恢复的状态 |

Workspace 环境覆盖与偏好属于 Workspace 授权路径，不接受借用 host scope 绕过归属检查。AppSettings 继续使用现有协议。不得复制同一宿主能力到每个应用的专有路由。

快照包括 `revision`、`observedAt`、`scope`、`target`、`capabilityState`、`effectiveState`；target 由服务端身份解析，客户端只允许选择已获权的目标。环境快照区分原始值、展开预览、来源、敏感状态，不自动把所有原始值广播出去。

写请求包含 expectedRevision、幂等键和强类型 change set。预览返回短期 planId、脱敏差异、影响、需授权能力和期限；plan 绑定 actor、目标、变更摘要及基线 revision。应用不得用新载荷替换已确认计划。应用前重新检查授权、外部变更和能力状态；冲突返回 409，缺失必要前置条件返回 428。

服务端状态：`Prepared → Applying → Applied | Failed | PartiallyApplied | Unknown`；需连接确认时为 `AwaitingConfirmation → Applied | RolledBack | RecoveryRequired`。生效时间另用 `Immediate | NewProcess | NewLogin | ServiceRestart | HostRestart`，避免“已持久化”与“运行态生效”混淆。

同幂等键同载荷返回既有操作；异载荷冲突。Helper 现有重复 ID 拒绝语义不等同 HTTP 幂等：由 Server 持久操作日志承接重试；不在断连后盲目生成新 Helper ID 重做写入。未知结果先查日志并读回资源，无法确认则进入恢复状态。

按资源串行写入；revision 必须覆盖宿主外部变更（例如规范化快照摘要/平台版本），不能只增长数据库计数。跨多个 OS 资源无法承诺 ACID，应保存步骤结果与补偿状态。回滚也检查新 revision，避免覆盖另一管理员后续修改。

变化通知只包含 settingId、scope、资源标识和 revision，通过现有实时通道扩展；接收端按权限重新读取。重连先重取快照，禁止用旧缓存覆盖新状态。

## 7. 环境变量：必须完成的纵向切片

### 7.1 交互与共同语义

提供 Workspace、远程当前用户、远程机器三个明确分区；新增、编辑、删除、筛选、PATH 分项增删与排序、原始/展开预览、重复与不存在路径提示。区分删除操作与空字符串；不能把空值静默转换为删除。

记录变量来源与覆盖关系。普通变量对 RelaxKonOS 创建的非特权用户工作负载按 HostMachine → HostUser → Workspace → 已授权的单次工作负载覆盖构建；不得直接复制 Server 服务进程环境。PATH 使用平台环境构建规则与显式覆盖/追加模式，展示最终顺序；不要把 Windows 用户 PATH 简化为覆盖系统 PATH。

Windows 名称大小写不敏感，Linux 大小写敏感；PATH 分隔符分别为 `;` 与 `:`。不自动删掉空路径项、重排或大小写归一化；对当前目录搜索等危险语义提示确认。变量名、值、批次数量与总请求大小有明确上限并在两端校验；拒绝 NUL、非法名称和 provider 无法无损表达的数据。

展开预览有限深度，检测循环引用；原始字符串不经 shell 求值。`$(...)`、反引号、分号、引号和换行按数据校验与转义，绝不拼入 shell/PowerShell 程序。

读取变量本身也需要权限。敏感值默认掩码，单独授权显示；搜索、日志、通知、导出及预览不包含秘密值。恢复材料必须限制访问并按保留期清理，不把完整环境保存为普通审计日志。

### 7.2 Windows provider

用户范围明确绑定认证用户 SID，并正确访问对应用户配置单元；LocalSystem 的 HKCU 不是目标用户。机器范围使用固定系统环境存储位置，客户端不能提交注册表路径；保留字符串类型及可展开字符串语义，写后重新读取。

发送适当环境变化通知，但 UI 明示不会重写已运行进程的环境。新建 Terminal、任务及受管非特权工作负载必须获取新快照构造环境；Windows 服务与其他登录会话可能需要独立重启/重新登录。不得为使变量生效自动重启整个服务器。

### 7.3 Linux provider

Linux 不存在覆盖所有 shell、PAM、systemd 服务的统一用户环境存储。第一轮明确支持：Ubuntu 的机器 `/etc/environment` 受限无 shell 语法，以及 RelaxKonOS 启动器使用的用户/Workspace 环境；用户持久范围标注“RelaxKonOS 启动的进程”，不宣传为所有宿主登录程序生效。

执行阶段核对目标发行版真实加载机制；读到不能无损编辑的语法时给出具体错误并保持文件不变。保留无关条目和文件元数据，采用受限解析、受保护临时文件、原子替换与读回；不向 `.bashrc`、`.profile` 批量追加脚本。未来增加 PAM/environment.d provider 必须单独声明支持的消费者与优先级。

### 7.4 提权边界

新增专用 EnvironmentRead/EnvironmentApply（最终命名与枚举统一）及 host-user/host-machine capability；Helper 再验证身份绑定、scope、变量名、长度、变更数、revision 和目标文件/注册表键。不得借 FileWrite 任意写环境文件代替领域校验。

允许用户编辑 PATH、JAVA_HOME 等以及经明确高影响确认的加载器/运行时变量。机器范围变更可影响其他进程，应按管理员操作处理。Helper 与所有特权子进程始终使用由安装策略控制的干净环境和可信绝对可执行路径，不能继承被编辑的 PATH、LD_PRELOAD、运行时注入变量或 Workspace 环境。需要重启受管服务时另行走该服务能力与确认。

## 8. 其他宿主能力与恢复

| 能力 | Windows / Linux 第一轮 | 关键约束 |
| --- | --- | --- |
| 时区 | 两平台枚举与设置本机有效时区 ID | ID 由远程系统列举；不把 IANA/Windows ID 混用；显示格式和宿主时区分开 |
| 主机名 | 两平台读取与设置 | 平台校验、影响预览、读回；域加入或组织策略限制给出具体原因，支持待重启状态 |
| DNS | Windows 网卡；Ubuntu NetworkManager 或 systemd-resolved/networkd 的明确可写组合 | 执行时探测实际 owner，只对声明支持且经过测试的 provider 开放写入；不可直接覆盖被托管的 resolv.conf |
| 代理/防火墙 | 复用现有领域能力与平台支持矩阵 | 一个真源；不在设置应用复制规则引擎或绕过 Proxy/Firewall 授权 |
| 服务、存储、恢复 | 接入已支持的管理/诊断与关联入口 | 不凭空增加磁盘格式化、任意服务执行等功能 |

DNS 编辑支持网卡选择、自动/手动、IPv4/IPv6 地址与顺序，展示变更影响。可能断开当前连接的操作必须在应用前建立宿主侧持久恢复任务；推荐 60 秒确认期限，可在计划中明确实际值。恢复任务不能依赖 Client、Server 请求线程或 Linux one-shot Helper 继续存活；复用或实现固定动作的 OS 调度恢复机制。

客户端从新连接确认后取消恢复；超时自动恢复旧配置。主机重启后读取恢复日志并处理未完成计划。若部署无法提供独立恢复能力，DNS 写入标为暂不可用并说明前置条件，不悄悄取消恢复要求。主机名等不保证完全自动恢复的操作必须明确手动恢复路径和待生效条件。

## 9. 实施里程碑

以下全部为本 Goal 必做；平台明确不适用不算失败，但不能把未实现填成“不支持”来完成阶段。

| 阶段 | 工作与主要落点 | 完成证据 |
| --- | --- | --- |
| G0 基线与契约 | 核对 Settings、Runtime/SDK、Server Privileged、部署和测试；固定作用域、身份映射、支持矩阵及错误码 | 代码清单、冲突文档修订、具体 provider 方案、逐项验收表 |
| G1 平台底座 | Protocol 目录/路由/DTO；Server 领域接口、权限、revision、操作日志、通知；提取 Workspace 服务 | 不创建窗口也能读写偏好；两客户端同步；过期 revision 与越权被拒 |
| G2 UI 框架 | Settings 导航、搜索、卡片、面包屑、响应式与状态组件；迁移现有八页能力 | 三语言、亮暗主题、键盘和尺寸截图；原有入口无回归 |
| G3 环境变量 | 两平台 provider、Helper capability、PATH 编辑器、终端/工作负载环境构建 | 用户与机器隔离、持久化读回、新进程生效、特权环境隔离的集成证据 |
| G4 宿主扩展 | 时区、主机名、DNS 与独立超时恢复；关联代理、防火墙、诊断 | Windows 与 Ubuntu 实机/隔离 VM 用例，断网及重启恢复证据 |
| G5 全系统接入 | Shell、Terminal、SDK、DevCli 共用服务；关闭 Settings 仍工作；修订旧接口调用者 | 至少三个非窗口入口、自动化无 UI 测试、manifest/capability 一致 |
| G6 收尾验收 | 运行相关构建/测试、UI 检查、部署文档与所有冲突清理 | 完整验收矩阵、已知平台限制、变更清单，无伪成功/占位写入 |

G1 后可进行 G2；G3 依赖 G1，G4 依赖操作恢复底座；G5/G6 集成所有阶段。每阶段更新本文执行记录，包含变更、验证命令、结果、尚未解决的问题和下一步。不要只留下设计或 UI mock 就标记完成。

## 10. 验收矩阵与完成定义

| 场景 | 必须观测到的结果 |
| --- | --- |
| 设置应用从未打开，API 修改主题/默认程序 | Shell、文件关联及另一设备刷新 |
| 保存失败、断网、切换用户/服务器 | 草稿状态真实；不向错误主机或新用户重放写入 |
| 两个客户端和宿主外部工具同时编辑 | 冲突被检测；无整份偏好或 PATH 静默覆盖 |
| 普通用户尝试机器变量；有授权后重试 | 首次拒绝/要求提权，授权后真实持久化；Helper 缺失时可解释 |
| Windows Helper 以 LocalSystem 运行 | 用户变量写入目标 SID，服务账户与其他用户不受误写 |
| 环境值含引号、特殊字符、空值、循环引用 | 无命令执行；删除与空值明确；不支持值无部分写入 |
| 改 PATH/运行时加载变量后触发特权操作 | Helper 与管理子进程仍使用可信路径和干净环境 |
| 修改环境后新开终端，旧终端仍运行 | 新进程按声明规则取新值，旧进程不被伪称已更新 |
| HTTP 重试、Helper 断连、Server 重启 | 操作可查询；不盲重放，不把 Unknown 当 Success |
| DNS 修改导致断连且无确认 | 独立恢复任务按期限回退，Server/Client 退出也有效 |
| 宿主策略锁定、未知网络后端 | 原因具体；无假开关或无效“已保存” |
| app capability 拒绝、跨用户/Workspace 请求 | 所有入口一致拒绝，导航入口不能越权 |
| 查看日志、事件、搜索索引和导出 | 不含密码、JWT、环境秘密值或恢复快照明文 |

验证包括 Protocol/领域单测、Server 授权与幂等集成测试、Helper 输入与恢复测试、Windows 服务身份及 Ubuntu 真正 provider 测试，以及 Avalonia 人工/自动 UI 检查。先发现现有测试组织再添加有行为价值的用例；不只测 DTO 与自身实现镜像。

可从 `dotnet build RelaxKonOS.sln`（先核实解决方案文件名）及 `dotnet test RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj`、`dotnet test Framework/RelaxKonOS.Core.Tests/RelaxKonOS.Core.Tests.csproj` 开始，按实际改动补充项目。实机修改仅在明确指定的测试主机/隔离 VM 执行并记录原值和恢复结果；缺少环境时如实标记未验证，不能宣布全量完成。

完成条件：G0–G6 证据齐全；设置服务可脱离窗口运行；必做宿主能力真实可用；特权路径完整；相关调用者/示例/协议同步升级；未解决阻塞、测试失败及平台缺口均不被“UI 完成”掩盖。

## 11. 文档冲突清理清单

- `docs/desktop/RelaxKonOS.Settings.md`：替换仅五页、宿主只读、吞保存错误等旧设计，记录新导航与服务分工。
- `docs/platform/RelaxKonOS.PrivilegedOperations.Goal.md`：将笼统环境禁令改为禁止特权执行环境注入，纳入封闭环境操作；更新过期代码基线。
- `Shared/RelaxKonOS.Protocol/Privileged/PrivilegedOperationContracts.cs`：实施新增操作时同步修改禁止 environment operation 的注释与相关拒绝测试，保留禁止通用执行字段。
- `docs/platform/RelaxKonOS.PrivilegedOperations.Operations.md`、`RelaxKonOS.PrivilegedHelper/README.md` 及英文版本：补安装条件、capability、环境隔离、恢复任务与诊断。
- `docs/architecture/RelaxKonOS.Protocol.md`、`docs/platform/RelaxKonOS.Storage.md`、`docs/development/RelaxKonOS.AppSettings.md`：补新契约、操作日志与真源边界，不把 OS 状态塞进 AppSettings。
- `docs/platform/RelaxKonOS.Security.md`、`docs/platform/RelaxKonOS.PermissionModel.Refactor.md` 及应用权限文档：同步设置能力、目标绑定和敏感值读取授权；不放宽其他领域边界。
- `docs/applications/RelaxKonOS.Terminal.md`、相关工作负载文档、DevCli README、SDK 示例与 manifest：同步环境生效语义及新能力调用方式。

## 12. 可直接用于 Goal 模式的指令

```text
依据 docs/desktop/RelaxKonOS.SettingsSystem.Goal.md 完成 RelaxKonOS 设置系统与设置应用升级，逐阶段执行 G0–G6，并维护该文件的执行记录与验收证据。
目标是独立于设置窗口的系统配置服务、参考 Windows 的设置体验，以及真实可写的远程环境变量、时区、主机名和受支持的 DNS。复用已有特权助手，Server 保持非特权；明确区分客户端、Workspace、远程用户与远程机器。
本需求已明确替换旧文档“Settings 不触及宿主配置”和“环境变量操作一律禁止”的限制；保留封闭操作、认证、权限、审计和恢复要求。同步修订冲突文档及仓库内调用者，不增加兼容别名或双格式解析。
持续完成实现、相关测试和 UI 验证，不停在规划、mock 或只读页面。不要创建新的 Goal 或预算，除非当前模式的用户指令要求。没有测试主机时继续可独立完成的工作，准确记录尚未验证的跨平台项；不要用开发机替代远程测试目标进行系统配置实验。
每阶段给出简明进度；最终报告具体变更、验证证据、剩余限制。所有必做验收完成后才能标记目标完成。
```

## 13. 参考依据

Windows 只作为信息架构与交互依据，RelaxKonOS 的路由、权限与跨平台 provider 以本文设计为准。

- [Microsoft：探索 Windows 设置](https://support.microsoft.com/en-us/windows/experience/exploring-windows-settings)：分类与设置发现。
- [Microsoft：启动特定 Windows 设置页](https://learn.microsoft.com/zh-cn/windows/apps/develop/launch/launch-settings)：页面深链接和平台可用性思想。
- [Microsoft：Windows 环境变量](https://learn.microsoft.com/en-us/windows/win32/procthread/environment-variables)：用户/系统环境、子进程继承与变更通知。
- [Microsoft：PowerShell 环境变量](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_environment_variables?view=powershell-7.5)：作用域与跨平台大小写差异。

## 14. 执行记录

| 日期 | 阶段 | 结果 | 验证与剩余项 |
| --- | --- | --- | --- |
| 2026-09-07 | 文档基线 | 已核对八页 Settings、AppSettings、特权协议和 Helper 文档，形成此执行方案 | 仅文档交付；G0–G6 实现及运行验证未开始 |

### 2026-09-07 / G0 执行基线

- 起始 commit：`64ce9194bdd5693237464eec66a3668c3f02fbff`；`git status --short` 为空，起始工作区干净。根 AGENTS.md 仅规定直接升级接口、不保留兼容层。
- 解决方案：`RelaxKonOS.sln`。Server 测试是 `OutputType=Exe` 的行为验证程序，必须 `dotnet run --project RelaxKonOS.Server.Tests`，不能把 `dotnet test` 的无测试退出当作通过；另有 `Framework/RelaxKonOS.Core.Tests`。
- Client 注册点：`Client/RelaxKonOS.Client/Services/Bootstrapper.cs`。既有 `PreferencesSync` 在登录后加载且无需打开 Settings，但写入分散在窗口、Shell、Explorer、SDK 壁纸及编码偏好调用者。底层真源实际为 `Workspace\Desktop` 注册表键，不再是 Workspace JSON 列。
- Server 注册点：`RelaxKonOS.Server/Program.cs`；偏好路由 `Endpoints/WorkspaceEndpoints.cs`；注册表 `Registry/CachedSqliteRegistryRepository.cs` 当前为内存真源、5 秒延迟落盘。宿主操作日志不能复用这一延迟写入来保证执行前持久化。
- Helper 基线：`RelaxKonOS.PrivilegedHelper/Program.cs` 的 `PrivilegedOperationExecutor`；Linux `LocalPrivilegedOperationRunner`、Windows `WindowsNamedPipePrivilegedOperationTransport` 已存在，不重写传输。`HostElevationSessionStore` 现有授权绑定 sub/jti/capability/target，默认五分钟。
- 身份：`Domain/User.PlatformIdentity` 已存在；宿主用户目标将从认证 sub 解析服务端 User，再核验平台 UID/SID，不接受请求指定用户或 HKCU。JWT 中的 RelaxKonOS 用户 GUID 不是宿主 SID/UID。

| provider | 实施方案 | 当前验收状态 |
| --- | --- | --- |
| Windows 环境 | Helper 固定 HKLM 环境键 / 目标 SID 下 Environment；保留 REG_SZ/REG_EXPAND_SZ、读回及广播 | 待实现，待指定远程 Windows 测试目标 |
| Ubuntu 环境 | 固定 `/etc/environment` 受限保真解析、原子替换；用户范围仅 RelaxKonOS 启动器 | 待实现，待指定 Ubuntu VM |
| 时区 / 主机名 | 平台枚举合法时区 ID；固定 OS API/绝对程序；主机名校验、策略与待重启结果 | 待实现及远程测试 |
| DNS | 探测 Windows 网卡 / Ubuntu 实际网络 owner；固定动作 OS 持久恢复任务先落盘，再写 DNS | 待实现；不能因尚未实现就声明平台不支持 |
| 错误 | 428 缺少 revision/授权前置；409 外部修改或幂等载荷冲突；能力原因独立区分离线、权限、Helper、平台、策略 | Workspace revision 已落地；宿主能力待实现 |

### 2026-09-07 / G1 第一批实现（阶段未完成）

- 新增 Server `Settings/IWorkspaceSettingsService` 与 `WorkspaceSettingsService`、独立偏好校验器。读取不再静默销毁损坏的数据。注册表三个实现新增原子 CompareExchange；偏好 GET 返回 revision，PUT 强制提交观测 revision，旧版本返回 409，缺失返回 428。壁纸上传更新引用同样检查竞争，不吞并发覆盖。
- Client 原 `Apps/Settings/ISettingsClient` / `SettingsClient` 直接替换为 `Services/WorkspaceSettings/IWorkspaceSettingsService` / `WorkspaceSettingsService`，仓库内 Shell、Explorer、SDK、编码、URI 路由和同步调用者一并迁移；没有旧接口别名。
- 新增窗口外的 `WorkspacePreferencesEditor`：冻结草稿与连接目标、防抖保存、连接变化清理、保留失败草稿、关闭窗口继续保存。Settings VM 仅调用服务；三语言展示保存中、服务端已接收/等待落盘、失败重试、冲突、离线。
- 已新增 `SettingsSystemVerification`：三个存储实现的 stale revision 拒绝、32 个并发写者仅一个成功、用户隔离、损坏数据保留、缓存落盘重启后 revision/值一致。提供 `--settings-only` 精确运行入口。测试结果更新如下。
- **仍需实现**：目录/强类型宿主契约、实时跨客户端同步、偏好冲突合并/放弃 UI、持久化完成状态、宿主操作协调器、完整 G2–G6。此记录不表示 G1 验收通过。

| 验证命令 | 当前结果 |
| --- | --- |
| `dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj --no-restore -v quiet` | 通过，0 warning / 0 error |
| `dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -v quiet` | Debug 失败：原 App.axaml.cs 的 AttachDeveloperTools 引用不可用；待修复依赖恢复 |
| `dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -c Release -p:UsedAvaloniaProducts= -v quiet` | 通过，0 warning / 0 error；关闭构建统计以避免向 sandbox 外的 Avalonia telemetry 目录写入，未跳过 C#/XAML 编译 |
| `dotnet run --project RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -- --settings-only` | 通过（退出 0）。恢复本地依赖后，HTTP 428/409、跨用户读写拒绝、注册表绕过拒绝、三个存储实现及 SQLite 重启用例全部通过。仍有 NuGet 源不可达 NU1801 警告 |
| UI 截图 / 640×480、1024×768、1440×900、200% / 两设备 / 宿主写入与恢复 | 待测试；未运行开发机系统配置实验 |

- G1 补充：注册表 PUT 契约增加必传 `expectedRevision`（创建用 0），两个编辑器调用者同步升级；受管 Desktop JSON 走相同校验；拒绝删除受管默认偏好及其祖先键，避免删除/重建重置版本后接受旧草稿。默认注册表键创建改为 insert-only CompareExchange，避免并发初始读取覆盖已保存值。
- HTTP 行为验证使用临时 loopback Kestrel 和测试身份运行实际生产路由，数据仅在临时目录；验证 428、409、跨用户 GET/PUT、注册表 stale PUT 与受管删除拒绝。此测试不替代真实 JWT 认证、两设备 UI、宿主 provider 或恢复验收。
- 下一批顺序：先补偏好持久状态/实时变化通知与断线重取、客户端草稿冲突处理及服务行为测试；再完成目录与宿主强类型协议、持久操作协调器，并推进 G2–G6。未将任何阶段或总 Goal 标记完成。

### 2026-09-08 / G1 持久化状态与会话隔离（阶段未完成）

- 本次继续执行起点 commit：`05481ba2da44c0fb6f7318e374fa02899dbed58b`；已有用户改动为删除 `.idea/.idea.RelaxKonOS/.idea/.name`，保持不动。
- 偏好协议增加服务端观测的 `persistedRevision`，由注册表 `AppliedRevision` 填充；客户端提交的持久版本不作为真源，也不进入偏好 JSON。修正 DTO 中仍声称 Workspace JSON 列是真源的注释。
- 窗口外草稿服务在接收成功后最多进行 12 次间隔一秒的只读确认；仅对应 revision 确实落盘才显示三语言“已保存”。读取失败、超时或版本被其他写者替换时保留“服务端已接收/等待落盘”，不伪报成功、不自动重写。新编辑和会话切换取消旧确认。
- `PreferencesSync` 增加返回结果的 token 身份检查，登出清除加载去重标识，读取失败允许下次重新尝试。完整实时事件、重连刷新与壁纸异步应用期间会话切换仍待后续完成。
- 验证：`dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj --no-restore -v quiet` 通过，0 warning / 0 error；Client Release 首次并行构建退出 1 且无诊断，改用 `dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -c Release -p:UsedAvaloniaProducts= -v normal -m:1` 通过，0 warning / 0 error（编译服务器管道权限不足后回退编译成功）；`git diff --check` 通过。
- **未测试**：本批行为测试、持久化失败/竞争测试、登录切换测试、UI 人工验证；按用户允许暂缓，不能引用前批测试作为本批通过证据。远程 Windows/Ubuntu 目标未提供，未在开发机做系统配置实验。
- 下一步：继续 G1 实时变化、冲突处理、目录与宿主协调器，再推进 G2–G6；G0–G6 的剩余必做验收均未宣称完成。

### 2026-09-08 / G1 跨连接同步与草稿恢复（阶段未完成）

- 代码核对纠正：此前只有 Workspace Hub 协议声明，没有 Server 实现或路由。新增 `/hubs/settings-changes`，复用 SignalR/JWT 注册；`Subscribe` 按认证 sub 校验 Workspace 所有权，连接到期关闭。后台按订阅资源分组，每秒观察真源版本及持久版本，覆盖偏好 API、壁纸和注册表写入者；只推送 Workspace 标识与版本。
- Client `SettingsChangesStream` 生命周期由单例 `PreferencesSync` 管理，独立于 Settings 窗口；首次连接/重连先订阅再重读，30 秒只读恢复检查并触发令牌续期，不重放写入。连接更换取消旧订阅；刷新串行并在 UI 线程应用，草稿存在时保留草稿并记录冲突。
- 草稿目标改用远程地址、Workspace、Session 标识绑定，正常 token 续期不会使同会话草稿失效；发送仍使用当前认证 token。新增三语言“放弃草稿并重载”操作，先成功获取快照才丢弃草稿；默认应用注册表变化同步到已打开的设置页。完整逐字段合并仍待实现。
- 壁纸上传/下载结果在应用前检查原连接身份；下载还检查当前壁纸键及取消状态，避免旧连接或旧壁纸请求返回后替换当前图片。
- 构建：Server Debug 与 Client Release 均使用 `--no-restore -m:1 -p:UseSharedCompilation=false -v quiet`（Client 另加 `-p:UsedAvaloniaProducts=`），通过，均 0 warning / 0 error。
- 新增 `SettingsNotificationsVerification`：使用真实 SignalR WebSocket 握手、两个连接、跨用户订阅拒绝、重新连接时当前版本通知、严格限定通知字段；SQLite 验证补充落盘前不得报告持久版本、重启后持久版本一致。
- 测试依赖最初缺少 assets，已用 `dotnet restore RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --source /home/nanami/.nuget/packages -p:NuGetAudit=false -p:BuildInParallel=false -v quiet` 从本地缓存恢复。沙箱禁止 loopback socket，已通过工具审批在沙箱外运行刚构建的 `dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --settings-only`，退出 0；输出确认通知用例、HTTP 428/409/越权/注册表绕过拒绝、三个存储实现及 SQLite 重启通过。仅临时测试数据，不进行开发机宿主配置实验。
- **仍未验证**：真实 JWT 到期/续期/撤销的端到端行为、客户端草稿与壁纸竞态行为测试、两台真实设备、所有 UI 尺寸/语言/键盘截图、Windows/Ubuntu provider。SignalR 测试使用测试身份中间件，不冒充真实 JWT 验收。
- G1 下一步仍为目录及宿主强类型契约、权限与持久操作协调器、逐字段冲突处理；G2–G6 未完成。不得因通知测试通过将 G1 或总目标标记完成。

### 2026-09-08 / G1 目录与持久操作底座、时区后端切片（阶段未完成）

- 新增 `Protocol/Settings/SettingsContracts.cs`：范围、能力原因、生效时间、目录、时区快照/预览、planId 应用、查询及回滚契约。`SettingsApiRoutes` 为唯一新路由常量；无旧路由别名或双格式解析。Workspace 通知补充 settingId/scope。
- `/settings/catalog` 提供已实现偏好和时区的发现元数据，不在目录请求中串行调用 Helper；具体平台状态由领域读取探测。Client 的既有八页均可通过对应 settings URI 导航，移除已被统一入口取代且无调用者的 VM 导航方法。目录仍待扩充和接入 UI 搜索。
- 新增 `IHostTimeService`、`SettingsOperationCoordinator` 与 `SettingsOperationJournal`。预览绑定认证 actor、资源、请求摘要、观测 revision 和五分钟期限；幂等键同载荷取原计划、异载荷 409，应用只接收 planId。机器写入复用 `HostTimeChange` / `host/time` 的短期授权。
- 独立 SQLite 日志使用 `synchronous=FULL` 和 Data Protection 加密文档，Helper 写入前提交 Applying。跨进程文件锁串行化协调器；已执行计划重试返回原结果，丢失结果/中断保持 Unknown，禁止盲目重放。回滚要求原操作读回版本仍与 OS 一致并重新授权。数据库只记录操作，不充当 OS 配置真源。
- 新增封闭 Helper `HostTimeRead` / `HostTimeApply`，拒绝混合文件/服务字段；Windows 固定系统目录 tzutil.exe，Linux 固定 `/usr/bin/timedatectl`，使用参数列表而非 shell，枚举远端 OS 时区 ID，Helper 自行比较基线并写后读回。Server 保持非特权。
- Linux Helper 启动与所有现有 Helper 子进程新增清空继承环境和可信固定 PATH；NativeService 改为绝对程序路径。Linux policy 移除环境变量覆盖，仅从安装控制文件加载。Windows 服务 managed runtime 启动前的隔离仍需安装链路完善与验证，未将子进程清理当作全部启动隔离已完成。
- 同步更新 Settings 说明、Protocol、Storage 和 Helper 中英文 README。官方实现依据：[Microsoft tzutil](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/tzutil)、[systemd timedatectl 实现](https://github.com/systemd/systemd/blob/main/src/timedate/timedatectl.c)。未执行这些修改命令。

| 本批验证 | 结果 / 证据边界 |
| --- | --- |
| `dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v quiet` | 通过，包含 Server 构建；0 warning / 0 error |
| `dotnet build RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v quiet` | 通过；0 warning / 0 error；没有启动 Helper 执行写入 |
| `dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -c Release -m:1 -p:UseSharedCompilation=false -p:UsedAvaloniaProducts= -v quiet` | 通过；0 warning / 0 error |
| `dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --settings-only` | 新增操作用例通过：加密日志重开、actor 隔离、授权前置、幂等、读回、外部编辑阻止回滚、Unknown 不重放、启动环境去除注入变量；使用受控内存 provider，不等于时区平台测试 |
| 原有 Workspace HTTP、SignalR、并发与 SQLite 重启用例 | 前次完整运行退出 0，最终重建后的复验结果见下方补充 |
| 时区端点真实 JWT HTTP、Windows/Ubuntu 实际读写、外部策略、真实进程重启与 OS 回滚 | **未测试**；未提供指定远程测试目标，未使用开发机进行配置实验 |

- **必做剩余**：时区 UI/SDK/CLI 与宿主通知；目录 UI、逐字段草稿合并；环境变量/主机名/DNS 领域及平台实现；DNS 独立恢复任务；日志保留期清理、Unknown 读回协调、部署 ACL/密钥/运行时隔离；完整 G2–G6 及跨平台验收。时区回滚与协调器已有实现，但宿主侧故障恢复不能据此宣称完成。G1 与总目标保持执行中。
- 最终重建后复验：上述 `--settings-only` 退出 0；操作协调器、两连接通知/越权/重连/无值载荷、HTTP 428/409/跨用户拒绝、存储并发与 SQLite 重启全部通过。为允许临时 loopback socket 经工具审批运行，未扩大为宿主配置测试。

### 2026-09-09 / G1 窗口外 DevCli 时区入口（阶段未完成）

- 延续既有 G1 底座，不重写 Helper transport。DevCli 新增 `settings` 命令组：目录、时区读取、预览、按 planId 应用、操作查询和带 revision 回滚；引用共享 Protocol 的路由与强类型 DTO，无兼容别名。
- 目标必须显式提供 HTTPS Server origin；宿主 JWT 使用独立 `RELAXKONOS_HOST_ACCESS_TOKEN`，不借 Developer Bridge 配对 token 获得宿主权限。应用依赖原 JWT 的短期精确资源授权；缺失授权直接输出 Server 结构化错误，不启动认证对话框。禁止自动重定向；网络失败不重试写入，提示按原 planId 查询。
- 中英文 DevCli README 已同步调用顺序、授权、生效范围及尚未接入的环境/主机名/DNS 命令。
- 构建初次因缺少 DevCli assets 返回 NETSDK1004；从 `/home/nanami/.nuget/packages` 本地缓存 restore 后，`dotnet build Tools/RelaxKonOS.DevCli/RelaxKonOS.DevCli.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v quiet` 通过，0 warning / 0 error。`git diff --check` 通过。
- **未测试**：本批 CLI 参数/HTTP 行为测试、真实 JWT 授权/过期、远程时区读写与回滚、所有 UI 验证。按用户许可暂缓测试；构建不作为运行验收。未提供远程 Windows/Ubuntu 目标，未在开发机执行系统配置实验。
- G1 仍缺完整宿主领域与通知、恢复协调和冲突合并；G2–G6 仍未完成。后续继续客户端独立领域服务、设置 UI 框架和环境变量纵向切片；不将本批 CLI 接入视作阶段或总目标完成。

### 2026-09-09 / G1 客户端领域服务与时区编辑切片（G1/G2/G4 均未验收）

- 当前起点 commit `d2df863002b1ee1361f5ac42bcbc2a8cd455f910`；保留上批 DevCli 工作区变更并继续。新增客户端 `Services/HostSettings/IHostTimeService` 与实现，提供目录、时区读取/预览/应用、操作查询/回滚及精确资源授权，不依赖 Avalonia 页面或窗口。连接参数冻结 Server URL、用户 ID 与会话 ID；请求前、令牌刷新后及响应处理后校验，防止旧连接结果污染新主机。
- 宿主设置 HttpClient 不接入可能重放请求的自动认证 handler；自行在发送前取得有效 token，不自动重定向、不重试写请求。授权沿用 `HostElevationRequest` / `host/time`，不保存密码或自行缓存授权。服务端非文件提权入口在校验目标后复用仍有效的同 JWT/actor/capability/target 授权，不延长既有授权期限。
- 纠正已发现的事实错误：原时间页以客户端 `TimeZoneInfo.Local` 显示所谓宿主时区。已删除此绑定，改用远程 Helper 快照和可选 ID；页面区分 Workspace 显示格式与远程机器时区，并显示远程地址和身份。
- 新增时区编辑 VM 与页面交互：加载/放弃草稿、选取远程 ID、预览原值→新值/影响/期限、授权并应用、按原计划查询结果、带读回 revision 恢复原时区。编辑本身不发送特权写入；应用后锁定草稿，未知结果保留计划 ID 并要求查询。连接变化清理计划、草稿及授权上下文；关闭页面释放订阅并取消客户端等待，已持久化服务端操作独立继续。
- 中英日文补充作用域说明、按钮、授权输入提示和全部操作状态；错误同时保留 HTTP 状态与结构化 problem URI，尚未完成每个错误码的友好本地化。同步 Settings 实现文档。
- 构建证据：Server `dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v quiet` 通过，0 warning / 0 error。Client Release 首次通过但出现两处 Watermark 弃用警告；改为当前 PlaceholderText 后，`dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -c Release -m:1 -p:UseSharedCompilation=false -p:UsedAvaloniaProducts= -v quiet` 再次通过，0 warning / 0 error。`git diff --check` 通过。
- **本批未测试**：HttpClient/VM 行为测试、JWT 续期导致授权失效、授权复用/过期端点测试、会话切换与取消竞态、断网未知结果、关闭窗口后的操作查询、远程 Windows/Ubuntu 时区修改与回滚、所有 UI 尺寸/200% 缩放/三语言/键盘截图。按用户允许暂缓，构建只证明 C#/XAML 可编译，不作为功能验收。未运行任何开发机系统配置实验。
- **继续必做**：完整 G1 领域/通知/恢复与逐字段冲突处理；G2 注册式分类导航、搜索、首页/账户/辅助功能、响应式与页面离开草稿确认；G3 环境完整纵向切片；G4 主机名/DNS 与宿主独立恢复；G5 SDK/入口；G6 跨平台与 UI 验收。当前时区恢复按钮仍不是操作历史/恢复中心，未据此宣称完整恢复体验或任何阶段完成。

### 2026-09-09 / G2 分类导航与缓存搜索第一批（阶段未完成）

- 现有八页增加稳定 Route 注册，根 VM 直接查注册页面，删除按 VM 类型逐一 switch 的导航；核心分类 emoji 图标替换为统一线宽、主题文字色的矢量路径。保留已有页面模板和全部领域功能。
- 设置顶栏持续显示远程地址、用户、Workspace、当前分类路径；增加返回历史。宽度小于 760 个逻辑像素时折叠侧栏、显示分类下拉框，保存/重试操作改为可换行布局。此为响应式框架实现，不表示所有页面内部固定宽度控件已完成整改。
- 新增纯本地不可变 `SettingsSearchIndex`：按标题/分类/范围/settingId/关键词进行多词匹配，前缀结果优先。本地已实现条目立即可搜索，Server 目录独立异步加载、按稳定 settingId 合并；结果保留能力原因，不把远程返回的任意 URI 交给激活器。语言变化重建索引，连接变化清除旧目录并重新读取。
- 搜索 UI 展示分类、范围和能力原因；Ctrl+F、方向键、Enter/双击、Escape 均有处理。三语言同步。修正服务端时区目录仍引用已删除的旧只读说明资源键。
- 编译：首次 Client 构建发现当前 Avalonia 不允许 DataTemplate 内声明 xmlns，移至根节点后，`dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -c Release -m:1 -p:UseSharedCompilation=false -p:UsedAvaloniaProducts= -v quiet` 通过，0 warning / 0 error。`git diff --check` 通过。
- 新增可独立复跑的 `Client/RelaxKonOS.Settings.Tests` 控制台验证程序（必须 `dotnet run`，不是 `dotnet test`）。从本地 NuGet 缓存恢复后运行 `dotnet run --project Client/RelaxKonOS.Settings.Tests/RelaxKonOS.Settings.Tests.csproj --no-restore -c Release -p:UseSharedCompilation=false`，退出 0；英文大小写、中/日文同义词、多关键词、不可用项仍可发现及空结果检查通过。200 项**合成目录**、50 次预热、1000 次查询测量 p95=0.075ms / max=1.132ms；只测生产索引查询，不测网络、UI 调度或渲染，不能据此验收“150ms 内呈现”或声称存在 200 项真实设置。
- **未测试/未完成**：首页、账户、辅助功能、子页分组及关联入口；settingId 定位到具体控件（目前只打开所属页面）；离开草稿保留/放弃确认；页面内部窄布局整改；真实 UI 的 640×480、1024×768、1440×900、200% 缩放，亮暗主题、三语言、键盘/屏幕阅读器与焦点截图；远程目录加载/连接切换竞态测试。G1 及 G3–G6 的既有剩余工作全部保留，未标记任何阶段完成。

### 2026-09-09 / G3 环境契约与 Linux 受限文档核心（阶段未完成）

- 新增共享强类型 Environment 契约：显式 Set/Delete、String/ExpandString、Workspace PATH Replace/Append、宿主/Workspace 快照、来源与掩码标识、环境预览请求。空字符串不会解释为删除；尚未注册环境 HTTP 或 Helper 操作，不能把协议类型视为可用写入 API。
- `EnvironmentValidation` 固定批次 128 项、名称 255 字符、值 32767 字符、总名称/值 UTF-8 数据 256KiB 上限；验证 NUL/不完整 Unicode、非法名称、重复名称、删除载荷、平台值类型。Windows 大小写不敏感、Linux 大小写敏感；PATH/加载器/运行时注入相关项要求显式高影响确认。名称启发式敏感分类只是基础工具，不能替代读取授权或完整秘密保护。
- 新增纯数据展开器，支持 Windows `%NAME%` 与 Linux `$NAME`/`${NAME}` 的显示预览；循环、16 层深度、128KiB 输出及全局工作量限制，不读取本进程环境、不执行命令替换。PATH 分项保留空项、重复项及顺序，并提供当前目录搜索/重复提示。此展开器尚未接入工作负载构造；Linux 原始持久值不会因这个预览工具自动获得 shell 展开语义。
- Helper 新增 `LinuxEnvironmentDocument`，实现受限 `/etc/environment` 文档的全量先解析/验证、纯内存编辑与结果重解析；保留未修改行、注释、顺序、缩进及 LF/CRLF。拒绝重复变量、export 声明、等号附近歧义空白、转义、换行值及无法无损表达的数据。失败不产出部分文件，也不执行任何文件写入。
- 官方实现核对：[Linux-PAM pam_env.c](https://github.com/linux-pam/linux-pam/blob/master/modules/pam_env/pam_env.c) 的 `_parse_env_file` 在引号处理前截断 `#`。据此拒绝包含 `#` 的变量值，避免错误地把引号当作通用 shell 转义。不同构建/发行版消费者仍需在目标 Ubuntu 上核验；此解析器不宣称支持任意 PAM/systemd/shell 语法。
- 验证：`dotnet run --project Client/RelaxKonOS.Settings.Tests/RelaxKonOS.Settings.Tests.csproj --no-restore -c Release -p:UseSharedCompilation=false` 退出 0，新增环境检查覆盖保真行/CRLF、空值与删除、拒绝语法、失败保留源对象、平台名称、高影响确认、NUL/删除载荷、命令文本保持数据、展开循环/资源界限、PATH 顺序；原搜索验证也通过。全部使用内存字符串，未读取或修改开发机宿主配置。Helper 构建通过结果见本批收尾；`git diff --check` 通过。
- **仍未实现/未测试**：Helper 环境封闭操作及 actor/UID/SID 绑定、真实 Linux 文件元数据/ACL 保留与原子替换、Windows 注册表 provider、受保护恢复材料、Server 环境协调器/授权/审计、Workspace 环境存储、工作负载传播、敏感值揭示授权、环境 UI/SDK/CLI；远程 Ubuntu/Windows 的真实写入/读回/回滚全部未测试。当前交付是生产契约/解析核心，不是 mock provider，也不能算完整 G3 纵向切片。继续完成这些实际落点，G0–G6 均按既有未完成验收继续推进。
- 收尾构建：`dotnet build RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v quiet` 通过，0 warning / 0 error（包含新 Protocol 核心）；未启动 Helper。

### 2026-09-09 / G3 Windows Helper 环境 provider（纵向切片仍未完成）

- 新增封闭 `HostEnvironmentRead` / `HostEnvironmentApply` 及本地 IPC 原始环境状态类型。环境请求严格对照允许字段，拒绝混合文件/服务/时区载荷；其他 Helper 操作也明确拒绝环境字段。没有新增任意注册表路径或执行字段，没有旧协议别名。
- Windows provider 使用固定 HKLM 环境键或目标 SID 下的 HKU Environment；验证规范账户 SID 与宿主账户解析，用户 hive 未加载时失败，不写 Helper/LocalSystem HKCU，不挂载或创建任意用户 hive。Server 认证 actor 到 SID 的映射和授权仍待接入，当前没有环境 HTTP 入口。
- 保留 REG_SZ/REG_EXPAND_SZ，使用 DoNotExpandEnvironmentNames 读取原值；内容/类型/目标参与 revision。命名互斥锁串行化同一资源的 Helper 写入，重新核对 expectedRevision，验证批次/高影响确认，逐项写注册表、Flush、读回后报告成功。批次不承诺 ACID，中断/部分失败必须由后续 Server 日志协调，不在 Helper 盲目补偿。
- 写后发送 Environment 的 WM_SETTINGCHANGE 广播，并单独报告通知结果；不能把广播成功当作运行进程已更新，也不保证跨 Windows 会话/服务生效。未执行广播或任何真实注册表读写。
- 两种既有 transport 的审计资源摘要纳入环境资源标识；不记录名称、原值、恢复材料或完整 IPC 载荷。中英文 Helper README 同步边界与待接入项。
- 实现依据：[Microsoft RegistryKey.GetValue](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.registrykey.getvalue?view=net-10.0)、[Microsoft WM_SETTINGCHANGE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-settingchange)。平台实际效果仍需指定 Windows 测试主机验证。
- 构建：`dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v quiet` 与 Helper 同参数构建均通过，0 warning / 0 error；包含新 Protocol 类型。`git diff --check` 通过。
- **本批未测试**：Helper 字段拒绝/账户 SID/命名互斥锁行为测试、Windows 注册表/ACL/外部修改/中断/读回/广播/多登录会话、Windows 服务启动运行时隔离、真实授权/审计端到端。未提供指定远程主机，未在开发机试验系统配置。
- **继续必做**：Server 身份解析及环境读/写/敏感揭示授权、持久计划与恢复日志、Linux 文件 provider、Workspace/工作负载构造、环境 UI/SDK/CLI。Linux 分派当前返回明确的实现待接入错误，不能把这一暂态当作平台不支持而通过验收。G3 及总目标保持未完成。


### 2026-09-11 / G1/G3 客户端环境领域服务与授权目标发现（阶段未完成）

- 本轮起点 `ec1f92b`，工作区干净。核实代码已包含 `HostEnvironmentService`、`EnvironmentOperationCoordinator`、环境 HTTP 路由和精确资源授权校验，超出上方最后一批记录；它们是本轮开始前的代码，不计作本轮新增，也未据此补报测试通过。
- 新增客户端 `IHostEnvironmentService`/`HostEnvironmentService`，独立于 Avalonia 窗口：目标发现、默认掩码读取、显式揭示、预览、planId 应用、查询和 revision 回滚。授权限制为环境读取/揭示/修改三种 capability，不缓存授权密码或环境值。
- 新增共享路由常量与已认证 `GET /host-settings/environment/target?scope=hostUser|hostMachine`，返回服务端当前身份映射的目标并设置 no-store；无 Helper 调用、无环境值、无授权副作用。解决首次读取前精确资源授权需要远程 UID/SID 的发现问题；拒绝 Workspace 等 scope，不引入旧路由别名。
- 将时区客户端已有连接冻结/令牌获取/HTTP 错误与响应校验提取为共用 `HostSettingsService`，环境与时区均使用禁重定向、无自动认证重放的 typed HttpClient；旧时区调用者继续使用其领域接口，没有兼容适配层。
- 构建：Client Release `dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -c Release -m:1 -p:UseSharedCompilation=false -p:UsedAvaloniaProducts= -v quiet` 与 Server Debug 同参数（无 Client 专有参数）均成功，0 warning / 0 error。`git diff --check` 通过。只执行编译，没有执行开发机宿主配置修改。
- **本批跳过测试**：目标发现真实 JWT/身份映射/越权 HTTP 测试；客户端 HttpClient 会话切换、token 刷新、响应取消竞态及授权复用测试；窗口关闭后环境操作、真实 Windows/Ubuntu 环境读写/回滚；UI 布局、三语言、键盘与缩放验收。按用户要求优先实现，复杂测试暂缓；编译不能代替这些行为验收。
- **继续必做**：环境 UI/CLI/SDK、Linux 文件 provider、Workspace 与非特权工作负载环境构造、宿主通知与 Unknown 恢复协调；G2 首页/账户/辅助功能/草稿确认；主机名、DNS 独立恢复与既有 G4–G6 剩余项。G1/G3 及总 Goal 仍执行中，未标记完成。


### 2026-09-11 / G3/G5 DevCli 环境变量操作入口（阶段未完成）

- 保留上批客户端领域服务改动，继续新增 DevCli `environment-target`、`environment`、`preview-environment`、`apply-environment`，复用按计划 ID 查询及 revision 回滚。只支持显式 `hostUser`/`hostMachine`；无交互认证、无写入重试，沿用独立宿主 JWT 和已有精确 capability。
- 环境变更通过 `--changes <file>` 或 `--changes -` 读取 UTF-8 JSON，支持 BOM，2 MiB 输入上限、16 层 JSON 深度、拒绝未知属性和空/超量批次；远程平台详细语义和 256 KiB 数据上限仍由 Server/Helper 校验，不拿客户端 OS 猜测远程规则。解析失败仅给固定提示，不回显输入秘密。空值与显式 Delete 保持区别。
- 默认读取掩码，`--reveal` 是独立显式选项且要求现有揭示授权；授权不足直接保留 Server 错误。同步 CLI help、中英文 README 和 Settings 实现说明，给出本地变更文件/预览/应用/查询/回滚用法。
- DevCli 与 Settings.Tests 首次构建因缺少 project.assets.json 失败，已从 `C:/Users/Administrator/.nuget/packages` 本地缓存 restore。`dotnet build Tools/RelaxKonOS.DevCli/RelaxKonOS.DevCli.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v quiet` 通过，0 warning / 0 error。
- `dotnet run --project Client/RelaxKonOS.Settings.Tests/RelaxKonOS.Settings.Tests.csproj --no-restore -c Release -p:UseSharedCompilation=false` 退出 0：新增实际 CLI 解析器的 BOM/空值/Delete/超限/无效 JSON/秘密不回显/错误参数检查，原环境与搜索检查通过；仅临时文件，无 HTTP 或宿主修改。合成搜索 p95=0.122ms / max=4.786ms 只记录索引性能，不作为 UI 验收。`git diff --check` 通过。
- **本批跳过测试**：CLI 真实 HTTPS/JWT/grant 到期、标准输入管线端到端、网络中断/幂等/远程读回回滚；Windows 注册表与 Linux provider 实机效果；UI 和 SDK 行为。按用户要求暂缓复杂测试，未使用开发机做宿主配置实验。
- **继续必做**：环境 UI/SDK/终端、Linux 文件 provider、Workspace/工作负载传播及宿主恢复/通知；G2/G4/G6 既有剩余项均保留。CLI 接入只推进窗口外入口，G3/G5 与总目标未完成。


### 2026-09-11 / G2/G3 环境页第一批交互（阶段未完成）

- 新增 `EnvironmentPageViewModel` / `EnvironmentPageView` 并注册导航、单色图标、本地多语言搜索及 `relaxkonos://settings/environment` 激活。页面持有草稿，领域能力继续来自窗口外的 `IHostEnvironmentService`。
- 接入远程 HostUser/HostMachine 选择、读取授权、默认掩码、显式揭示授权、名称筛选、类型/来源/展开值/警告；新增/编辑/删除通过显式 Set/Delete 暂存批次，空字符串不转换为删除。客户端按远程快照的平台名称语义验证批次，PATH/运行时高影响确认在预览前校验。
- 预览使用基线 revision 与新幂等键，显示脱敏计划、影响及到期时间；应用仅提交 planId，读取和修改授权分开请求。查询/回滚复用 Server 持久操作，Unknown 保留计划并锁定清除，禁止直接重新读取覆盖未知结果。草稿存在时锁定 scope/读取，明确放弃后可重载；会话切换取消等待、清除旧主机值与草稿。尚无页面离开确认。
- 将原时区授权对话框封装为 SettingsApp 内共用函数，展示实际资源和能力，用于时区和环境；未新增密码存储或自动重试写请求。同步中英日文环境页文案。
- `dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -c Release -m:1 -p:UseSharedCompilation=false -p:UsedAvaloniaProducts= -v quiet` 两次通过（后次包含 URI 与详情完善），0 warning / 0 error。`git diff --check` 通过；未执行宿主环境读写。
- **本批跳过测试**：VM 授权取消/并发/会话切换/未知结果行为，远程 Windows/Ubuntu 读取/揭示/写入/回滚及 JWT 到期；640×480/1024×768/1440×900/200% 缩放、亮暗主题、中英日文、键盘和屏幕阅读器视觉验收。用户要求优先实现，复杂测试暂缓，编译不作为运行证据。
- **继续必做**：Workspace 分区、PATH 分项编辑/顺序/不存在路径检测、草稿单项撤销与完整差异交互、离开页面确认、系统/开发者/终端关联入口、SDK；Linux provider、工作负载构造、宿主通知/恢复以及原有 G2/G4/G6 剩余项。环境页为可编译的第一批 UI，完整 G3 与总目标仍执行中。


### 2026-09-11 / G3 PATH 分项与草稿单项管理（阶段未完成）

- 新增环境 VM 的 PATH 编辑部分：根据远程快照使用平台名称比较和分隔符，追加/替换/删除/上下移动分项；保留空项、重复项和顺序，不做 trim/排序/大小写归一化。索引选择支持独立操作重复项；单项拒绝嵌入分隔符，组装值限制 32767 字符。删除最后分项产生空值，不冒充删除变量；需显式暂存后再预览/应用。
- 接入共享 PATH 警告与中英日文说明，明确空项当前目录搜索语义和远程存在性未检测。新增暂存变量选择、重编辑、单项移除；修改批次会使旧 plan 失效。
- 修复重新加载掩码快照后仍残留旧揭示值的选择/编辑框问题。环境回滚发出前清除旧 Applied 结果；丢失回滚响应保持未知，不能依据旧状态继续清除/再次回滚，须查询原计划。
- Client Release 编译通过，0 warning / 0 error；最后回滚状态调整后复验见下。`git diff --check` 通过。
- **跳过测试**：PATH VM 重复/空项选中和移动交互、超长输入行为、加载掩码/切换会话/回滚丢失响应竞态、布局/键盘/三语言与缩放截图、真实远程路径存在性及进程生效。按用户要求优先实现；没有对开发机做宿主配置修改。
- **剩余**：Workspace 环境、远程路径存在性检测、离开页面草稿确认、完整实际差异复核和恢复历史；Linux provider/工作负载传播/宿主通知及 G2/G4/G5/G6 既有剩余项。PATH 编辑实现不等于完整环境纵向切片验收，总目标保持执行中。
- 收尾复验：包含回滚未知状态修复的 Client Release 构建通过，0 warning / 0 error；diff 检查无空白错误，仅既有 Git LF/CRLF 提示。
