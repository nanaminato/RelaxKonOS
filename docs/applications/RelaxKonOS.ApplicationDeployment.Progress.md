# 容器化应用部署实施与测试进度

对应 [Goal](./RelaxKonOS.ApplicationDeployment.Goal.md) 与 [设计](./RelaxKonOS.ApplicationDeployment.Design.md)。更新日期：2026-09-19。

## 当前结论与续接入口

状态：设计与 M1–M5 全量实现完成（Server 与 Client 均编译通过，0 警告 0 错误）；**全部必需测试按用户要求跳过，原因：当前环境无 Docker**。

本轮交付：

- 协议冻结：`Shared/RelaxKonOS.Protocol/ApplicationDeployments/`（路由、严格 DTO、枚举、问题码）。
- Server 领域层：`RelaxKonOS.Server/ApplicationDeployments/`（三个 JSON 账本存储、密钥存储、运行时、代理集成、Manager、Coordinator、启动恢复）与 `RelaxKonOS.Server/Endpoints/ApplicationDeploymentEndpoints.cs`。
- 客户端内置应用：`Client/RelaxKonOS.Client/Apps/ApplicationDeployments/`（独立内置应用，与 Docker Manager 分离），含向导、观察、日志与三语文本。
- 设计文档与三语本地化（`Localization/{en-US,zh-CN,ja-JP}/application_deployments.json`，242 键 × 3 语言；`Tools/verify-localization.py` 通过，3238 键全语言一致）。

**未验证项：所有运行期行为均未在真实 Docker Engine 上验证。** 本文件不因此把任何实施项标记为已验收。

2026-09-20 修正：客户端"应用部署"打开即报"服务器未提供"应用部署"接口"（`application-deployment.http_404`）并非版本或服务未启动，而是服务端路由缺陷——`ApplicationDeploymentEndpoints` 把绝对常量 `Applications` 传给了非空前缀的 `MapGroup`，实际注册成前缀重复的 `/api/v1.0/application-deployments/api/v1.0/application-deployments/applications`，客户端请求 `/api/v1.0/application-deployments/applications` 得到 404。已改用相对常量 `ApplicationsPattern`；验证见表 B05。

下一步：在具备 Docker 的 Linux 宿主上执行 T01–T15，按本文证据格式补充命令、平台版本与结果；I12 平台验收依赖该环境。已知未实现范围见设计文档 §1.3（单服务 Compose 项目部署）。

## 记录规则

- 实施状态：待开始 / 进行中 / 已实现待验证 / 已验收 / 阻塞。
- 测试编写状态：待编写 / 编写中 / 已编写 / 不适用（注明手工用例）。
- 测试执行状态：未执行 / 执行中 / 通过 / 失败 / 阻塞；跳过必须注明原因，不计通过。
- 每次实施同步更新相关 ID，填写文件或提交、验证证据和下一步；只有实现与验收均完成才勾选。
- 编译、模拟测试、真实 Engine 集成和客户端端到端分别记录。失败保留原记录，修复重跑新增结果。
- 完成数使用“已验收项/必需项”，不以阶段权重估算百分比；未执行测试没有通过率。

## 实施清单

当前已验收：0/12（实现已完成 11 项，1 项阻塞于真实 Engine 环境）。所有实现项均停留在“已实现待验证”。

| ID | 阶段 | 交付项 | 状态 | 测试关联 | 实现证据 / 下一步 |
| --- | --- | --- | --- | --- | --- |
| I01 | M1 | Protocol、路由、严格 DTO、问题码 | 已实现待验证 | T01 | `Shared/RelaxKonOS.Protocol/ApplicationDeployments/`（6 文件：路由、契约、操作契约、枚举、问题码、请求）；请求 DTO 全部 `JsonUnmappedMemberHandling.Disallow`。2026-09-20 补齐 `ApplicationsPattern` 并修正服务端集合路由（B05）。待 T01 |
| I02 | M1 | 应用/版本/操作存储、原子提交 | 已实现待验证 | T02 | `ApplicationDeploymentCatalogStore`、`ApplicationDeploymentOperationStore`（含 `RetainedOperations=500`/`RetainedAuditRecords=2000` 有界裁剪）、`ApplicationDeploymentSecretStore`；临时文件+重命名+fail-closed。待 T02 |
| I03 | M1 | 后台操作、幂等、锁、取消与恢复 | 已实现待验证 | T03、T09 | `ApplicationDeploymentCoordinator`（`IHostedService`、`Idempotency-Key`+指纹、`MaximumConcurrentOperations` 门、按应用的资源锁集合、启动恢复 Running→Interrupted 且仅在副作用未越过临界区时可重试）。待 T03/T09 |
| I04 | M1 | 授权、审计、密钥引用和脱敏 | 已实现待验证 | T10 | `ApplicationDeploymentEndpoints` 两条策略 `ApplicationDeploymentsRead`/`ApplicationDeploymentsManage` + `RequireHostFeature`；审计只记操作者/目标/结果/operation ID，不落凭据；密钥以 DataProtection 保护正文、只上报版本。待 T10 |
| I05 | M2 | 镜像拉取、平台校验与身份固定 | 已实现待验证 | T04 | `ApplicationDeploymentRuntime` 预检与创建路径；`ApplicationDeploymentMapper` 从真实容器状态派生 `ActualState`，引擎不可用时为 `Unknown`；`DescribeDrift` 区分意图与实际。待 T04 |
| I06 | M2 | 单服务 Compose、标签与漂移识别 | 部分实现 | T08、T11 | 资源归属、标签化与漂移识别已实现（`DriftContainerMissing`/`DriftUnownedResource`/`DriftContainerExternallyModified`）；**单服务 Compose 项目部署未实现**，改用容器原语，已在设计文档 §1.3 披露。待 T08/T11 |
| I07 | M3 | 上传、解压、上下文与清理限制 | 已实现待验证 | T05 | `/uploads`（`DisableAntiforgery`、有界暂存）与 `/file-references`；问题码覆盖越界路径、链接、条目数、展开总量与过期引用。待 T05 |
| I08 | M3 | Java JAR/.NET 发布包模板 | 已实现待验证 | T06 | 模板契约 `ApplicationDeploymentTemplateDto` 携带 `RequiresArchive`/`RequiresImageReference`/`SupportsSelfContained`/`DefaultContainerPort`；入口与参数按模板校验、数组表达。待 T06 |
| I09 | M3 | Python 锁定依赖与启动模板 | 已实现待验证 | T07 | `ApplicationSourceKind.PythonProject` 强制 `ProgramEntry`；构建在镜像内完成（`DeploymentStage.Building`，进度按阶段内工作量，无分母则为 null）。待 T07 |
| I10 | M4 | 就绪检查、版本回滚、可选代理 | 已实现待验证 | T08、T12 | `DeploymentStage.HealthChecking` + HTTP/进程两级就绪；停机替换策略在 UI 明示；回滚复用已发布修订；`ApplicationDeploymentProxyIntegration` 增删单条 `/` 路由并以 `TestConfigurationAsync` 校验后再保存。待 T08/T12 |
| I11 | M4 | 客户端向导、观察、日志与三语文本 | 已实现待验证 | T13 | 客户端内置应用（7 步向导、列表/详情、修订/操作/日志三页签）；长操作以持久化 operation 轮询而非等待 HTTP 请求；`application_deployments.json` 三语 242 键。待 T13 |
| I12 | M5 | 平台验收、帮助文档、Goal 收口 | 阻塞 | T14、T15 | 阻塞条件：无 Docker 环境。设计文档与三语文本已就位；平台证据与 Goal 关闭需真实 Engine。 |

## 必需测试矩阵

当前：0/15 项执行通过；**全部 15 项跳过，原因：当前环境无 Docker**。跳过不计通过。每行是一组验收场景，不代表一个测试方法；拆分后须保留原 ID 与子用例关联。

| ID | 层级 | 场景与预期 | 编写状态 | 执行状态 | 证据 |
| --- | --- | --- | --- | --- | --- |
| T01 | 单元/契约 | DTO 往返、未知字段、非法入口/端口/资源/引用拒绝，全部调用方使用新契约 | 待编写 | 跳过（无 Docker 环境） | — |
| T02 | 单元/存储 | 版本不可变、状态原子写入、损坏账本明确失败、密钥正文不落账本 | 待编写 | 跳过（无 Docker 环境） | — |
| T03 | 集成 | 幂等重试只创建一次，同键异请求冲突，同应用变更互斥、跨应用安全并行 | 待编写 | 跳过（无 Docker 环境） | — |
| T04 | Engine 集成 | 镜像部署成功；认证失败、断网、镜像不存在、OS/架构不匹配明确失败；固定镜像身份 | 待编写 | 跳过（无 Docker 环境） | — |
| T05 | 单元/集成 | 越界路径、链接、解压炸弹、超限、过期/跨身份文件引用拒绝；暂存清理不伤其他资源 | 待编写 | 跳过（无 Docker 环境） | — |
| T06 | Engine 集成 | Java JAR、.NET Web/Worker 发布包运行；错误入口与不匹配运行时诊断；不调用宿主工具链 | 待编写 | 跳过（无 Docker 环境） | — |
| T07 | Engine 集成 | Python Web/Worker 构建运行；锁定依赖、安装失败与入口失败；重启不再安装依赖 | 待编写 | 跳过（无 Docker 环境） | — |
| T08 | Engine 集成 | 就绪成功/超时/崩溃；失败发布不激活；回滚恢复旧版本及配置，数据卷保留 | 待编写 | 跳过（无 Docker 环境） | — |
| T09 | 故障注入 | 拉取/构建/创建/激活阶段取消、断客户端和重启 Server；不重复副作用、不残留永远 Running | 待编写 | 跳过（无 Docker 环境） | — |
| T10 | 安全集成 | 越权查询/日志/取消拒绝；密钥和凭据脱敏；无特权或 socket 注入；构建上下文不泄露宿主文件 | 待编写 | 跳过（无 Docker 环境） | — |
| T11 | Engine 集成 | 资源限制、日志轮转、宿主重启恢复；识别外部修改/删除，清理仅处理本应用受管资源 | 待编写 | 跳过（无 Docker 环境） | — |
| T12 | 站点集成 | 无代理可部署；回环端口/容器网络正确；证书绑定；代理校验或重载失败恢复旧配置 | 待编写 | 跳过（无 Docker 环境） | — |
| T13 | 客户端 E2E | 四种来源向导、错误展示、任务重连、日志和回滚；三语完整；真实进度与未知进度区分 | 待编写 | 跳过（无 Docker 环境） | — |
| T14 | 平台验收 | 无应用工具链的 Linux 宿主完成镜像、Java、.NET、Python 全流程；记录实际平台版本 | 待编写 | 跳过（无 Docker 环境） | — |
| T15 | 故障/运维验收 | 磁盘不足、端口冲突、Daemon 不可用、旧镜像/密钥缺失和恢复失败可诊断；卸载保留数据 | 待编写 | 跳过（无 Docker 环境） | — |

## 环境与验证记录

环境 ID 应记录 OS/架构、Docker Engine/Compose、Server/Client 提交与测试 SDK 版本、宿主应用工具链存在情况。记录环境信息时不得附带凭据。

| 环境 ID | 平台与版本 | 用途 | 可用性 | 备注 |
| --- | --- | --- | --- | --- |
| E01 | 当前开发工作区（Windows，win32 宿主），.NET SDK 10.0.400 | 构建与静态校验 | 可用 | 仅用于编译与本地化校验；**不推定 Docker 可用** |
| E02 | 隔离 Linux 宿主，发行版/架构待登记 | 真实 Engine 与无宿主工具链验收 | 待准备 | T04–T12、T14、T15 必需；缺失是当前跳过原因 |

| 日期 | 运行 ID | 提交/变更 | 环境 | 测试 ID/命令 | 结果 | 证据位置/后续动作 |
| --- | --- | --- | --- | --- | --- | --- |
| 2026-09-19 | — | Goal 与 Progress 文档初始化 | — | 无产品测试 | 未执行 | 先完成 M1；文档检查不计功能验收 |
| 2026-09-19 | B01 | 协议冻结 + Server 领域层与端点实现 | E01 | `dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj -c Debug -p:UseSharedCompilation=false` | 通过（0 警告 0 错误） | 编译通过不等于验收；仅证明可构建 |
| 2026-09-19 | B02 | 客户端内置应用实现 | E01 | `dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj -c Debug -p:UseSharedCompilation=false` | 通过（0 警告 0 错误） | 同上；运行期行为未验证 |
| 2026-09-19 | B03 | 三语本地化新增 | E01 | `python Tools/verify-localization.py` | 通过（3238 键全语言一致） | 仅证明键一致性，不证明界面文本正确性 |
| 2026-09-19 | B04 | 全解决方案构建 | E01 | `dotnet build RelaxKonOS.sln -c Debug -p:UseSharedCompilation=false` | 通过（0 警告 0 错误） | 同上 |
| 2026-09-19 | — | T01–T15 | E02 缺失 | 见测试矩阵 | 全部跳过 | 跳过原因：当前环境无 Docker；不计通过 |
| 2026-09-20 | B05 | 修正集合路由：服务端改用相对常量 `ApplicationsPattern`（`ApplicationDeploymentEndpoints.cs`）、协议新增该常量并登记命名约定（`RelaxKonOS.Protocol.md` §5） | E01 | `dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj -c Debug -p:UseSharedCompilation=false`；再以临时程序调用 `MapApplicationDeploymentEndpoints()` 枚举真实路由表 | 通过（0 错误 / 2 条既有 CA1416 警告；枚举出的 19 条路由与 Design §3.1 及客户端常量逐条一致，`GET/POST /api/v1.0/application-deployments/applications` 不再带重复前缀） | 路由表输出见本行说明；不证明运行期行为（无 Docker），仅证明路径注册正确 |

每次测试运行补充：实际命令或手工步骤、预期与实际、通过/失败/跳过数、退出码（适用时）、脱敏日志或报告路径。只写“测试通过”不构成证据。

## 问题与阻塞

| ID | 影响项 | 问题 | 状态 | 解决条件 |
| --- | --- | --- | --- | --- |
| B-01 | T01–T15、I12 | 当前环境无 Docker Engine，无法执行真实 Engine 集成、故障注入与平台验收 | 阻塞 | 提供具备 Docker 的隔离 Linux 宿主（E02），登记发行版/架构/Engine 版本 |
| B-02 | I06 | 单服务 Compose 项目部署未实现，仅支持容器原语（见设计文档 §1.3） | 待决策 | 确认是补做 Compose 适配，还是把该项移出第一阶段范围并同步修订 Goal §2 |
| B-03 | 全部实现项 | 所有运行期行为未经真实环境验证，不应视为具备应用部署能力 | 已知限制 | 完成 T01–T15 并留下可复核证据 |
| B-04 | I01、I11 | 集合路由曾用绝对常量注册于非空前缀 `MapGroup`，实际路径前缀重复；客户端 `GET /applications` 与 `POST /applications` 恒为 404，界面显示"服务器未提供"应用部署"接口"，掩盖了真实原因 | 已解决（2026-09-20，见 B05） | 已改用 `ApplicationsPattern`；同类问题由 Protocol §5 的命名约定约束，T01 需覆盖"全部调用方使用新契约" |

## 变更日志

- 2026-09-19：建立第一阶段范围、I01–I12 实施清单、T01–T15 测试矩阵和证据记录格式；源码/Git 构建及自动切流保留后续范围。
- 2026-09-19：完成设计文档与 M1–M5 全量实现（协议冻结、Server 领域层与 HTTP 端点、客户端内置应用、三语文本）；记录 B01–B04 构建与本地化校验证据；T01–T15 全部跳过（无 Docker 环境）；登记阻塞 B-01–B-03。
- 2026-09-19：修正静态审查发现的发布语义：.NET `runtimeOptions` 解析、修订快照回滚、候选接管时的旧实例恢复保留、容器/卷精确所有权标签校验、阶段取消与饱和时的幂等重试；Python 要求锁定依赖。真实 Docker 验收仍未执行。
- 2026-09-20：修正 B-04——`ApplicationDeploymentEndpoints` 的集合读/写端点改用相对常量 `ApplicationsPattern`（新增于 `ApplicationDeploymentApiRoutes`），消除 `MapGroup` 前缀重复导致的恒 404；在 `RelaxKonOS.Protocol.md` §5 与设计文档 §3.1 补充"绝对常量仅客户端、`MapGroup` 内只用 `*Pattern`"的约定与本次实例；记录验证 B05。运行期行为与 Docker 验收仍未验证。
