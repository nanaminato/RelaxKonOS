# RelaxKonOS 功能模块与双端应用扩展设计

> 状态：设计与实施路线，除明确标注的现有能力外均为**计划中**。本文不表示模块安装器、第三方 Server 扩展、进程隔离或热更新已经实现。本文记录从官方模块化到第三方双端扩展的完整路线；每个阶段以实际实现和验收记录为准。
>
> 范围：RelaxKonOS 自身的服务端功能模块、桌面应用组件、跨端包及扩展宿主。Android 详细设计和实现由 [Android 工程文档](../../Client/RelaxKonOS.Client.Android/docs/design/ApplicationPackages.Design.md)维护。

## 1. 目标与边界

将当前 Server 功能全集逐步拆成 Core 与可选择安装、启用的官方功能模块。Server 启动时只为启用且满足条件的模块注册服务、后台任务和路由，并向客户端报告实际可用能力。模块可拥有客户端组件、服务端组件或两者；客户端与每一台 Server 分别管理本端实际安装状态。

最终支持第三方应用扩展客户端与 Server。官方模块运行在受信任的 Host 进程中；第三方 Server 代码计划通过独立扩展进程和受限协议接入。隔离进程本身不构成完整沙箱；必须同时具备 OS 身份与资源隔离、受限通信、能力代理和平台验收。

优先目标是明确边界、按需启用和独立分发，并让开发者或开发 agent 能便捷地构建、更新和验证测试服务器上的官方模块；不承诺固定的内存节省。启用控制、程序集加载、文件实际安装和资源消耗是不同事实。首版修改模块集合需要重启 Server；不实现运行中修改默认 DI 容器、动态卸载 Endpoint 或热卸载 HostedService。热更新仅作为最后的独立评估，不是前面阶段的完成条件。

本设计不改变系统既有认证、宿主模式、Helper 授权、用户权限、受管运行时生命周期或 Android 原生应用发布方式。包元数据、能力声明与数字签名均不能自行授予权限或提升为内置身份。

## 2. 当前基础与约束

| 现状 | 设计含义 |
| --- | --- |
| [Program.cs](../../RelaxKonOS.Server/Program.cs) 统一注册服务，[ServerPipeline](../../RelaxKonOS.Server/Startup/ServerPipeline.cs) 统一映射路由和 Hub。 | 模块化必须同时覆盖 DI、HostedService、Endpoint 和 Hub。 |
| [HostServiceRegistration](../../RelaxKonOS.Server/Startup/HostServiceRegistration.cs)、[ProxyServiceRegistration](../../RelaxKonOS.Server/Startup/ProxyServiceRegistration.cs) 与 [DeploymentServiceRegistration](../../RelaxKonOS.Server/Startup/DeploymentServiceRegistration.cs) 含多领域注册及后台工作。 | 注册函数的拆分要先检查跨域依赖、初始化与恢复顺序，不能直接按目录机械搬迁。 |
| [ServerDescriptorFactory](../../RelaxKonOS.Server/HostMode/ServerDescriptorFactory.cs) 根据宿主模式、配置及领域条件产生能力。 | 加入有效模块集合后，同步更新能力和 Host 描述的相关字段，避免两个描述互相矛盾。 |
| [DeveloperPackageManager](../../Client/RelaxKonOS.Client/Services/Developer/DeveloperPackageManager.cs) 对桌面 `.roapp` 先注册元数据、启动时加载入口；[兼容性设计](../development/RelaxKonOS.ApplicationCompatibility.md) 先检查客户端平台和 Server 能力。 | 复用桌面应用发现、`ApplicationManager` 和兼容性机制；当前加载器不是 Server 模块管理器。 |
| [Server 项目](../../RelaxKonOS.Server/RelaxKonOS.Server.csproj) 集中引用 PTY、ACME 等依赖并发布 7-Zip、Mihomo 资源。 | 只加开关不会移除程序集和资源；需要独立项目、发布清单与实测才能证明磁盘收益。 |
| [组件中心](../platform/RelaxKonOS.ComponentCenter.Design.md) 和[受管运行时设计](../platform/RelaxKonOS.ManagedRuntimes.Design.md)管理宿主软件及其事务。 | 功能模块与其管理的软件是不同对象；不能绕过已有安装、Helper、恢复及来源规则。 |
| [VSD 契约](./RelaxKonOS.VirtualSystemDrive.Contracts.md)规定内置工厂由 Host 控制，外部描述符不授予信任。 | 文件系统元数据不能将包升级为官方模块或指定任意 Host 内入口。 |

原讨论提出启动时模块化、双端包和第三方独立宿主；本文以当前代码为基线细化依赖、事务与验收。现有 `.roapp` 客户端扩展仍按现有文档执行，不因本设计而获得隔离保证。

## 3. 名词、权威与状态

| 对象 | 身份与权威 | 独立状态 |
| --- | --- | --- |
| 功能模块 | RelaxKonOS 提供的一组服务、路由、后台工作及能力；官方身份由受控发布信任和 Host 注册确定 | 每个 Server 的安装版本、期望启用状态、当前生效状态与恢复状态 |
| 客户端应用组件 | 对应设备上可发现、可启动的桌面应用 | 每个设备的安装、版本、授权和启动状态 |
| 受管软件/运行时 | Nginx、Mihomo、Docker 等宿主软件，沿用其领域安装身份与台账 | 软件的安装、版本、配置、运行和恢复；不由模块清单推断 |
| 服务端能力 | Host 根据实际模块、宿主模式、平台与运行时条件发布的稳定契约 | 可用、不支持或不可用；不等于用户授权 |
| 包 | 关联一组有版本的载荷和声明的分发单位 | 本地客户端与每个目标 Server 独立安装；包存在不证明任一端运行 |
| 权限 | 用户、主机与领域授权机制的判断 | 请求、授予及撤销独立于包清单和能力 |

例如 Docker 管理模块已启用，而 Docker Engine 未安装时，应能发现管理入口并引导用户到组件中心；能力与具体操作应如实反映 Host 支持和软件状态。模块启用不自动安装软件，删除模块也不自动删除软件、业务数据或配置。状态未知必须显示未知，不得当成未安装或成功。

## 4. 目标架构与启动流程

```text
发布与安装                 Server 启动                              客户端
官方受控模块包         已安装清单 + 期望状态                     本机包目录
       │                        │                                      │
受保护版本目录        完整性/兼容性/依赖/恢复检查                  元数据发现
       │                        │                                      │
本机安装状态     →   冻结有效模块快照                             能力检查
                                │                                      │
                    注册服务、后台任务                               用户启动
                                │                                      │
                         Build Host                          惰性加载入口类
                                │
                    映射路由和 Hub → 发布实际能力
```

Core 负责启动、模块目录和状态、身份认证、宿主模式、授权、基础存储、Workspace、协议、受限 Helper 通道、诊断和恢复入口。Files 基础访问暂作为 Core 候选，待依赖审计确认。可选模块负责自己有明确归属的注册、路由、后台任务、能力及生命周期观察。

模块接口可采用以下形状，具体类型与所在程序集在阶段 0 冻结；这只是设计草案：

```csharp
public interface IServerModule
{
    string Id { get; }
    void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context);
    void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context);
}
```

在 `Build()` 前，Host 读取并验证已安装版本和期望状态，解析依赖并确定拓扑顺序；得到不可变的本次启动模块快照。按顺序注册服务，构建 Host，初始化 Core 存储及需要的恢复入口，再由相同快照映射路由/Hub。只有注册、路由和启动条件均有效的功能才对外报告为可用。启动失败不能显示成功或错误地发布能力。

同一模块的服务、后台任务、Endpoint、Hub、能力、安装适配器和恢复职责须有清晰归属。DI 注册顺序、鉴权中间件顺序、Endpoint 授权、User Mode 限制和共享锁保持既有语义。模块不能自行修改 Core 安全中间件、绕过 Host 授权或开放通用 Helper 命令。

能力描述由实际有效模块与宿主模式、平台、领域条件共同决定。模块声明只是候选输入，最终由 Host 计算；能力不代表当前用户有权限，也不代表被管理软件已安装。客户端在重连或 Server 重启后刷新描述；正在进行的请求仍须接受服务端实时校验，不以客户端缓存作为授权。无能力时保留可发现入口并给出原因和可操作的管理入口；操作是否可执行由服务端复核。

## 5. 模块边界与依赖

以下是审计候选，而非已确认的独立组件或承诺：

| 候选 | 可能的归属及拆分注意事项 |
| --- | --- |
| Core | Identity/Auth、HostMode、Storage、Workspace、基础 Protocol、Helper 桥、模块和恢复管理；基础 Files 待确认。 |
| Terminal | TerminalHub、会话与 PTY；核实会话关闭、认证、平台依赖后可作为首个试点。 |
| Docker | Docker Endpoint、服务及后台任务；应用部署、下载代理与镜像源存在交叉依赖。 |
| WebServer / WebsitePublishing | Nginx 管理与网站发布；需核实证书、权限、事务和两者之间的硬依赖。 |
| Certificate | ACME 与续期可能可选，但 Kestrel 启动证书和 Host 身份属于基础设施交叉点，不能直接整体移除。 |
| Proxy / GatewayProxy / ProxyServer | 分析共享服务、系统代理、配置发布、安装台账与启动恢复；先保证恢复，再决定拆分粒度。 |
| Tunnel、Git、Firewall、File Services、Application Deployment、Backup Recovery | 根据真实接口、生命周期和宿主支持逐项审计；不因目录独立就假设可单独启用。 |
| System Performance | 已有订阅与采样逻辑；先测空闲成本再判断是否值得拆分。 |

阶段 0 必须绘制注册、构造函数、静态调用、项目引用、资源、迁移、Helper 和任务恢复依赖图。将共享能力抽为有明确所有者的窄接口；只在真实存在多个实现或替换需求时引入 provider，不提前建设通用框架。Docker 下载代理等共享行为不能因禁用 Docker 管理界面而意外失效。

模块 ID 与能力 ID 分离。硬依赖指定模块 ID 和兼容版本范围，由 Host 在启动前验证并拓扑排序；缺失、禁用、冲突、循环、版本不兼容或不支持的平台产生稳定诊断，不静默忽略。可选集成由调用方显式判断能力，不把缺失依赖伪装成成功。模块间不得用磁盘 manifest 选择任意程序集或以本地可写文件冒充内置信任。

## 6. 安装、启用与恢复

### 6.1 生效状态

安装事实、期望启用状态和当前进程的生效快照分别保存；安装、启用、停用、更新和移除都通过明确计划完成。建议对外状态至少能表达：未安装、已安装未启用、待重启启用、已启用、待重启停用、故障和待恢复，并包含实际版本、期望版本、目标 Server、原因、状态修订及操作 ID。读失败显示未知；待重启不是已生效。

启用/停用首先检查权限、目标宿主、宿主模式、版本、依赖与反向依赖、活动连接、未完成任务、待恢复记录和资源所有权。受影响项、停机和数据保留策略应在计划中明确，审批后重新验证原始输入和状态修订。执行须具备幂等键、持久操作记录及并发锁，防止重复或旧计划覆盖新状态。

禁用模块不会自动卸载其管理的软件、停止或删除外部资源、删除业务配置或用户数据。涉及这些变化时必须使用对应领域的显式操作和既有授权。依赖中的其他启用模块必须先被安全处理；不得静默级联停用。

### 6.2 故障与恢复

系统代理、配置发布、安装与任务编排等功能存在持久事务及恢复职责。普通停用、更新或删除遇到执行中、状态未知、待补偿或待恢复操作时应阻止，并展示具体恢复入口；不得为了允许停用而清除账本。只有在证明由 Core 提供等价的受控恢复执行者并完成专项验证后，才可允许模块主体缺席时恢复。

安装/更新使用不可变版本目录、受保护元数据、原子切换和可恢复的操作记录；不覆盖正在运行的文件。记录候选版本、先前已验证版本、实际启动结果和恢复原因。候选启动失败时可以回到仍可信且与当前数据兼容的上一个模块集合；不得自动启用已撤销或不可信的版本。没有可信可用集合时，以能提供诊断和受控恢复的模式启动，禁止宣称业务功能正常；Core 本身无法安全启动时应失败关闭并提供本机诊断。

重启前须明确处理中请求、Hub 会话和后台任务的收尾与恢复；跨重启不能将未确认结果当成功。模块版本回退需要先验证数据 schema、其他模块依赖和外部副作用；不支持安全回退时保持待恢复并说明原因。故障注入应覆盖写入、切换、服务停止、启动、能力发布与确认各边界。

### 6.3 与受管软件的关系

模块安装器只管理 RelaxKonOS 模块自身；Nginx、Mihomo、FRP、sing-box、Docker 等继续走各自领域及[受管运行时设计](../platform/RelaxKonOS.ManagedRuntimes.Design.md)的版本、来源、确认、Helper 和恢复流程。模块更新或停用前必须检查受管软件事务与任务依赖；不得重新定义该文档中各平台现行信任和校验规则。组件中心可以提供统一导航和清楚的对象分类，但不合并台账与权限。

## 7. 包、信任与加载

### 7.1 包结构与契约

最终包模型需要表达稳定包 ID、版本、发布者、格式版本、各端载荷及平台架构、Host API 兼容范围、硬依赖、可选集成、能力要求、权限请求、资源摘要和入口。一个包可仅有客户端、仅有服务端或两者都有。各端安装器分别选择可执行载荷，客户端安装不代表 Server 已安装，反之亦然。

示意目录如下，名称与 schema 均需阶段 0/4 正式冻结；它不是当前 `.roapp` 格式或可执行安装示例：

```text
manifest.json
client/<platform>/<arch>/...
server/<platform>/<arch>/...
assets/...
```

安装前执行受限解析：限制包与条目大小、数量和解压资源；拒绝重复条目、绝对路径、路径穿越、符号链接/重解析点逃逸、未知强制字段和不支持的版本。每个路径必须归属于对应不可变版本根。读取清单与验证不能执行包代码。完整性摘要必须与经过验证的发布元数据绑定，不能只相信包内自报的摘要。日志、诊断和计划不保存秘密。

现有 `.roapp`、DevCli、VSD 和示例在双端包阶段直接升级到唯一当前契约，同时更新所有仓库内调用者、测试及文档；不增加旧格式别名、双重解析、迁移适配或回退路由。已有用户数据与已安装包如何安全保留、识别不兼容以及提示重装，须在变更前单独制定不执行旧格式的操作方案，禁止静默删除。包扩展名、schema、API 版本范围算法和版本目录最终命名在实施前冻结，本文不提前声称已确定。

### 7.2 官方模块

阶段 1 可采用编译期受控注册表，先验证条件注册；阶段 3 才引入独立程序集和启动时发现。要做到安装后无需重新编译 Host，必须设计受控程序集加载及依赖解析；它不是运行时热加载。共享抽象程序集由 Host 统一提供并保持类型身份，模块私有依赖的版本冲突、native assets、跨 RID 发布及加载失败必须有明确策略和测试。不能因用了 `AssemblyLoadContext` 就声称安全隔离。

官方模块身份来源于 Host 认可的受保护发布信任与安装记录；任意可写目录中的 manifest 不能声明自己是官方模块。需明确签名或等价可信发布机制、受保护信任根、摘要、发布者授权、撤销、密钥轮换、离线验证和防回退规则后，才开放独立模块安装。仅有 SHA-256 不证明来源。安装及启动前验证载荷与信任状态，失败不执行；按系统/用户模式分别规定目录所有权与可写权限。

### 7.3 开发时从客户端更新测试服务器

本能力的首要使用场景是开发者或 agent 修改官方模块后，快速在明确指定的测试 Server 上验证，无须每次重新发布整套 Server。它是计划中的开发流程；现有桌面开发桥接只管理本机桌面包，不能视为已具备此能力。

```text
构建指定模块和目标 RID → 生成带构建身份的不可变候选包
    → 指定测试 Server 与模块 → 上传候选或选择服务器已有候选
    → Server 校验、预览差异和依赖 → 提交精确计划
    → 暂存、切换并按计划重启 Server → 客户端重连并核对
    → 读取原操作结果、实际运行版本与构建摘要 → 执行专项验证
```

- **入口**：提供可脚本化的开发 CLI，供 agent 调用；桌面客户端可复用同一 Server 协议提供上传、预览、更新与重连界面。CLI 可封装构建、打包、上传、提交与等待，但每一步仍返回可观察结果和稳定退出码。客户端只是发起方，Server 是校验、执行和生效状态的权威。
- **精确目标**：操作绑定已认证的 Server 稳定身份、模块 ID、宿主平台/RID、候选包摘要、模块 API 与 Host 版本、预期当前修订和幂等键。CLI 明确显示目标并拒绝目标变化、错误架构、Host 不兼容、依赖冲突或旧修订；重试不能改用另一台 Server 或另一个构建。
- **构建溯源**：候选包含源码修订、工作树是否含未提交变更、构建配置、目标 RID、构建标识及产物摘要；记录不包含秘密。带未提交更改的构建必须提供可复核的源码快照或逐文件摘要，不能仅凭 Git HEAD 冒充构建来源。更新完成后核对 Server 返回的实际生效模块版本、构建身份和摘要，再运行目标专项测试；上传成功或重启成功均不等于新代码已生效。
- **开发信任边界**：测试实例需由管理员显式启用受保护的开发模块来源及限定的发布者/开发密钥，记录作用范围、期限和撤销；候选包通过对应开发信任验证并清楚标注为开发构建。生产实例默认拒绝该来源，开发信任不能变成正式发布信任，也不能由包、客户端或 agent 自行启用。仅有摘要或能登录 Server 不足以获得官方模块执行权限。
- **传输与执行**：使用现有认证连接及明确的模块管理授权，通过有界上传、Server 暂存和独立验证进行更新；不暴露本机开发桥接、不使用任意路径写入或任意命令执行作为通用安装 API。重启交由已有部署宿主/服务管理机制中的受控操作执行，具体支持方式按 System/User Mode 和平台分别设计并验证。不得假定 Server 进程能自行重启成功。
- **中断与恢复**：服务端在断开客户端前持久化操作、冻结目标与重启意图；客户端重连后按原操作 ID 查询结果。重启后由可信启动流程核验候选和健康状态，报告已生效、失败回退或待恢复；连接超时显示未知，不能重新提交新的目标或报告成功。更新前仍须通过第 6 节的依赖、活动任务、恢复与数据兼容门禁。
- **开发效率**：同一测试实例可显式选择保留必要配置与测试数据，更新单个模块及其确有变化的依赖；可支持按源码变更自动构建候选，但自动提交、目标和授权必须是用户明确配置的开发会话，不得由 watch 静默扩展到其他机器。Host 公共接口或 Core 发生不兼容变更时，明确要求配套更新 Host，而非强行装入模块。

正式发布更新可复用同一事务与验证框架，但使用正式来源和信任策略；开发候选不自动进入正式版本目录或更新渠道。

### 7.4 第三方扩展

桌面现有扩展运行在客户端进程，权限门控不是恶意代码沙箱；现有事实和风险保持明确。未来 Server 第三方扩展默认采用专用宿主进程和受限 OS 身份，而非向 Server 注入第三方 DLL、任意 DI 注册、任意 ASP.NET 路由或 Helper 动词。

Server 与扩展宿主通过有版本的本机 IPC 建立认证、逐插件身份和授权；Host broker 提供封闭、最小化的能力 API。需分别设计文件、网络、进程、秘密、数据目录和资源限额的 OS 约束；不能绕过 broker 直接访问 Server 凭据、其他插件数据、Helper 通道或主机敏感资源。按 Windows 和 Linux 分别明确实际机制及不可提供隔离时的失败关闭行为。签名证明来源或完整性，不证明代码安全；仅有独立进程、SDK 接口或权限声明不构成隔离。

扩展协议应限定消息大小、超时、并发、背压、取消、版本协商和错误码；支持 Host 崩溃、插件崩溃、重启、身份撤销与资源回收。用户和操作者身份通过 Host 验证并绑定请求，插件不能自称 BuiltIn、伪造主体或获得任意宿主提权。扩展可提供的 HTTP 能力须通过 Host 的受控路由、认证和审计边界，不直接接管全局中间件。生产开放前必须有独立威胁建模、平台隔离验证和故障测试。

## 8. 用户体验和管理面

管理界面分别展示 RelaxKonOS 功能模块、客户端应用和受管软件：本设备/目标 Server、已安装与生效版本、启用/待重启、依赖、发布者、权限请求、恢复状态和可执行操作。显示计划的影响、需要的权限、受影响模块及是否中断连接；用户可以取消未提交计划。状态来源必须明确，服务端不可达或状态未知时不伪造成功。

授权管理、包信任、任务台账与领域操作仍由各自的权威系统管理。开发者模式保留本机开发用途；不能把 loopback 桥接直接暴露到网络，也不能把它当成远程 Server 安装授权。跨端包的客户端和 Server 由对应设备分别安装，远程桌面代理若需要，须遵守 [Android 应用包方案](../../Client/RelaxKonOS.Client.Android/docs/design/ApplicationPackages.Design.md) 的独立配对和确认前置条件。

## 9. 分阶段实施路线

所有阶段顺序推进；每项关闭前记录代码范围、测试命令、平台与实际验证结果。设计文档本身不构成实现。若前置假设经依赖审计不成立，先修订本设计与验收条件，再开始对应实现。

### 阶段 0：冻结契约与基线

- 记录当前 DI、HostedService、Endpoint/Hub、能力、资产和恢复依赖；选定首个试点并解释选择。Terminal 是候选，不预设它一定无交叉依赖。
- 冻结 Core/模块边界、模块 ID、依赖规则、各类状态、持久数据与所有权、宿主模式、能力发布、错误码、权限和恢复策略；明确现有安装默认启用集合。
- 记录 Windows/Linux、System/User Mode、安装产物体积、启动及空闲资源基线，以及当前测试覆盖和未验收项。
- 决定试点是否需要真实独立 DLL；确定后续包格式、来源信任、数据升级及发布流水线的设计责任与待决项，不将草案字段冒充当前契约。

**验收**：依赖图能追踪每个试点服务、路由、后台任务和恢复执行者；契约经测试场景核对，无未解决的停用和事务所有权；基线可复现并记录环境。

### 阶段 1：官方模块注册框架

- 在现有进程和发布布局内引入受控的官方注册表、不可变启动快照、依赖检查及诊断。首次迁入时默认集合保持现有功能行为；不因空配置静默关闭功能。
- 将服务注册与 Endpoint/Hub 映射归属到同一模块，统一从快照决定能力。保留 Host 的认证、授权和中间件顺序，并维护现有宿主模式边界。
- 初期通过受控配置或测试宿主选择模块；变更需重启。不声称已实现独立模块包、文件瘦身或第三方插件。

**验收**：启用时现有 API 和权限回归通过；禁用时无所属路由、Hub、后台服务和能力，其他模块可用；覆盖依赖缺失/循环、配置无效及启动失败。分别验证有效能力与 Host 描述一致。

### 阶段 2：首个模块端到端试点

- 按阶段 0 选定的试点迁移实际注册、路由、后台任务、客户端能力要求、文档和部署默认配置。明确会话关闭、Server 重启、可发现入口和缺失原因。
- 提供可测试的期望状态与生效快照，不将本阶段的简单配置说成完整安装中心。真实安装记录、故障/恢复和并发语义按本阶段实现范围明确标注。
- 如果试点是 Terminal，专项验证 PTY、SignalR 认证和断线处理，以及禁用后的已存会话处置；试点改变时重新确定等价场景。

**验收**：Windows/Linux 和支持的 Host Mode 按矩阵验证启用、停用、重启与重连；缺失能力时客户端有清楚提示；请求和会话在重启时可预期终止或恢复。分别报告自动化与实机结果。

### 阶段 3：独立官方模块程序集与分发

- 将试点及其专用依赖、原生资产从 Host 项目和发布输出中拆出；实现受信任版本目录、安装事实、启动时发现、依赖解析和受控加载。建立 RID 精确发布、离线安装及受保护目录。
- 完成安装、启用、停用、更新、移除、重启、失败回退和恢复闭环；先冻结签名/信任根、校验、撤销与更新策略，后开放安装。
- 以测试服务器开发更新为首个更新场景，提供可脚本化 CLI 和 Server 端候选上传、精确计划、受控重启、重连核验；桌面界面可随后接入相同协议。实现前冻结开发来源授权、测试实例识别、构建溯源和各平台重启执行者。
- 验证实际裁剪的发布清单和二进制依赖；条件注册、物理安装、运行时加载及资源测量分别报告。

**验收**：未安装模块的专用文件不在精简发布中，且无相关注册、路由及能力；安装后重启可用；损坏、伪造、版本冲突、错误架构、撤销、路径攻击和中断均按契约处理；运行当前与候选版本的真实升级/回退验证。agent 通过 CLI 对指定测试 Server 完成两次不同源码构建的更新，重连后以实际运行摘要区分两次结果；覆盖上传中断、切换后断连、目标/修订不匹配、开发信任撤销、候选启动失败回退和生产实例拒绝开发来源。

### 阶段 4：多模块迁移与管理界面

- 按依赖和恢复复杂度逐项迁移 Docker、WebServer、证书、代理等候选；每个模块先完成依赖审计和恢复门禁。Core 只保留确有共享或恢复责任的代码。
- 引入模块管理界面和可观察的计划/操作记录，连接组件中心导航；模块与受管软件状态、授权和台账分开呈现。
- 逐领域更新应用文档、追踪项及支持矩阵；受管软件生命周期变更遵循受管运行时设计，不把模块开关当成软件卸载。

**验收**：覆盖模块组合、反向依赖、活动任务和待恢复拒绝、诊断、重启、既有安装默认行为；关键领域在真实宿主核实恢复和外部资源保留。每个模块独立记录已验证和未验证的平台。

### 阶段 5：统一双端包和开发体验

- 冻结唯一当前包 schema、API 兼容规则与分发协议，明确仅客户端、仅 Server、双端包以及桌面 Shell 的不同入口和权限；更新 DevCli、示例、文档与所有仓库调用者。
- 客户端继续由 `ApplicationManager` 注册和激活，启动前检查对应 Server 的实际能力；Server 安装状态由目标 Server 管理。包读取不执行代码，身份与授权均由 Host 决定。
- 设计精确版本、来源、发布者、完整性、权限差异预览及独立两端操作。Android 相关产品和实现要求在 Android 工程文档维护，不能推断 Android 可以运行桌面 DLL。

**验收**：三类包和跨设备/跨 Server 状态正确；不支持平台明确拒绝；恶意路径、伪造 BuiltIn、版本不兼容与权限变更得到正确处理；旧契约调用者全部同步更新，不引入兼容分支。

### 阶段 6：发布来源与第三方服务端扩展

- 在完成可信来源、签名/撤销/轮换、精确版本和更新事务后，设计官方、离线和第三方来源；来源配置不能自行扩大 Host 权限。来源变更与回退需要可审计和可恢复。
- 实现第三方扩展宿主、IPC broker、插件身份、权限和资源限额；按平台落地 OS 隔离和受限身份，完成数据、网络、进程、秘密与 Helper 的边界设计。
- 单独设计第三方管理路由、故障隔离、升级/撤销、跨端版本匹配及用户授权。保持现有桌面扩展的风险描述，直到其隔离能力也被实现和验证。

**验收**：独立安全审查、恶意插件对抗测试和 Windows/Linux 实机隔离验证通过；证明不能越权读取 Host/其他插件数据、访问秘密或直接调用 Helper；崩溃、资源耗尽、撤权、来源撤销、断电恢复和协议不兼容有可观察结果。无法提供有效隔离的平台不开放执行。

### 阶段 7：运行时热变更评估（可选，最后）

闲时释放与请求唤醒的已采纳设计方向见[Server 模块闲时卸载与按需加载](RelaxKonOS.ServerModuleResidency.Design.md)（2026-10-28；首个运行时切片已实施，其余待实施）。采用可配置休眠策略、请求唤醒与租约保护，默认闲时延迟释放，按职责拆分 Proxy 共享能力，先以 Git 只读操作试点。该设计区分实例驻留与安装/版本变更，通过 RES 条目跟踪。**该能力独立推进，不必等待本节的运行中版本更新评估，也不以本阶段是否实施为前提**：协调器、租约、可销毁模块容器、三种策略、会话退出触发、驻留契约与 Git 只读试点已经落地并通过测试（详见该文档 §8），程序集卸载、模块间依赖租约、客户端呈现与 Proxy 拆分仍未实现；模块安装/版本变更仍通过重启生效。闲时释放作为独立工作推进，不受本节运行中版本更新评估的可选性及阶段顺序限制。

只有在真实使用需求和指标证明重启不可接受时另立设计：动态 Endpoint、DI 生命周期、活动请求、后台任务、资源清理、依赖持有、卸载失败、原生库和回滚均须解决。先完成可观测的受控进程重启方案评估，再决定是否实现。没有完整验收前，安装和变更继续通过重启生效；本阶段可以永久不实施。

## 10. 验证与交付记录

各阶段至少覆盖：模块组合与依赖图、能力/路由/服务一致性、授权和 User Mode、客户端重新连接、版本与发布产物、恢复事务、并发与幂等、路径和来源攻击。分别标明单元/集成、真实进程、Windows/Linux 实机和故障注入的覆盖；不得用 mock 或单平台构建代替跨平台验收。

性能与体积比较记录相同构建配置、RID、模块集合、运行时间和测量方法；分别报告发布字节、加载程序集、启动耗时、空闲内存与持续 CPU/IO。测量结果不能推断未测平台或长期收益。

实施时在本节按阶段追加日期、提交/构建身份、命令、结果、证据位置、已知限制和未完成项；涉及受管软件的变更还要同步其跟踪项与应用文档。设计文本本身不构成实现或验收。

### 首个注册框架与 Terminal 试点（2026-10-27）

当前工作树增加编译期官方模块注册表和不可变启动快照，提供依赖拓扑、缺失/循环/重复 ID 与配置错误检查。Terminal 的 PTY 工厂、会话管理器与 Hub 由同一快照控制；共享的 SignalR 用户身份提供器仍属于 Core。登录描述和认证后的 Host 能力将 `Terminal` 与启用状态求交，桌面 Terminal 声明 `server.terminal`，自动恢复也先检查兼容性。其他功能仍沿用原注册方式。

默认启用；在 Server 的受控配置中设置 `Modules:Terminal:Enabled` 为 `false` 并重启后生效，例如 `appsettings.host.json` 中的 `{"Modules":{"Terminal":{"Enabled":false}}}`。无效值及未知模块配置会阻止启动。配置不支持在线修改，不包含安装/卸载或远程更新 API；Terminal 代码及 PTY 依赖仍在 Server 发布包中。重启会中断现有连接；本次未验证各平台的已有 PTY 子进程结束或重新附加行为，管理员应先关闭不再需要的会话。

Windows 开发机上的验证：Server 与桌面 Client 构建通过（既有警告保留）；`Tests/ServerModules` 专项覆盖配置、依赖、客户端 manifest、启停状态与能力一致性，并使用真实回环 HTTP 验证启用时未认证 Hub 拒绝、禁用时返回 404；现有 Terminal Hub 契约检查通过；Alias 登录回归 68 项通过。测试宿主使用真实模块注册和路由，但不是完整的已部署 Server + 桌面客户端 PTY 端到端验收。

阶段 0 的全模块依赖图、性能/体积基线，阶段 1 的其他模块迁移，以及阶段 2 的真实客户端交互、Linux/Windows 和各 Host Mode 会话生命周期验证均未完成；不能据此宣布上述阶段全部验收通过。独立程序集、可选安装、开发 CLI 和远程模块更新仍属后续阶段。

### 双平台隔离测试宿主（2026-10-27）

使用工作树（Git HEAD `c9438abe2686aec32f32bacdbef0c295ce99f3e4`；构建时含未提交更改）发布 `Tests/ServerModules` 的 Release 自包含程序，在 Debian 13 x64 和 Windows Server 2022 x64 测试账户的独立目录执行，各 16 项检查通过。测试宿主使用构建出的真实 Server 模块注册和 Hub 映射，以临时回环 HTTP 端口验证启用时匿名请求受到认证保护、禁用时路由返回 404；还检查配置、依赖失败、客户端 Terminal manifest 和两种状态的服务注册与能力描述一致。Debian 现有 Server 和 Guardian 在测试后均为 active；测试未替换或重启它们。

本次构建的 `ServerModules.cs` SHA-256 为 `308932ec1875e22eba526451db7596d5eb1f6dbe91f1a0a62ebf9384f1663a77`，测试入口 `Program.cs` 为 `cd794e66bf38575b23aff9a63458ecf39aba332f3d488811762ffdba1f11f63a`。Linux Server/测试程序集分别为 `1f303bd66e5ccb905661703e607cc75e13de1f89dad0e64099b692b5aaa54514`、`bc04103fda7b728440ac7c534a19b76e6086d0cbb7a7bf480e61d13a6f11e9df`；Windows 分别为 `817d33babdc517d328ebc049cd532c680408d85297c8e0d6ae3afedee997dab1`、`3ae98595a49f0b39023a5afbba18284528ab5ba63ce05333883cfb6673cd7670`。测试载荷保存在各账户的 `module-acceptance-20261027` 目录，本地构建在 `artifacts/module-acceptance/`。

这不是完整 Server 进程部署测试：没有以真实用户登录、通过桌面操作 PTY、测试启停后实际会话处置，也没有验证 System/User Mode 的 Helper 与服务重启。因此跨平台隔离宿主通过不等于阶段 2 的端到端验收完成。

### Firewall 与 System Performance 迁移（2026-10-28）

工作树基线 `7780ee7b`（构建时含未提交更改）。本轮把注册框架从「只服务 Terminal 试点」泛化为可逐个迁入的形态，并迁移两个独立领域，其他领域仍沿用原注册方式。

- **框架**：`IServerModule` 增加能力声明，`ConfigureServices`/`MapEndpoints` 接收只读的 `ServerModuleContext`（配置、宿主环境、平台），模块拿不到已构建的 `WebApplication`，因此无法调整 Core 中间件顺序。模块的配置名由模块 ID 派生（`relaxkonos.system-performance` → `Modules:SystemPerformance`），避免 ID 与配置名各自维护而漂移；重复 ID、重复配置名、未知模块、未知设置项、错误布尔值、缺失/循环依赖仍阻止启动。
- **能力发布**：启动快照的启用集合求和得到能力集合，`ServerDescriptorFactory.ApplyModules` 统一用它裁掉模块拥有的 `server.terminal`/`server.firewall`/`server.metrics`/`server.processes`；登录描述和认证后的 `/server/capabilities` 走同一方法，两处不会再给出不同答案。部署模式仍是上界：模块不能让 User Mode 主机公布特权能力。
- **新迁移模块**：`relaxkonos.firewall`（`IHostFirewallService` 平台实现、`/api/v1.0/firewall/*`）与 `relaxkonos.system-performance`（指标 Provider、`PerformanceSampler` 及其 HostedService、`PerformanceBroadcastService`、进程采样、`/api/v1.0/system/performance/*`、`/api/v1.0/system/processes/*`、`/hubs/performance`）。
- **边界修正**：`/api/v1.0/system/network-addresses` 从系统监控端点文件拆到 Core 的 `HostNetworkEndpoints`，因为代理应用只问「这台主机可被哪些地址访问」，与是否启用性能采样无关；`PerformanceBroadcastService` 从事务型 Workspace 注册移入模块，禁用后不再留下喂给不存在 Hub 的后台循环。桌面任务管理器声明 `server.metrics` 与 `server.processes`，与 Terminal 声明 `server.terminal` 同一机制。

配置：`Modules:Firewall:Enabled`、`Modules:SystemPerformance:Enabled`（与既有 `Modules:Terminal:Enabled` 同形），默认全部启用，空配置等同旧行为。变更需重启，不提供安装/卸载、远程更新或在线切换；三个模块的代码与原生依赖仍在 Server 发布包内。

Windows 开发机验证：`Tests/ServerModules` 39 项通过（默认集合、逐模块开关、非法配置、依赖缺失/循环/重复/配置名冲突、注册与能力一致性，以及真实回环 HTTP 在启用时 401、禁用时 404；网络地址路由在两种状态都保持 401）。`RelaxKonOS.Server.Tests` 的 `--squid-only`、`--session-valid-only`、`--performance-only`、`--terminal-contract-only`、`--process-termination-only`、`--alias-only`（68 项）、`--firewall-read-only`、`--settings-only` 均通过。真实 Server 进程（`DOTNET_ENVIRONMENT=Development`，临时端口）在默认配置与 `Modules:SystemPerformance:Enabled=false`+`Modules:Firewall:Enabled=false` 两种环境下启动成功，路由矩阵为：`/healthz` 200、`network-addresses` 401、`terminals` Hub 401 恒在；`performance/info`、`firewall/status`、`performance` Hub 由 401 变为 404。

未完成：Proxy 的交叉依赖（任务编排、网关代理、系统代理）当时仍未拆分（已于同日迁移，见「Proxy / GatewayProxy / ProxyServer 迁移」一节）；Docker 的审计结果见下一节。缺失能力的常驻入口与原因提示、模块管理界面、Linux 实机矩阵、模块组合下的重启/恢复与活动会话处置、发布体积与空闲资源基线均未验证；本文不据此宣布阶段 4 验收通过。

### Git 模块与部署端口拆分（2026-10-28）

依赖审计发现 Git 只有一个跨域调用点：`ApplicationDeploymentService` 用 `GitBuildService.Artifact(owner, buildId)` 判断某次源码构建能否作为镜像来源。因此本轮的拆分方式是给消费方定义端口，而不是给提供方开后门：

- 新增 `IDeploymentImageBuildSource`（`ApplicationDeployments/IDeploymentImageBuildSource.cs`），返回 `Available/NotFound/NotReady/Unavailable` 与构建操作（保留 revision 需要的构建溯源字段）。部署域不再引用 `RelaxKonOS.Server.Git` 的任何服务类型。
- 注册顺序即语义：Host 先注册 `UnavailableDeploymentImageBuildSource`，`relaxkonos.git` 模块随后注册 `GitDeploymentImageBuildSource` 覆盖它。禁用 Git 时部署给出 `application-deployment.git_build_unavailable`(503)，不再退化成 `image_not_found`(409)，也不会静默改用归档来源；客户端已补三种语言的该错误码文案。
- `relaxkonos.git` 拥有 `IHostGitCli`、`IGitRepositoryService`、`GitTextEditor`、`GitBuildOptions`/`GitBuildService` 与 `/api/v1.0/git/*`（含 workspace、build 路由），并公布 `server.git`；桌面 Project Editor 声明该能力。构建产物与引擎侧 image id 的比对保持不变（`image_reference_invalid`）。

命令行验证：`Tests/ServerModules` 78 项通过（新增 Git 注册、能力一致性、部署端口实现选择与「未知构建从不返回可部署产物」，以及 `/git/repositories`、`/git/builds/resolve` 的 401/404）。`RelaxKonOS.Server.Tests` 的 `--git-conflicts-only`、`--push-preview-only`、`--alias-only`、`--squid-only` 通过。真实进程：默认配置 `git/repositories` 401 且启动日志列出四个模块；`Modules:Git:Enabled=false` 时 `/git/repositories`、`/git/builds`、`/git/builds/resolve` 全部 404，而 `healthz` 200、`system/performance/info` 与 `server/capabilities` 仍 401。

仍未完成：Git 模块的依赖审计没有覆盖 `GitTextEditor` 对 Core `IFileService` 的依赖（同为 Core，方向正确）；Proxy 当时尚未拆分（已于同日迁移，见末节；Docker、Certificate、WebServer 见后续小节）；模块管理界面与安装/卸载仍属后续阶段。

### Docker 模块与引擎传输边界（2026-10-28）

Docker 不能像 Terminal/Git 那样整体迁入：审计发现同一个 `RelaxKonOS.Server.Docker` 命名空间里混着两层东西——**引擎传输**与**管理界面**。传输层被四个域直接使用：`ApplicationDeployments`（`IDockerEngineService`）、`Installations`（`IDockerEngineService` + `IDockerRuntimeInstaller`）、`ManagedRuntimes`/`Runtimes/FrpRuntimeManager`/`WebServer`/`Proxy`（`IDockerProxyResolver` 与 `IOutboundProxyHttpClientFactory`，即主机出站代理偏好）。若把这些一起塞进模块，禁用 Docker 管理界面会让 Nginx/FRP/Mihomo 与受管运行时下载静默改走直连——正是本设计第 5 节点名的失败模式。

因此边界按「谁需要事实」切分：

- **Core 保留**：`DockerCliEngineOptions`（含为部署构建上下文追加的 `BuildRoots`）、`IDockerEngineService`、`IDockerRuntimeInstaller`、`IDockerProxyResolver`、`IOutboundProxyHttpClientFactory`、`IRuntimeDownloadProxyProvider`。它们回答的是「这台主机怎么执行 docker」与「出站代理是什么」，与是否展示管理器无关。
- **`relaxkonos.docker` 拥有**：容器/镜像/网络/卷编辑端点、`IDockerComposeService`、`DockerStackOperationStore` 与栈操作协调器（HostedService）、`IDockerEngineProxyConfigurator`/`IDockerDesktopProxyReader`/`IDockerProxyService`、引擎生命周期控制、`IDockerImageMirrorResolver`、`/api/v1.0/docker/*` 与 `server.docker`。
- 端点分组原有的 `RequireHostFeature(ServerHostFeature.Docker)` 保留：模块启用但宿主模式不支持时仍然拒绝，模块不是特权能力的来源。

命令行验证：`Tests/ServerModules` 96 项通过，新增断言包括「docker 模块从不注册共享引擎传输」（对比 `ConfigureServices` 前后的服务描述符计数）、compose/栈协调器/代理管理器的注册开关、`server.docker` 与 Host 布尔一致，以及 `/docker/status`、`/docker/proxy` 的 401/404。`RelaxKonOS.Server.Tests` 的 `--docker-control-only`、`--docker-resources-only`、`--stack-operations-only`、`--managed-outbound-proxy-only`、`--current-nginx-only`、`--deployment-definition-only`、`--deployment-progress-only`、`--settings-only`、`--alias-only` 通过。真实进程：默认 `docker/status` 与 `docker/proxy` 401；`Modules:Docker:Enabled=false` 时两者 404，而 `git/repositories`、`system/performance/info`、`server/capabilities` 仍 401、`healthz` 200。

已知未解决：`--components-only` 在本机失败（`NginxPackagePlan` 对「卸载未安装包」的问题码序列断言），已在 `HEAD` 的独立工作树复现，属既有失败，与本轮迁移无关；`--mihomo-runtime-only` 需要 Linux x64，本机跳过。栈操作协调器属于恢复职责，当前配置开关没有「有待恢复操作时阻止停用」的门禁，因此禁用模块后中断的栈操作会停留在未协调状态；这是阶段 1 配置开关的已知限制，实施停用门禁前不得把该开关用于生产。

### Certificate 与 WebServer 模块（2026-10-28）

这两个域相互依赖，且都同时包含「基础设施」与「管理界面」：

- **`relaxkonos.certificates`**：ACME 服务与 ARI 续期循环、证书操作/续期记录、`ICertificateManager`、`/api/v1.0/certificates`。Core 保留 `ICertificateStore`、`CertificateMetadataRepository`、`CertificateDeploymentRepository`、`KestrelCertificateRegistry`、`KestrelCertificateStartupService`——监听器能不能起来不属于任何可选模块。
- **`relaxkonos.webserver`**：`/api/v1.0/webservers/*`、`/api/v1.0/website-publications/*`、`WebsitePublicationStore` 与其后台协调器。Core 保留 `IHostPrivilegeService`、`NginxManagedOptions`、`NginxInstallPackageStore`、`WebServerMetadataRepository`、`WebServerOperationStore`、`NginxWebServerManager`/`IWebServerManager`（部署的站点发布、组件中心的 Nginx 状态、安装域都直接用）。
- **第一条真实硬依赖**：`relaxkonos.webserver` 声明 `Requires: relaxkonos.certificates`（发布事务要绑定证书）。单独关闭证书会让启动失败并打印 `Server module relaxkonos.webserver requires enabled module relaxkonos.certificates.`；两者一起关闭才允许。这正是设计要求的「缺失/禁用产生稳定诊断，不静默忽略」，也是「不得静默级联停用」的实现方式——不替操作者做决定。
- **审计中被启动校验抓到的一处遗漏**：`FileHttp01ChallengeStore` 最初跟着 ACME 进了模块，但 Core 的 `NginxWebServerManager` 在构造时要读它的 challenge 根目录来生成 `location ^~ /.well-known/acme-challenge/`，于是 `Modules:Certificates:Enabled=false` 在 Development 的 `ValidateOnBuild` 下直接失败。已把它移回 Core。这条记录说明依赖审计必须覆盖**构造函数**，不能只看服务名。

验证：`Tests/ServerModules` 119 项通过，新增默认集合、能力与 Host 布尔一致、`Requires` 拒绝与「一起关闭才允许」、证书/WebServer 模块不注册 Core 基座（对比描述符计数）、`/api/v1.0/certificates` 的 401/404；WebServer 模块改为无宿主断言（它的发布协调器绑定应用部署域）+ 真实进程验证。`RelaxKonOS.Server.Tests` 的 `--certificate-renewal-records-only`、`--certificate-binding-only`、`--certificate-replay-only`、`--webserver-only`、`--webserver-sites-only`、`--deployment-definition-only`、`--alias-only`、`--session-valid-only` 通过。真实进程：默认 `certificates`/`webservers` 401、`website-publications` 405（仅 POST）；`Modules:WebServer:Enabled=false` 时两者 404 而 `certificates` 仍 401；`certificates` 单独关闭启动即失败；两者一起关闭后 `healthz` 200、两个路由 404、`docker/status` 仍 401。

### Proxy / GatewayProxy / ProxyServer 迁移（2026-10-28）

审计结论：这一域没有「同一命名空间内管理面与传输面分层」的结构，而是 Core 的其它域直接反向依赖代理运行时。因此本轮沿用 Docker 的原则处理——Core 保留被其它域驱动的运行时与发布链路，模块只拿「只回答 HTTP 客户端」的部分。

**Core 保留**（`Startup/ProxyRuntimeRegistration.cs`，由 Host 注册）：

- mihomo 运行时传输：`IProxyPlatformPaths`、`MihomoControllerOptions`、`IProxyControllerSecretStore`、`IMihomoControllerClient`、`IProxyPrivilegedOperations`、`IProxyDiagnosticLogStore`、`MihomoRuntimeManifest`、`IMihomoRuntimeProbe`、`MihomoRuntimeManager`。
- mihomo 发布台账：`MihomoPublicationStore`、`MihomoRuntimeAuthorizationProvider`、`MihomoSettingsPublicationFiles`、配置/安装/引导/移除四个发布服务，以及 `MihomoPublicationRecoveryService` 恢复循环。理由是 `Installations.MihomoRuntimeInstallationService` 在安装流程里直接驱动这条链路，且中断的发布是「未解决的权威」，不能随 UI 一起消失。
- 代理设置与系统代理：`IProxySettingsService`/`MihomoSettingsService`、`IHostSystemProxyService`。两个 Core 消费者：安装引导从这些设置里选材；`Docker.DockerProxyResolver` 用它们推导受管出站代理——若随模块消失，禁用代理界面会让 Docker 构建静默回落到直连（正是本设计第 5 节点名的失败模式）。
- sing-box：`SingBoxRuntimeService`、`ProxyServerStore`、`ProxyServerRuntimeAuthorizationProvider`、`IProxyServerCertificateSource`。`TaskOrchestration` 的 sing-box 观测/对账/恢复域、`Components.SingBoxComponentAdapter`、`RuntimeCatalogEndpoints` 与 `Installations.SingBoxInstallationService` 都直接依赖。
- 存储适配：`IProxyProfileRepository`、`IProxySubscriptionRepository` 仍按存储提供程序注册在 `StorageStartup`；Core 的配置发布读取前者，缺失时以 `runtime.profile_publication_required` fail-closed。

**`relaxkonos.proxy` 拥有**（`Startup/Modules/ProxyModule.cs`）：引擎适配（`IProxyEngine`/`MihomoEngine`、`IProxyEngineRegistry`）、`IProxyLifecycleService`、操作与审计台账、订阅下载及四个 HttpClient、TUN 安全与恢复入口、`IProxyPlatformService`、`ProxyServerService` 与凭据过期循环、Squid 网关、系统代理守卫、`/api/v1.0/proxy*`、`/api/v1.0/gateway-proxy/*`、`/api/v1.0/proxy-server/*`，以及 `server.proxy`/`server.gateway-proxy`/`server.proxy-server`。

三个应用共享一个模块：Host 描述符只有一个 `Proxy` 布尔，三个平面能力由它投影——网关代理在全平台都回答（不支持时报 `Supported=false` 而不是让应用消失），代理服务器仍受部署模式限制（仅 System Mode）。

**模块上下文新增 `StorageProvider`**：`ServerModuleContext` 现在携带已解析的存储提供程序（不是原始配置值），因为档案/订阅/配置事务服务只在 SQLite 下成立；模块据此注册，避免内存模式出现「服务存在但仓储缺失」的半成品。`Program.cs` 相应把上下文构造移到存储提供程序解析之后，模块因此不必重复「默认是 sqlite」这条规则。

验证：`Tests/ServerModules` **157 项**通过，新增「默认启用 proxy」「能力撤回（三个一起消失）」「模块从不注册共享运行时骨干（对比 5 个服务类型的描述符计数）」「引擎注册表/生命周期/代理服务器/Squid/TUN 的注册开关」「发布恢复循环不随 UI 消失」「存储绑定服务随模块与提供程序双重门控」。`RelaxKonOS.Server.Tests` 的 `--proxy-configuration-only`、`--proxy-controller-only`、`--proxy-geodata-only`、`--proxy-tun-only`、`--system-proxy-only`、`--squid-only`、`--managed-outbound-proxy-only`、`--managed-component-cleanup-only`、`--official-component-releases-only`、`--alias-only`、`--session-valid-only`、`--settings-only`、`--docker-control-only`、`--docker-resources-only`、`--webserver-only`、`--terminal-contract-only` 通过。真实进程：默认配置 `/proxy`、`/proxy/settings`、`/gateway-proxy/squid/status`、`/proxy-server/status` 全部 401；`Modules:Proxy:Enabled=false` 时四者 404、`POST /proxy/runtime/external-detection` 也 404，而 `healthz` 200，`docker/status`、`git/repositories`、`certificates`、`system/performance/info` 仍 401。

已知限制：

- 禁用后 `SystemProxyGuardHostedService` 不再周期性重设系统代理，`ProxyServerExpiryService` 停止清理过期凭据。停用前应先在代理界面关闭系统代理与 TUN；未确认的运行时发布由停用门禁拦下（见下一节）。
- `Storage:Provider=memory` 的启动在本机仍失败：`Git` 的 `LocalGitRepositoryService` 需要只有 sqlite 分支才注册的 `IDbContextFactory`。这是既有问题（本轮不新增该失败点），也说明内存模式目前不是可用的启动组合。

### 停用门禁（2026-10-28）

本文第 111 行对停用提出的要求是：遇到执行中、状态未知、待补偿或待恢复操作时应阻止，并展示具体恢复入口，且**不得为了允许停用而清除账本**。本轮把它落成 Host 的一层启动校验，而不是靠操作者自觉：

- `IServerModuleDeactivationGuard`（`Startup/ServerModules.cs`）：模块自己回答「我现在缺席会留下什么」。只有**被停用**的模块会被问，且只问实现了该接口的模块；启用模块一次都不会被检查。
- `ServerModules.ValidateDeactivationAsync`：汇总所有遗留工作，非空即抛，消息里带模块 ID、操作 ID、项目名与状态。**没有 override 开关**——完成或恢复工作后才允许停用。
- 调用点在 `Program.cs` 打印启用模块清单之后、`InitializeServerStorageAsync`、路由映射与任何 HostedService 启动之前，因此表现为「拒绝启动」而不是「启起来再报错」。
- 读不到的账本按「状态未知」处理并同样阻止（fail-closed）。把无法解析当成「没有待办」正是这里要避免的静默丢失。
- 共享基础设施同理再确认一次：门禁需要读的账本必须比模块活得久，所以它们留在 Core（见下）。

**账本归属调整**：`DockerStackOperationStore` 从 `relaxkonos.docker` 移回 Core（`HostServiceRegistration`），与 mihomo 发布台账同一理由——模块缺席时门禁仍要读它。写入者仍是模块里的 `DockerStackOperationCoordinator`（唯一写入者，并在启动时对账）；`DockerComposeOptions` 的 `Configure` 随之下移。

**当前覆盖**：

| 模块 | 判定来源 | 语义 |
| --- | --- | --- |
| `relaxkonos.docker` | `DockerStackOperationStore.Active`（`Queued`/`Running`） | 未协调的 compose 栈操作 |
| `relaxkonos.proxy` | `MihomoPublicationState.Pending` | 已开始但未确认的运行时发布（未解决的权威） |
| `relaxkonos.webserver` | `WebsitePublicationStore.Pending()`（`Queued`/`Running`） | 未完成的站点发布（协调器要拿证书与 Web 服务器重新核验） |

`WebsitePublicationStore` 为此新增了只读的 `Pending()`：门禁问问题时**不得**调用 `InterruptActive()`，把一次「能不能停用」的询问变成状态变更正是设计禁止的那种副作用。

**未覆盖（明确记录，不要误以为缺口已经清零）**：

- `relaxkonos.certificates`：证书操作是每次 ACME 请求内的短事务，另有 `CertificateRenewalWorker` 的续期职责。停用后只是停止续期，不存在「只有它才能收尾」的挂起操作，因此本轮不加门禁；但续期停止应在界面上提示（与系统代理守卫同类）。

验证：`Tests/ServerModules` **171 项**通过，新增断言为「门禁只问被停用的守卫」「启用模块从不被检查」「诊断包含模块与操作 ID」「Docker 守卫读真实账本（空账本 0 条、`Running` 操作 1 条且带操作 ID）」「站点发布守卫报出排队的发布，且询问本身不会把操作改成 interrupted」「代理守卫把不可读的发布权威报为无法读取而非无待办」「清单把同一份遗留工作一并发布」。真实进程三连（同一账本目录）：未完成栈操作 + `Modules:Docker:Enabled=false` → 启动被拒并打印 `compose stack operation a35c030e-… for project "gate" is still Running (Deploy)`；启用 Docker 启动 → `healthz` 200、协调器把该操作改为 `interrupted`；再次停用 → 启动成功、`/docker/status` 404。空账本 + 停用 Docker → 正常启动（无误报）。夹具写入器 `Tests/ServerModules` 的 `--write-unresolved-stack <dir>` 让上述账本由真实 store 生成，而不是手写 JSON。

#### 只读模块清单 `/api/v1.0/server/modules`（2026-10-28）

门禁只有在**重启被拒之后**才说话，而操作者真正需要的是改配置之前的答案。因此新增一个只读路由（`ServerApiRoutes.Modules`，`ServerModuleDto`）：

| 字段 | 含义 |
| --- | --- |
| `id` / `enabled` | 模块身份与本进程是否启用 |
| `capabilities` | 该模块在启用时提供的能力（Host 的并集里可追溯到它） |
| `requires` | 它声明依赖的模块 |
| `requiredBy` | **启用中**的、声明依赖它的模块——这是「停用会被拒」的第一类原因；已停用的依赖者不算 |
| `orphanedWork` | 该模块的守卫报出的未收尾操作——第二类原因；空数组表示现在停用不会被门禁拦 |

语义约束：

- **只读**：启停仍然只能靠配置 + 重启；这条路由绝不改运行状态。
- `orphanedWork` 只对**启用中**的模块有意义（停用模块若有遗留工作，启动早已被门禁拒绝），因此不对停用模块调用守卫，避免「查询即触发」。
- 单个守卫抛异常时，清单不会 500，也不会把它报成「可以停用」，而是为该模块写一条「守卫失败（异常类型）」——诊断接口的 fail-closed 形式。
- 端点需要 JWT（与 `/server/capabilities` 同组），`Program.cs` 把 `ServerModuleContext` 注册进 DI，端点与启动门禁读同一个快照。

验证：`RelaxKonOS.Server.Tests --alias-only` 增至 **70 项**（真实登录后带 Bearer 取清单，断言能激活端点、列出终端模块、并给出「证书被 webserver 依赖」的方向）；真实进程 `/healthz` 200、`/server/modules` 与 `/server/capabilities` 均 401。

#### 客户端的解释链条（2026-10-28）

模块清单的意义在于让「这个应用为什么用不了」有可操作的答案，而不只是把能力名重复一遍。桌面端因此加了三层，全部只读：

1. **取数**：`Client/RelaxKonOS.Client/Services/Modules/ServerModuleCatalog.cs`（`IServerModuleCatalog`）按登录读一次 `/server/modules` 并缓存。它永远不阻塞启动决策——`GetAsync` 把任何失败折叠成 `null` 并把原因记在 `LastFailure`；缓存按 `ServiceId` 归属，登录结束即作废；`EnsureLoaded()` 供启动判定顺手触发一次后台读取。
2. **归因**：`ServerCapabilityExplanation.Explain(inventory, capability)` 是纯函数，把「缺少的能力」归到四种情形之一——未读取（`Unknown`，不猜）、提供者被停用（**点名模块**）、提供者已启用（说明限制来自部署模式或宿主平台）、没有任何模块声明（不是模块开关能解决的事）。同一份清单永远给同一个答案，启动提示与设置页不会互相矛盾。
3. **呈现**：
   - `ApplicationCompatibilityService` 在「缺少能力」的提示里追加第二行原因（`app_compatibility.reason_module_disabled` / `reason_module_enabled` / `reason_no_module`），清单不可用时**不写第二行**，而不是猜原因。
   - 设置 →「系统」页新增只读的「服务端模块」卡片：模块 ID、启用状态、能力、以及两条会被门禁拒绝的原因（被哪些启用模块依赖、还有几条未收尾操作及具体操作描述）。页面在可见时通过 `OnAttachedToVisualTree` 读取一次，失败时明确显示「服务端没有返回模块清单」，不会把空列表渲染成「这个服务端没有模块」。

验证：`Tests/Client/RelaxKonOS.ApplicationLayout.Tests --modules-only` 覆盖归因矩阵（含「两个提供者时以启用者为准」）、缓存只读一次 / 失效重读 / 换服务器换清单、未登录不发请求、500 与服务不可用不缓存为答案、登录事件预取与登出作废，并在三种语言下真实渲染 `SystemPageView` 断言模块 ID、依赖说明与未收尾操作说明都出现且没有未翻译标签；`Tests/Client/RelaxKonOS.WindowPreviews.Tests --settings-only`（本轮新增的隔离入口）仍通过。完整布局套件 105 个页面组合通过。

已知边界：这一页只解释、不改状态——模块启停仍是配置 + 重启；「安装/卸载模块」与在线切换仍属后续阶段。

#### 资源与体积基线（2026-10-28）

「停用模块究竟省多少」此前一直是本文的空白项。已在 Windows 上用同一 Release 构建实测，方法与全部数字记录在[模块闲时卸载与按需加载 §7](RelaxKonOS.ServerModuleResidency.Design.md)（RES-01）。结论只有三条：

- 8 个模块全开与全关相差 **约 31 MB 工作集 / 26 MB 私有字节 / 370 ms 启动**（三次交错重复的中位数）。
- 闲时 CPU（约 0.9% 单核）与闲时 IO（≈0 B/s）在两种配置下**相同**，因此「停用模块省 CPU」在当前实现里没有依据。
- **模块开关不改变任何发布字节**：8 个模块同处 `RelaxKonOS.Server.dll`，配置只决定注册与否。体积收益只能来自阶段 3 的独立程序集 + 裁剪。

逐模块的 15 MB 级差值落在运行间噪声里（单次四个模块差值相加 70 MB，而全关只差 31 MB），因此本文不发布任何「哪个模块最贵」的排序；那需要 ≥5 次重复并报分位数。

## 服务端模块管理关联设计

“组件中心 → 服务端模块”的目标入口、启用集合、计划/操作协议、重启核实及实施跟踪由[服务端模块管理完整设计](RelaxKonOS.ServerModuleManagement.Design.md)统一规定。本文的包信任与模块边界继续有效；管理界面和新增写入路由均未因设计发布而实现。
