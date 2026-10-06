# RelaxKonOS 设置平台设计

> 当前界面方向：参考 Windows 11 设置，以清晰分类、卡片分组、常用项直接可见和低频项折叠降低负担，不要求沿用原有交互。普通偏好自动保存；主机名和时区在页内明确应用，草稿切页保留，可直接重置，不增加离页确认弹窗或首页待办中心。底层协议约束继续有效，界面不展开其实现细节。


> 设置应用的当前功能、服务入口与限制见 [设置应用](RelaxKonOS.Settings.md)。本文定义目标、范围、协议、安全和恢复规则；下表分开记录已实现能力与设计中的能力。宿主写入的真实平台验收仍有未执行项。

改造阶段与 UI 验收状态见 [Windows 11 风格改造进度](RelaxKonOS.Settings.Windows11.Progress.md)。

## 1. 当前能力与设计范围

| 能力 | 当前边界 |
|---|---|
| 设备辅助功能与日常设置 | 已实现缩放、减少动画、高对比度、自动化通知/免打扰、终端恢复、连接栏固定、首页固定项与分组重置；新增运行态验收统一在最后执行 |
| Workspace 偏好 | 已实现版本比较、冻结草稿、防抖、保存状态和变化订阅 |
| 应用导航与搜索 | 已实现页面路由、分类搜索、登记项控件定位、连接摘要与窄窗口导航；时间语言页自动交互通过，完整窗口与无障碍验收待补 |
| 宿主时区与主机名 | 已实现读取、差异预览、目标授权、应用、查询与恢复；真实平台写入验收待补 |
| 宿主环境 | 已实现 typed 服务、环境页面、PATH 分项、DevCli 与 Linux/Windows provider；真实平台验收待补 |
| Workspace 环境覆盖 | 已实现授权 GET/PUT、加密持久化、版本冲突与空值/删除语义；客户端编辑分区已接入；终端消费尚未接入 |
| 工作负载环境构造 | 纯构造规则已实现并验证；实际 PTY 尚未接入，当前终端不能宣称消费 Workspace 环境 |
| 远程 IPv4 / DNS 与独立断连恢复 | 已接入 Windows netsh + SYSTEM 计划任务及 Linux NetworkManager 检查点；自动化使用模拟 provider，真实断连恢复验收待补 |
| SDK/终端定向入口 | 已实现环境编辑器导航；导航不授予权限，外置应用宿主写 API 尚未开放 |
| 宿主实时通知与完整恢复历史 | 尚未全部实现 |

设置服务独立于设置窗口。允许经过受限 Helper 修改受支持的宿主环境、时区、主机名与 DNS；环境配置数据不能作为 Helper 或特权子进程的启动环境。配置注册表、AppSettings 与真实 OS 配置各有独立真源。

## 2. 设置范围与真源

每个设置在 UI 与协议中明确目标、作用域、来源、权限、持久化与生效时间。页面顶部持续展示当前远程主机与连接身份；本机配置标注“此客户端设备”。切换连接必须清空旧主机草稿与授权引用。

| 范围 | 真源与身份 | 例子 | 写入/生效 |
| --- | --- | --- | --- |
| ClientDevice | 当前客户端设备的既有受管本地存储 | 窗口布局、客户端辅助功能、开发模式 | 本机运行态；不能冒充宿主设置 |
| Workspace | 服务端当前用户拥有的 Workspace | 主题、壁纸、语言、默认应用、Workspace 环境覆盖 | 服务端保存，通知所有连接设备 |
| AppPrivate | AppSettings 现有隔离键 | 编辑器、终端显示偏好 | 应用服务负责解释 |
| HostUser | 认证身份映射到远程 UID/SID 的宿主配置 | 用户环境变量 | 在正确用户身份下写入；不可误写 Server 服务账户 |
| HostMachine | 远程 OS 的真实配置 | 机器环境、主机名、时区、DNS | 宿主 provider；受保护写入走 Helper |

HostMachine 不归某个 Workspace 所有；读写权限由宿主策略和认证决定。设置目录只返回调用者可获知的描述与能力状态，不泄漏其他用户变量或秘密。持久状态以 provider 读回的 OS 配置为准；数据库只保存操作记录、受保护恢复材料、版本元数据，不能把 JSON 写成功当作系统修改成功。

## 3. 产品结构与 UI 验收

| 一级导航 | 子页与主要能力 | 设计范围 |
| --- | --- | --- |
| 首页 | 账号/Workspace 摘要、常用入口、搜索 | 简化方向；操作状态留在相关页，不增加独立待办中心 |
| 系统 | 通知与启动、关于、主机名、环境变量、存储概览、服务与恢复入口 | 环境、主机名可写；存储复用既有读取能力 |
| 网络与 Internet | 网卡详情、地址、DNS、连接诊断、代理、防火墙 | DNS 可写；代理与防火墙复用现有领域服务 |
| 个性化 | 主题、壁纸、Shell、桌面表现 | 保留已有完整功能并统一布局 |
| 应用 | 已安装应用、权限、默认应用、应用设置入口 | 保留能力，避免全部堆在一个长页面 |
| 账户与权限 | 当前宿主身份、会话、应用授权、特权助手状态 | 复用认证；不另造账号或密码库 |
| 时间和语言 | 显示格式、语言、区域、远程时区、时间同步状态 | 时区可写；手动改系统时钟不是本轮要求 |
| 辅助功能 | 文字/界面缩放、减少动画、高对比度 | 当前设备即时生效，保留 Workspace 外观；最终验收待执行 |
| 开发者 | 环境变量关联入口、开发模式、包工具、诊断 | 环境页单一实例/路由；保留已有开发工具 |

UI 要求：

- 宽窗口使用侧栏、内容标题、面包屑与分组卡片；窄窗口折叠导航。验证 640×480、1024×768、1440×900 及 200% 缩放，无关键按钮被裁切、无全页横向滚动。
- 统一图标与现有主题资源，不以 emoji 作为核心导航图标；亮/暗主题、焦点、高对比度、屏幕阅读名称和键盘顺序完整。中文、英文、日文文案同步。
- 每项提供标题、说明、当前值、范围、生效提示与状态。详情页有相关设置和返回入口；搜索支持标题、关键词、同义词（例如 PATH/路径/环境变量），结果显示分类路径、范围与不可用原因。
- 目录加载不阻塞本地搜索；普通已缓存搜索目标为 150ms 内呈现，基于至少 200 个设置项测量。网络探测异步更新，不让首页串行等待所有 provider。
- 偏好可即时预览并防抖保存，成功静默，失败显示短暂 toast 并记录日志；失败保留草稿但不伪装为已保存。宿主草稿切页保留，页内可重置，不增加离页确认弹窗。
- 时区与主机名采用“编辑 → 应用 → 结果”交互；一次点击应用在后台完成计划准备、必要授权与结果确认，不向用户展示计划期限或强制单独预览。底层仍保留不可变计划、revision、幂等与读回；不对每次键入发起特权操作。有效短期授权范围内复用认证，高影响修改仍展示准确变更内容。
- 区分未登录、离线、加载失败、无权限、需要提权、Helper 不可用、平台不支持、外部策略锁定；不得统一变成灰按钮。离线宿主写入不排队自动重放。

## 4. 独立平台能力

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

## 5. 协议、并发和操作状态

在 `Shared/RelaxKonOS.Protocol` 定义 DTO、枚举和路由常量；沿用当前 API 前缀。环境、时区、主机名、目录、操作查询与回滚已实现；网络快照、IPv4/DNS 写入与连接确认已接入；IPv6 写入、Wi-Fi 扫描/加入和其他 Linux owner 仍为设计项：

| 路由（相对于 `/api/v1.0`） | 语义 |
| --- | --- |
| `GET /settings/catalog` | 当前连接可见目录、能力和平台原因 |
| `GET /host-settings/environment?scope=hostUser|hostMachine` | 当前绑定目标的环境快照 |
| `POST /host-settings/environment/preview` | 强类型环境变更预览 |
| `POST /host-settings/environment/apply` | 根据预览应用环境变更 |
| `GET /host-settings/time`、`identity`、`network` | 各领域快照；network 返回网卡列表，由客户端选择网卡 |
| `POST /host-settings/{domain}/preview`、`apply` | time、identity 使用计划；network/apply 使用网卡 ID、快照 revision 和客户端操作 ID |
| `GET /settings/operations/{id}` | 有权限的调用者查询结果、恢复与待生效状态 |
| `POST /host-settings/network/confirm` | 在期限内按操作 ID 确认网络连接仍可用 |
| `POST /settings/operations/{id}/rollback` | 授权后回退该操作可恢复的状态 |

Workspace 环境覆盖与偏好属于 Workspace 授权路径，不接受借用 host scope 绕过归属检查。AppSettings 继续使用现有协议。不得复制同一宿主能力到每个应用的专有路由。

快照包括 `revision`、`observedAt`、`scope`、`target`、`capabilityState`、`effectiveState`；target 由服务端身份解析，客户端只允许选择已获权的目标。环境快照区分原始值、展开预览、来源、敏感状态，不自动把所有原始值广播出去。

写请求包含 expectedRevision、幂等键和强类型 change set。预览返回短期 planId、脱敏差异、影响、需授权能力和期限；plan 绑定 actor、目标、变更摘要及基线 revision。应用不得用新载荷替换已确认计划。应用前重新检查授权、外部变更和能力状态；冲突返回 409，缺失必要前置条件返回 428。

服务端状态：`Prepared → Applying → Applied | Failed | PartiallyApplied | Unknown`；需连接确认时为 `AwaitingConfirmation → Applied | RolledBack | RecoveryRequired`。生效时间另用 `Immediate | NewProcess | NewLogin | ServiceRestart | HostRestart`，避免“已持久化”与“运行态生效”混淆。

同幂等键同载荷返回既有操作；异载荷冲突。Helper 现有重复 ID 拒绝语义不等同 HTTP 幂等：由 Server 持久操作日志承接重试；不在断连后盲目生成新 Helper ID 重做写入。未知结果先查日志并读回资源，无法确认则进入恢复状态。

按资源串行写入；revision 必须覆盖宿主外部变更（例如规范化快照摘要/平台版本），不能只增长数据库计数。跨多个 OS 资源无法承诺 ACID，应保存步骤结果与补偿状态。回滚也检查新 revision，避免覆盖另一管理员后续修改。

变化通知只包含 settingId、scope、资源标识和 revision，通过现有实时通道扩展；接收端按权限重新读取。重连先重取快照，禁止用旧缓存覆盖新状态。

## 6. 环境变量设计

### 6.1 交互与共同语义

提供 Workspace、远程当前用户、远程机器三个明确分区；新增、编辑、删除、筛选、PATH 分项增删与排序、原始/展开预览、重复与不存在路径提示。区分删除操作与空字符串；不能把空值静默转换为删除。

记录变量来源与覆盖关系。普通变量对 RelaxKonOS 创建的非特权用户工作负载按 HostMachine → HostUser → Workspace → 已授权的单次工作负载覆盖构建；不得直接复制 Server 服务进程环境。PATH 使用平台环境构建规则与显式覆盖/追加模式，展示最终顺序；不要把 Windows 用户 PATH 简化为覆盖系统 PATH。

Windows 名称大小写不敏感，Linux 大小写敏感；PATH 分隔符分别为 `;` 与 `:`。不自动删掉空路径项、重排或大小写归一化；对当前目录搜索等危险语义提示确认。变量名、值、批次数量与总请求大小有明确上限并在两端校验；拒绝 NUL、非法名称和 provider 无法无损表达的数据。

展开预览有限深度，检测循环引用；原始字符串不经 shell 求值。`$(...)`、反引号、分号、引号和换行按数据校验与转义，绝不拼入 shell/PowerShell 程序。

读取变量本身也需要权限。授权读取后直接显示所有变量值，包括敏感值，不再单独授权揭示；日志和通知仍不包含秘密值。恢复材料必须限制访问并按保留期清理，不把完整环境保存为普通审计日志。

### 6.2 Windows provider

用户范围明确绑定认证用户 SID，并正确访问对应用户配置单元；LocalSystem 的 HKCU 不是目标用户。机器范围使用固定系统环境存储位置，客户端不能提交注册表路径；保留字符串类型及可展开字符串语义，写后重新读取。

发送适当环境变化通知，但 UI 明示不会重写已运行进程的环境。新建 Terminal、任务及受管非特权工作负载必须获取新快照构造环境；Windows 服务与其他登录会话可能需要独立重启/重新登录。不得为使变量生效自动重启整个服务器。

### 6.3 Linux provider

Linux 不存在覆盖所有 shell、PAM、systemd 服务的统一用户环境存储。当前实现只支持机器 `host/environment/machine` 的 `/etc/environment`，并把它明确显示为“PAM 登录环境”；Linux `HostUser` 被拒绝，不能伪造为通用用户环境。后续若实现 RelaxKonOS 启动器环境或 systemd `environment.d`，必须作为独立 provider/作用域，分别声明消费者与优先级。

Helper 仅在扫描到未关闭 `readenv`、未改写 `envfile` 的 `pam_env.so` 配置时开放该 provider；否则失败为不支持。读到不能无损编辑的语法时保持文件不变。写入以原始字节 revision 条件化，使用 Helper 互斥、写前二次比对、同目录落盘临时文件、原子替换与读回；保留模式并拒绝链接/目录。不向 `.bashrc`、`.profile` 批量追加脚本。真实目标发行版的 PAM 栈、登录消费者与外部编辑恢复仍须在指定 Ubuntu VM 验证。

### 6.4 提权边界

新增专用 EnvironmentRead/EnvironmentApply（最终命名与枚举统一）及 host-user/host-machine capability；Helper 再验证身份绑定、scope、变量名、长度、变更数、revision 和目标文件/注册表键。不得借 FileWrite 任意写环境文件代替领域校验。

允许用户编辑 PATH、JAVA_HOME 等以及经明确高影响确认的加载器/运行时变量。机器范围变更可影响其他进程，应按管理员操作处理。Helper 与所有特权子进程始终使用由安装策略控制的干净环境和可信绝对可执行路径，不能继承被编辑的 PATH、LD_PRELOAD、运行时注入变量或 Workspace 环境。需要重启受管服务时另行走该服务能力与确认。

## 7. 其他宿主能力与恢复

| 能力 | Windows / Linux 第一轮 | 关键约束 |
| --- | --- | --- |
| 时区 | 两平台枚举与设置本机有效时区 ID | ID 由远程系统列举；不把 IANA/Windows ID 混用；显示格式和宿主时区分开 |
| 主机名 | 两平台读取与设置 | 平台校验、影响预览、读回；域加入或组织策略限制给出具体原因，支持待重启状态 |
| DNS | Windows 网卡；Ubuntu NetworkManager 或 systemd-resolved/networkd 的明确可写组合 | 执行时探测实际 owner，只对声明支持且经过测试的 provider 开放写入；不可直接覆盖被托管的 resolv.conf |
| 代理/防火墙 | 复用现有领域能力与平台支持矩阵 | 一个真源；不在设置应用复制规则引擎或绕过 Proxy/Firewall 授权 |
| 服务、存储、恢复 | 接入已支持的管理/诊断与关联入口 | 不凭空增加磁盘格式化、任意服务执行等功能 |

DNS 编辑支持网卡选择、自动/手动、IPv4/IPv6 地址与顺序，展示变更影响。可能断开当前连接的操作必须在应用前建立宿主侧持久恢复任务；推荐 60 秒确认期限，可在计划中明确实际值。恢复任务不能依赖 Client、Server 请求线程或 Linux one-shot Helper 继续存活；复用或实现固定动作的 OS 调度恢复机制。

客户端从新连接确认后取消恢复；超时自动恢复旧配置。主机重启后读取恢复日志并处理未完成计划。若部署无法提供独立恢复能力，DNS 写入标为暂不可用并说明前置条件，不悄悄取消恢复要求。主机名等不保证完全自动恢复的操作必须明确手动恢复路径和待生效条件。

## 8. 验证矩阵

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



## 9. 实现与维护入口

当前行为和已执行检查维护在 [设置应用](RelaxKonOS.Settings.md)，应用私有配置见 [AppSettings](../development/RelaxKonOS.AppSettings.md)，宿主授权见 [安全模型](../platform/RelaxKonOS.Security.md)。增加能力时同步更新当前状态、Protocol、Client、Server、Helper、测试和操作文档，不添加旧接口兼容层。

真实写入检查在明确指定的隔离 Windows/Ubuntu 宿主执行，记录目标身份、系统版本、命令/步骤、读回、恢复与审计结果。尚未执行的检查留在能力边界和验证矩阵中，不以阶段进度或构建替代。

## 10. 平台后续交付顺序

1. 设置定向入口：SDK 与终端通过 `ISettingsNavigation.OpenEnvironmentAsync()` 打开 `relaxkonos://settings/system/environment`，选中系统分类并复用宿主环境编辑器。导航本身不授予读取或修改权限；外置应用宿主写 API 仍未开放。
2. Workspace 环境：版本化存储与当前 Workspace 授权契约已建立，客户端编辑分区已接入，下一步接入终端消费；不将环境值混入外观偏好或普通通知。
3. 工作负载环境：在服务端进程创建边界构造目标用户的非特权环境，并接入终端；特权终端、Helper 和管理进程使用独立可信环境，不接受覆盖。没有这条消费链前不宣称 Workspace 环境已生效。
4. DNS：先确定受支持 backend 和持久、独立的恢复调度机制；恢复能力与重启/断连验证完成前保持不可写。

时区与主机名的页面不承担平台操作管理中心职责。正常用户只需编辑和应用；结果未知时保留计划并提供查询，阻止重新加载清除操作或重新提交。Windows 主机名变更提示待重启，不自动重启宿主。

### Workspace 环境存储契约（已实现，尚未接入进程）

- `GET/PUT /api/v1.0/workspaces/{id}/environment` 使用现有 Workspace 归属校验，跨用户返回 404；响应均禁止缓存。不存在配置时返回 revision `"0"`、空覆盖集合与 PATH Append 模式，不创建宿主配置。
- PUT 接收现有 `WorkspaceEnvironmentUpdate`：`expectedRevision`、Set/Delete 批次、`pathMode`。缺失 revision 返回 428，无效输入 400，过时版本 409；错误带稳定 `problemCode`。高影响变量仍需 `confirmHighImpact`。
- SQLite 即时事务提交后才返回成功，revision 每次成功提交递增。变更只作用于 Workspace 覆盖，删除意味着撤销覆盖，空字符串仍是覆盖值；PATH Replace/Append 与批次原子保存。单批次与全量快照均受 128 项、名称/值长度与总 UTF-8 字节上限约束。
- 数据存储在 `data/workspace-environment/environment.db`，文档由宿主 Data Protection 保护并绑定用户与 Workspace。密钥按现有宿主配置持久保存；数据库与宿主密钥共同纳入恢复。它不使用延迟落盘的外观偏好缓存，不向普通审计或变化通知写入值。不可解密或无效记录返回错误，保留原文档。
- `expandedPreview` 仅按 Workspace 自身覆盖做有限替换，缺失宿主基线会产生未解析引用提示；它不是最终工作负载环境。编辑分区与客户端服务已接入；工作负载消费者与实时通知尚未接入，不能以 GET/PUT 成功宣称新终端已经生效。

### Workspace 编辑与工作负载构造规则（2026-10-05）

系统页新增独立 Workspace 环境入口，搜索 ID 为 `workspace.environment`。编辑器读取当前 Workspace 原值，按变量暂存 Set/Delete 后统一应用；PATH 模式可单独提交空变更批次，不制造空 PATH。高影响变量及已有 PATH 的模式变更需要确认。失败保留草稿；传输失败使结果未知时锁定再次提交，明确“放弃草稿并重新读取”恢复远程基线。切换身份关闭编辑器，关闭时清空值和草稿。

客户端 `IWorkspaceEnvironmentClient` 将稳定服务、用户、登录会话和 Workspace 绑定为连接目标，令牌获取前后、HTTP 返回和解析后均校验目标；写入不经过认证重放 handler，也不跟随重定向。已验证令牌获取期间 Workspace 切换不会发送旧目标写入。

`WorkloadEnvironmentBuilder` 仅构造非特权用户环境数据，尚未接入实际 PTY：机器、用户、Workspace 按顺序覆盖普通变量；Windows 用户 PATH 与机器 PATH 合并，Workspace 明确 Append/Replace，保留空项、重复和顺序。Windows 只展开 ExpandString，检测循环和超限；Linux 保留原始字符串，拒绝伪造通用 HostUser 层。拒绝掩码、缺失原值、错误来源和超限最终环境。没有读取或继承 Server 进程环境，也不得用于 Helper/管理员进程启动。
