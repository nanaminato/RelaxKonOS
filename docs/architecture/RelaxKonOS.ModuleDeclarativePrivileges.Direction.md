# 第三方模块声明式特权能力：下一阶段方向

> 状态：方向提案，2026-11-01。DC-01 契约冻结已实现；DC-02 的 apt 包事务已接线并在 Ubuntu 26.04 实机通过安装/漂移/来源撤销/移除验收（mysql-server 与 redis-server），Windows 适配器、共享依赖与外部安装接管仍未交付；DC-03～DC-06 未开始。当前可执行的提权为 `service.manage` 的启动/停止/重启与 `package.manage` 的安装/更新/修复/卸载（仅 apt 且 Helper 可解析时），其余能力类别可声明但执行时明确拒绝。本文不把设计目标当作已交付证据。
>
> 本文拥有声明式能力扩展方向；[Broker 设计](RelaxKonOS.ServerModulePrivilegeBroker.Design.md)继续拥有身份、确认、计划、审计和撤销机制。[受管运行时设计](../platform/RelaxKonOS.ManagedRuntimes.Design.md)继续拥有安装生命周期、资源所有权、来源信任和恢复规则。

## 1. 需要解决的问题

如果第三方模块每增加 MySQL 安装、PostgreSQL 卸载或数据库密码恢复，都必须先给 Core/Helper 新增一个应用专用操作，那么第三方无法独立交付功能。固定业务白名单只能作为早期交付范围，不能成为模块生态的最终能力模型。

下一阶段采用：**通用能力类别 + 模块声明的资源范围 + Host 解析的精确计划 + 用户授权 + Helper 独立复核**。

模块声明需求，不能声明自己已获授权。Host 需要认识软件包、服务和文件等能力类别，不必为每个应用编写一套 Core 分支。出现新的系统能力类别、平台适配器或无法表达的特殊操作时，仍需要扩展宿主；不承诺有限声明语言覆盖所有程序行为。

首个服务动作切片对应 `service.manage`；它是一次能力类别的第一次落地，不是永久的逐应用白名单。

## 2. 两层授权与职责

| 层次 | 用户批准什么 | 不意味着什么 |
| --- | --- | --- |
| 能力范围授权 | 哪个模块版本可申请哪些包、服务、目录、网络或秘密资源；允许的平台和风险等级 | 不等于批准该范围内所有未来变更 |
| 具体操作确认 | 当前精确版本、依赖闭包、文件差异、服务中断、数据处理和不可逆影响 | 不允许执行时重新解析为另一目标或追加步骤 |

默认第三方模块每次确认。用户可明确授予有限常设许可，绑定操作种类、资源范围、风险上限、有效期和模块摘要。删除数据、扩大权限、执行高信任扩展等操作默认要求新的具体确认；免密码的管理员验证记忆不能代替确认。

模块更新、发布者变化、权限扩大或制品摘要变化时旧授权失效；撤销立即生效。后台任务没有适用常设许可时明确拒绝，不制造无人处理的待确认任务。

职责分配：

- 模块：提交类型化声明、业务意图和原因，展示业务结果；不提供可信的最终执行步骤。
- Core Broker：结合当前主机事实、资源所有权、Host 策略与信任状态生成计划，拥有确认界面和批准记录。
- 平台适配器：解析包事务、服务依赖和文件变更；生成 Host 定义的类型化执行步骤，不输出供模块拼接的 shell。
- Helper：依据受保护策略、能力授权和精确计划独立复核目标及前置状态，再执行封闭的能力类别。不能只相信 Server 传入的路径、摘要或模块清单。

## 3. 能力目录

以下是目标目录，逐类实现和验收，不表示清单中的能力现在可用。

| 类别 | 模块可声明的资源范围 | Host/Helper 必须解析和约束 |
| --- | --- | --- |
| `package.manage`：软件包安装、更新、修复、卸载 | 包管理器、已授权来源、包名、版本范围、目标平台 | 冻结精确版本/架构/来源及可取得的制品身份、依赖闭包、安装脚本、服务影响和卸载集合 |
| `service.manage`：服务管理 | 精确服务标识、允许动作、模块拥有或明确授权的外部服务 | 启停/重启/重载、开机启动、依赖影响；声明服务定义时使用固定字段，不能夹带任意启动命令 |
| `file.manage`：文件与配置 | 精确路径或限定根、允许动作、大小、属主和模式范围 | 规范化、链接/竞态防护、内容摘要与差异、原子发布、秘密脱敏和补偿 |
| `data.manage`：数据生命周期 | 安装实例所属数据与备份资源、保留/恢复动作 | 所有权、备份验证、数据格式约束；删除数据必须独立确认 |
| `account.manage`：OS 账户与 ACL | 模块所属专用账户、组、路径和可授予权限 | 禁止任意加入管理员组或修改无关账户；权限扩大单独确认 |
| `network.manage`：网络与防火墙 | 结构化接口、地址/端口范围及模块所属规则 | 实际规则差异、冲突、连通性风险和恢复措施 |
| `secret.reference`：证书与秘密引用 | 指定用途、目标实例、最小访问权限和有效期 | 安全生成、存储、轮换、撤销与受控交付；清单、计划和日志不包含秘密值 |
| `setting.manage`：定时任务、系统设置与 Windows 注册表 | 模块所属任务、Host 定义的设置键或限定注册表根 | 固定结构、平台支持、依赖影响；不能借任务或设置字段绕过受限执行边界 |
| `app.manage`：应用管理协议 | 实例身份、账户范围和受支持的类型化动作 | 有适配器时使用数据库/管理协议；普通业务访问凭据和 OS 管理员验证分别处理 |

声明语言只允许宿主支持的类型、枚举、范围和引用，拒绝任意脚本、自由文本命令、调用者指定的提权账户，以及借环境变量、工作目录、钩子或服务定义注入执行程序。未知能力或不支持的平台明确拒绝，不退化为通用 shell。

清单中的能力声明使用稳定的类别标识与类型化资源对象，不使用自由文本；未知字段、未知类别、越界范围和平台不支持都要在安装/信任阶段拒绝，而不是留到执行时才失败。

## 4. 软件安装、卸载也是可声明能力

MySQL 模块应能声明“管理指定已授权仓库中的 mysql-server，控制 mysql.service，维护指定配置和所属数据目录”。Host 支持相应包管理器和通用能力后，新增模块通常无需修改 Core。

以下为概念示例，**不是当前可解析的 manifest 或已冻结 API**：

```yaml
capability: package.manage
resources:
  manager: apt
  sourceRef: host-approved-mysql-repository
  packages: [mysql-server]
  versionRange: "Host 支持的版本范围表达式"
actions: [install, update, repair, remove]
dataPolicy: preserve-by-default
```

`sourceRef` 必须解析到 Host 已独立批准的来源；模块不能通过清单中的 URL、公钥或摘要给自己的仓库授信。传递依赖不能仅靠模块写一个通配符就获得许可：Host 展示解析出的完整依赖闭包，并检查来源、冲突、风险与用户批准范围。

具体事务要求：

1. 从当前包数据库及受信任元数据计算计划，冻结包名、版本、架构、来源、可验证制品身份、依赖与连带移除集合。
2. 展示 maintainer scripts/安装钩子、可能中断的服务、配置和数据处理，以及可回滚程度。包安装脚本可能获得 root，这份信任不能被“声明式”字样掩盖。
3. 执行前复查包数据库、信任、制品和资源修订。变化时返回计划漂移，不静默重新求解；不能冻结或验证的目标明确报告不支持或要求重新规划。
4. 安装、更新、修复、回退和卸载共用实例锁、持久操作记录和恢复机制；同版本修复不算更新。
5. 外部已有安装必须明确授权接管。共享依赖记录引用和影响，不因卸载一个模块就删除其他应用仍使用的资源；禁止全局 purge/autoremove。
6. 默认保留配置与数据；删除数据单独生成计划和确认。没有实际补偿能力时如实标记不可逆，失败进入待恢复，不伪造通用回滚。

Windows MSI/其他安装器的可支持范围须单独定义和实测；安装器属性、脚本和自定义动作不能变成任意参数旁路。Linux 包管理器的结果不代替 Windows 验收。

## 5. MySQL 密码操作示例

应区分两种业务流程：

- **已有管理凭据的改密**：模块使用已授权数据库管理协议完成；凭据通过实例限定的秘密引用交付，不因此获得 OS root。若需要 Host 代理的类型化动作，再实现对应协议适配器。
- **遗失管理凭据后的恢复**：可能涉及停止服务、受限的恢复启动、数据库操作、恢复正常配置和健康核实。业务工作流组合已有能力；特殊启动或协议操作没有适配器时明确不支持，不能把字符串藏在服务动作或文件写入中执行。

用户应看到目标实例和账户、停机影响、恢复阶段、秘密变更与失败后的人工恢复方式。新密码及数据库管理凭据只通过目的限定的秘密通道传递，不出现在 manifest、计划步骤、命令行、普通回执或日志中。

## 6. 特殊能力与高信任扩展

对确实无法用既有能力表达的操作，保留模块携带受审查扩展程序的后续机制。它必须与普通能力授权分开，明确显示程序身份、发布者、摘要、参数 schema、秘密输入通道、运行身份、权限范围、工作目录、环境、网络、超时和资源限制。

此机制不允许把 shell、解释器或包管理器作为自由文本命令入口。运行权限应尽量限定；无法约束程序行为、必须完整 root/LocalSystem 时，明确标为主机级代码信任，由部署策略决定是否允许，不能声称签名或哈希提供行为隔离。

即使是签名程序，也可能恶意或有缺陷；签名和摘要证明身份与字节一致，不证明安全。普通声明式流程缺少适配器时不自动升级到高信任执行。

## 7. 确认、执行与审计

确认页面由 Core 拥有，用主机解析结果展示变化，不把模块提供的说明当作执行事实。计划至少绑定模块/发布者/制品、发起用户与会话、能力授权修订、来源信任、资源与主机状态修订、精确步骤、影响、有效期和 `planHash`；秘密仅绑定受控引用及修订。

批准令牌一次性使用，绑定计划、用户、会话与模块；管理员验证与计划确认分别校验。Helper 复核原计划和当前状态后执行，不重新规划或接收任意命令。接口升级同步更新 Protocol、SDK、Server、Helper、客户端、工具和示例，遵守仓库唯一当前契约政策，不增加双格式解析。

准备、批准、执行和结果持久化并关联同一 operationId。执行中断或响应丢失时核对原操作；证据不足进入待恢复，不自动重放变更。撤销阻止尚未执行的步骤；执行中事务在安全边界停止或补偿，不能承诺撤销会逆转已生效操作。

## 8. 实施顺序与验收门槛

当前 Broker/隔离/v2 安装工作是前置基础；本表是待实施方向，不以已有服务动作切片代替完成。

| 编号 | 后续交付 | 关闭条件 | 状态 |
| --- | --- | --- | --- |
| DC-01 | 冻结能力类别、资源约束、两层授权、秘密引用及平台支持模型 | Protocol/SDK/manifest/Helper 策略与确认 UI 一致；拒绝未知字段和越权声明 | 已实现（契约冻结，见 §9） |
| DC-02 | 首个通用包管理适配器，贯通安装/更新/修复/卸载 | 用 MySQL 与另一个第三方软件包验收，新增模块无需新增应用专用 Core 枚举；依赖漂移、来源撤销、脚本风险、共享资源和外部安装均有证据 | 进行中（apt 已实机验收 mysql-server 8.4.11 + redis-server 5:8.0.5；Host 冻结/派发、Drift 与来源撤销拒绝已实现；共享依赖、外部安装接管、脚本差异与 Windows MSI 未交付） |
| DC-03 | 通用服务、配置和数据生命周期；组合工作流 | MySQL 配置发布与保留数据卸载闭环；无脚本旁路，失败可解释且能恢复 | 未开始 |
| DC-04 | 数据库管理协议与秘密交付；密码恢复能力评估 | 有凭据改密实测；丢失凭据恢复须完成所需适配器、停机确认、秘密保护和恢复验收后才可宣称支持 | 未开始 |
| DC-05 | 常设许可、撤销和后台执行 | 范围、有效期、更新失效、撤销竞态、数据删除强制确认全部验证 | 未开始 |
| DC-06 | 高信任扩展评审与可选实现 | 威胁模型、平台可实现的隔离、独立授权及失败终止实测；未实现时保持明确拒绝 | 未开始 |

所有阶段覆盖未声明/目标越界、伪造来源、计划漂移、过期/重放/并发批准、用户拒绝、权限撤销、模块更新扩权、Helper 不可用/重启、执行中断和恢复。每项记录实际平台与 System/User Mode，未测组合明确标注。User Mode 不提供宿主特权时返回不支持，不降级绕过。

## 9. DC-01 当前契约（2026-11-01）

已冻结并实现的部分：

- **清单**：模块清单升级为 `schemaVersion=3`，`privileges` 改为能力类别数组，每项带 `kind`、`platforms` 和类型化资源对象。九个类别全部可声明；未知类别、未知字段、类别外动作、重复声明、空范围以及与 `rid` 平台不一致的 `platforms` 都在读取清单时拒绝。
- **共享目录**：`ModuleCapabilityCatalog` 是 Protocol 中唯一的类别/动作/可执行矩阵来源；manifest 校验、Broker、桌面客户端和测试共用它，不各自维护白名单。
- **两层授权**：安装预览把每项声明投影为类别、平台、资源摘要和 `Executable`；代码信任确认的是精确版本和该声明。每次执行仍需单次确认，Broker 按声明的资源范围（服务 + 动作、包 + 动作、根路径前缀等）匹配请求，不再做字符串全等比较。声明 `package.manage` 等类别不等于可以执行：包类别还要求 Helper 能按来源策略解析并冻结精确制品。
- **秘密引用**：`ModuleSecretReference(purpose, target, permission)` 形状已冻结；计划与界面只出现引用，绝不出现秘密值。交付适配器（DC-04）实现前，携带非空引用的请求返回 `module.privilege_unsupported`。
- **平台模型**：`platforms` 取 `linux`/`windows`/`macos`，必须包含清单 `rid` 对应的平台；运行时不匹配的声明不会进入计划。
- **执行矩阵**：`service.manage` 的 `start`/`stop`/`restart` 映射 Helper `NativeServiceAction`；`package.manage` 的 `install`/`update`/`repair`/`remove` 映射 Helper `ModulePackageApply`，但只在 Helper 能按 root 拥有的来源策略解析出精确制品时成立，否则保持 `module.privilege_unsupported`。其余类别明确 unsupported，不退化为 shell，也不要求逐应用新增枚举。
- **存储**：安装清单、`package-plans.json` 为 schemaVersion=3，`privilege-plans.json` 为 schemaVersion=5，`privilege-consents.json` 为 schemaVersion=2；旧格式拒绝读取，不做静默迁移。

验证：`dotnet build RelaxKonOS.sln` 通过；ServerModules 定向回归覆盖类别/动作越界、未交付类别拒绝、秘密引用拒绝、声明平台不符、计划漂移与升级后旧存储拒绝。DC-01 的类别目录不因 DC-02 接线而扩大到 `file.manage`、`data.manage` 等未交付适配器；它们仍只可声明、不可执行。

## 10. 待实施边界

上表其余阶段仍未实现：DC-02 的 apt 适配器已接线，但真实 Ubuntu 安装验收与 Windows 适配器范围仍未交付；DC-03/DC-04 需要配置、数据与数据库协议适配器；DC-05 需要把常设许可扩展到新类别；DC-06 只在有威胁模型和隔离实测后再评估。在此之前，任何未列出的命令、脚本或逐应用旁路都不属于本方向允许的实现方式。

### DC-02 第一层：Helper 解析与执行（2026-11-01）

- Protocol 冻结 `ModulePackageTransactionAction`、`ModulePackageArtifact`、`ModulePackageResolution`、`ModulePackageTransaction`：一个事务只含动词、Host 来源引用和冻结制品身份，没有 argv、shell、环境变量或仓库 URL。
- `AptPackageResolution` 增加模块包解析：`TryBuildModuleResolution` 复用 apt 的 policy/`--print-uris` 两份只读答案，按调用方给定的包集合冻结版本、架构、仓库、制品 URI、SHA-256、大小和依赖闭包；第三方来源要求 SHA-256，拒绝只有 MD5 的索引。
- `MatchesModuleResolution` 在执行前比对版本、架构、仓库、制品 URI、摘要、大小与整个依赖闭包；`IsUsableModuleResolution` 拒绝路径穿越文件名和带凭据的仓库 URI。
- 定向检查 15 项通过，覆盖解析、冻结、包集合越界、MD5-only 拒绝、漂移拒绝、非法身份拒绝，以及计划展示投影（精确包/版本/架构、制品摘要、依赖数量、保留配置与 purge 的警告差异）。
- Helper 侧只读解析已实现：`ModulePackageQuery` 动词只接受来源引用和包名，读取 root 拥有的 `/etc/relaxkonos/module-packages.json`（非 root 所有、链接或可被 group/other 写入即视为无来源），再通过 `apt-cache policy` 与 `apt-get --print-uris` 独立求解，要求解析出的仓库与整个依赖闭包都在该来源批准范围内。它不下载、不安装、不修改任何包状态。

  ```json
  {
    "schemaVersion": 1,
    "sources": [
      {
        "sourceRef": "host-approved-mysql",
        "baseUris": ["http://archive.ubuntu.com/ubuntu"],
        "packages": ["mysql-server"]
      }
    ]
  }
  ```

  该文件是 camelCase、大小写敏感、拒绝未知字段的唯一当前格式；Host 与测试共用同一解析器。2026-11-01 的真实验收发现旧加载器缺少 camelCase 命名策略，实际只能读取 PascalCase，文档示例因此从未真正可读；现在 `TryParse` 明确按文档格式解析，并加入“改 schemaVersion / 未知字段 / PascalCase 均拒绝”的回归检查。

以上只是解析与执行层；Host 侧计划生成、冻结与派发见下一节。解析层存在不等于 `package.manage` 可执行：没有 Helper 解析就没有计划。

Helper 的 `ModulePackageApply` 执行动词已实现：先查持久回执（同一操作绝不重解、不重跑），再按同一只读路径独立重解并比对。主制品必须完全一致；apt 现在仍要下载的每个依赖必须属于冻结闭包，已安装而不再下载的冻结依赖不算漂移，计划中从未出现的制品才算漂移；不一致即返回 `PlanChanged`。执行前写入持久回执，中断或响应丢失后同一操作返回 `Conflict` 待恢复而绝不自动重放；执行 `apt-get install pkg=version`、`--reinstall` 或 `remove [--purge]` 后再次用 `dpkg-query` 核实安装状态与版本/架构。

### DC-02 已接线的 Host 侧（2026-11-01）

1. `ModuleCapabilityCatalog.IsExecutable` 把 `package.manage` 纳入 Host 适配器矩阵；`CoreModulePrivilegeBroker` 在 prepare 时调用 Helper `ModulePackageQuery`，Helper 不可用、来源未批准、包不在来源策略内或解析失败一律使计划创建失败（`module.privilege_unsupported`），不会退化为仅凭声明创建计划。
2. 计划创建时冻结完整 `ModulePackageResolution`（包名、精确版本、架构、仓库、制品 URI、SHA-256、大小、依赖闭包）与来源引用；`privilege-plans.json` 升为 **schemaVersion=5**，旧格式拒绝读取且不迁移。`planHash` 绑定冻结身份的独立 `packageHash`，持久化记录缺失、串类别或摘要不符即 `module.privilege_recovery_required`。
3. 计划 DTO 新增 `ModulePackagePlanIdentity`（含来源引用、仓库、制品文件名/摘要/大小与依赖文件清单）；桌面确认页展示这些字段以及 `ModulePackageDescription.Warnings` 的维护者脚本、服务影响与“无通用回滚”警告。客户端拒绝“包计划无冻结身份/服务计划带包身份”的响应。
4. approve 把来源引用与冻结身份作为 `ModulePackageApply` 发出；Helper 先按 operationId 查持久回执（重放/中断不会被重新求解），再重解并比对主制品与仍将下载的依赖。不一致、来源或仓库被撤销时返回 `PlanChanged`，Broker 映射为 `module.privilege_plan_changed` 并把计划持久化为 `Failed`（已知未执行），不进入 `RecoveryRequired`；响应丢失仍进入 `RecoveryRequired`，不自动重放。已冻结但已安装的依赖不会被要求重新下载，因此第二次安装不会因闭包变小而误判漂移。
5. 模块更新、信任撤销、来源目录/策略修订变化会使旧计划的 `identityHash` 失效；Helper 在 apply 时再按当前 root 策略复核来源与制品，因此 Host 侧策略修订与 Helper 侧来源撤销都会让旧计划不可执行。常设许可仍只允许 `service.manage`（`ModuleCapabilityPolicy.AllowsStandingConsent`），包类别的后台许可留到 DC-05。
6. 版本范围按 Debian 版本序（epoch/upstream/revision）与声明区间比较；解析版本落在声明区间之外时拒绝计划，Windows MSI 声明仍返回 `unsupported`。

验证：`dotnet build RelaxKonOS.sln` 通过；`Tests/ServerModules --module-privilege-checks` 覆盖包计划冻结、区间拒绝、类别/动作越界、冻结身份篡改拒绝、v5 旧格式拒绝、approve 只发送冻结身份、Helper Drift→`module.privilege_plan_changed`、响应丢失→`RecoveryRequired`；`--module-package-transaction-checks` 覆盖 apt 身份解析、依赖子集匹配、策略文件 camelCase 解析、漂移拒绝与 Debian 版本序。

### DC-02 真实 Ubuntu 验收（2026-11-01，已执行）

在 Tailscale 测试主机 `100.113.194.85`（Ubuntu 26.04 LTS，x86_64，2 vCPU / 5.2 GiB）以 root 运行自包含 linux-x64 验收二进制；命令与原始输出保存在仓库外的 `.artifacts/module-package-ubuntu-acceptance-20261101.log`：

```bash
RelaxKonOS.ServerModules.Tests --module-package-linux-acceptance \
  --source-ref host-approved-mysql --base-uri http://cn.archive.ubuntu.com/ubuntu \
  --package mysql-server --package redis-server
```

结果：两个软件包各 9 项检查全部 PASS，真实 `apt install` 得到 `mysql-server 8.4.11-0ubuntu0.26.04.1 amd64`（依赖闭包 8 项）与 `redis-server 5:8.0.5-1ubuntu0.1 amd64`（依赖闭包 2 项）；同一 operationId 重放被 `Conflict` 拒绝；摘要漂移与来源撤销均被 `PlanChanged` 拒绝且未执行；`apt remove` 后 `dpkg-query` 核实不存在。验收后已 purge 本次新增的全部包、删除验收载荷与 `/etc/relaxkonos`、`/var/lib/relaxkonos/module-packages`，nginx 等既有包未受影响。

本次验收发现并修复两处真实缺陷：① 策略加载器缺少 camelCase 命名策略，文档示例格式实际不可读；② apply 时把“依赖已安装、apt 不再列出”误判为闭包漂移，导致同一包第二次事务被拒绝。现以主制品完全一致 + 仍将下载的依赖属于冻结集合的规则判定，并先查回执再重解。

### DC-02 尚未交付（下一步）

1. **Windows 适配器未定义**：`windows-msi` 声明仍可解析，但执行时返回 `unsupported`；安装器属性、脚本和自定义动作的封闭参数模型须单独立项和实测。
2. **维护者脚本与共享资源展示仍是静态警告**：Host 不解析 maintainer script 内容，也不计算共享依赖引用/影响；“外部已有安装接管”“卸载集合”“安装脚本差异”尚未生成可确认的数据。Linux 上的安装脚本风险因此只以“可能以宿主权限运行”的警告呈现，不构成已核实的脚本差异证据。
3. **真实 MySQL 服务与数据生命周期未验收**：本次只证明包事务，未启动/连接实例、未验证数据保留与卸载策略；DC-03/DC-04 才交付配置、数据与数据库协议闭环。

## 11. 关联文档

- [模块提权 Broker 设计](RelaxKonOS.ServerModulePrivilegeBroker.Design.md)
- [服务端独立模块分发设计](RelaxKonOS.ServerModuleDistribution.Design.md)
- [服务端模块包格式](RelaxKonOS.ServerModulePackage.Format.md)
- [受管运行时设计](../platform/RelaxKonOS.ManagedRuntimes.Design.md)
- [统一特权 Helper 架构](privileged-helper.md)
- [v2 实施记录](RelaxKonOS.ServerModuleV2.Progress.md)
