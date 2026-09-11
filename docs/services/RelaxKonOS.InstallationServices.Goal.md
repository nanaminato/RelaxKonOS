# RelaxKonOS 受管安装服务 Goal

> 状态：功能实现中；本文直接取代原 SMB 专用“安装进度与后台任务 Goal”
>
> 建立日期：2026-09-11
>
> 适用范围：RelaxKonOS Server 管理的宿主级第三方服务、运行时及其安装、升级、修复与卸载任务。

本文定义所有受管安装服务的同一处理模型。它覆盖 SMB（Samba / Windows File Server）、Nginx、FRP Runtime（`frpc` / `frps`）、Mihomo、Docker Engine、Git CLI 及以后经审批加入的宿主级依赖。每个领域仍拥有自己的能力、安装器和安全边界；本 Goal 统一的是任务契约、调度、进度、恢复、授权和用户体验，**不是**把领域实现变成可传任意命令的通用执行器。

## 实现进度（2026-09-11）

- 已完成公共 `Installations` Protocol：冻结 service/kind/state/stage 枚举、`InstallationOperationDto`、统一路由和稳定 problem code；Nginx 请求已移除 `packageId`，公共安装入口不再接收客户端包标识。
- 已完成宿主级持久账本与协调器：原子写入、秘密/路径/原始输出不落盘、幂等请求、资源锁、启动恢复、取消边界及审计摘要均由 `InstallationOperationStore` / `InstallationCoordinator` 处理。
- 已完成统一授权 Endpoint 与 Client 轮询/工作区恢复组件；Nginx、Git、SMB、FRP、Mihomo、Docker 均已注册为强类型 `IInstallationService`，完成后会回读各自真实状态。
- 已迁移 Nginx/Git/SMB/FRP/Mihomo 的 Client 安装入口到统一 operation ID 路径；Nginx 的 Windows 安装由 Server 使用固定官方 ZIP 下载、受限暂存/解压和配置验证完成，已删除客户端 ZIP 上传、版本目录、下载 URL、旧安装/卸载路由及对应 UI。普通 Nginx 生命周期 operation 仍保留在其领域 API 中。
- 已删除 Git/SMB 的旧安装路由契约，以及 FRP/Mihomo 向 Client 暴露受管运行时下载 URL 的旧 DTO、路由和界面入口；固定发行物只在 Server 领域安装器内使用。
- 已删除 FRP/Mihomo 的“从 Server 文件归档安装”内部接口和文件选择器，避免路径或归档内容跨越统一安装服务边界。
- 已删除 FRP 旧进程内安装状态 DTO；现有运行时测试改为通过强类型阶段报告器驱动，不再断言另一套安装状态模型。
- Docker Linux 固定安装器已接入统一任务；Windows 仍只返回受支持的人工宿主操作方案，Windows Server 不会自动安装。
- 已删除 Docker 旧同步“安装计划”入口；Process Guardian 仅保留无副作用的手动部署计划，不再暴露看似可执行安装的 Endpoint。
- Docker Client 已接入统一任务的恢复与观察面板；部署环境验证完成前不提供新的自动执行按钮。

尚未关闭的功能项：Docker Client 的执行入口按 §8.5 要求，待目标部署环境验证后再开放，现有界面仅提供本地化安装指引。

### 本轮暂缓的验证

以下测试尚未在本轮补写或执行，必须在功能变更冻结前完成：账本序列化/损坏 fail-closed、跨身份可见性、幂等冲突、共享资源锁、启动恢复、取消边界、Helper NDJSON 损坏，以及每个领域的真实健康检测。还需在隔离 Ubuntu 与 Windows Server VM 完成 §8.6 所列网络、APT/dpkg、校验失败、重启和断线恢复场景。

当前环境中 `RelaxKonOS.Server` 可无还原构建；Client 与测试项目因本机缺少 .NET workload SDK 目录而在项目引用解析阶段失败（无编译诊断），故未将该环境问题误记为功能测试通过。

领域设计仍是功能语义的权威来源：[SMB Goal](./file-services/RelaxKonOS.FileServices.Smb.Goal.md)、[File Services 规格](./file-services/RelaxKonOS.FileServices.Specification.md)、[Nginx 设计](../applications/RelaxKonOS.WebServerManager.Design.md)、[FRP Goal](../applications/RelaxKonOS.FRP_Integration.Goal.md)、[代理管理器 Goal](../applications/RelaxKonOS.ProxyManager.Goal.md)和 [Docker 管理器设计](../applications/RelaxKonOS.DockerManager.md)。本文与它们冲突时，安装任务的公共契约、恢复和安全规则以本文为准。

## 1. 成功标准

- 任一受管安装、升级、修复或卸载请求均立即返回 `202 Accepted` 和持久 `InstallationOperationDto`；HTTP 请求、窗口和 Client 断开都不终止已提交任务。
- 统一查询与取消路由按 operation ID 返回真实状态；重连后可继续观察同一任务。无可信百分比时 `progress` 为 `null`，UI 只能显示阶段指示，不能用计时器或阶段权重伪造总百分比。
- 每个安装器只发布来自可信来源的阶段和进度：包管理器状态流、HTTP 已知 Content-Length、已校验文件复制字节数或其自身明确的系统 API 状态。配置、验证、激活、回滚等通常为不确定进度。
- 任务在 Server 重启后恢复为 `Interrupted` 或由领域恢复器给出明确结论，绝不永久显示 Running；操作、审计、日志和 API 响应均不含命令行、仓库 URL、配置、路径、归档内容、凭据或原始 stderr。
- 同一不可并行资源严格串行，重复提交用 `Idempotency-Key` 返回同一任务；不同服务可并行，除非声明共享包管理器、端口、运行时目录或系统重启资源。
- 安装完成后必须由领域检测器回读真实状态并刷新 Client；“进程退出 0”或“文件已经写入”均不足以代表安装成功。

## 2. 范围与登记表

| 领域 | 当前受管安装对象 | 可信进度来源 | 专属边界 |
| --- | --- | --- | --- |
| SMB | Linux 固定 `samba` 包；Windows Server 固定 `FS-FileServer` role | APT status fd；Windows role API 阶段（如不可用则为 `null`） | 仅固定资源；不接收包、role、仓库或命令输入 |
| Web Server | Nginx：Linux 系统包；Windows 经验证的官方 ZIP | APT status fd；Windows 下载/复制的字节数 | 只管理 RelaxKonOS 拥有的实例和配置片段 |
| Tunnels | 受管 FRP 发行包（同一版本同时提供 `frpc` / `frps`） | 已验证的下载/复制字节数、解压/健康/激活阶段 | 固定发行清单、SHA-256、归档白名单和不可变版本目录 |
| Proxy | 受管 Mihomo 发行包及必要的 Linux systemd service | 已验证的下载/复制字节数、解压/health/activate 阶段 | 固定清单、原子 active/previous 切换；TUN 仍遵循单独恢复事务 |
| Docker | Ubuntu Docker Engine；Windows 桌面引导或已探测运行时 | 平台安装器明确提供的阶段；无可靠值时 `null` | Windows Server 不自动安装；不传递 Docker daemon、镜像或 shell 参数 |
| Git | Linux 固定 `git` APT 包 | APT status fd | 不支持的平台只给出手动安装指引；不接收包名、仓库或命令输入 |

以下不因本文自动纳入：外部/用户自带 Nginx、FRP、Mihomo 或 Docker；应用包安装；证书申请和续期；镜像拉取；以及任何尚未有专属领域 Goal、固定资源清单、权限和恢复方案的组件。Process Guardian 当前仅提供签名部署包的手动安装计划，不运行宿主安装器，故不创建虚假的异步任务；其将来具备受管安装器后才可登记。要新增安装服务，必须先在本表增加条目，并同时提供 §6 的领域适配器、锁资源、稳定 problem code 和测试；不得以“通用安装”路径绕开这些要求。

## 3. 当前状态与直接迁移目标

仓库现有模型不能并存为长期 public contract：Nginx 已有可持久化的 `WebServerOperationStore`，但只传阶段字符串；Proxy 有 `ProxyOperationStore`；FRP 仍以进程内 `TunnelRuntimeInstallationDto` 展示状态；Git 仍同步返回 `GitEngineInstallResult`；SMB 的旧文档只定义了其专用 DTO；Docker 安装仍在设计中。它们必须直接升级到下列公共安装契约；项目尚未发布，不保留旧 DTO、旧状态路由、旧轮询入口、双格式解析或 adapter。

领域自身的普通操作不必迁移：例如 Nginx reload、Docker 容器操作、证书申请和 FRP tunnel 启停仍可使用其专属 operation API。只有“改变宿主上可管理服务/运行时是否存在、版本或受管安装布局”的安装生命周期进入本模型。

## 4. 公共协议与 API

在 `Shared/RelaxKonOS.Protocol/Installations/` 新建零依赖契约与路由常量，所有 Client、Server 和测试只引用这里：

```text
InstallationServiceId = Smb | Nginx | Frp | Mihomo | Docker | Git
InstallationOperationKind = Install | Upgrade | Repair | Uninstall
InstallationOperationState = Queued | Running | Succeeded | Failed | Cancelled | Interrupted
InstallationStage = Queued | Preflight | Preparing | UpdatingPackageLists |
                    Downloading | Copying | Verifying | Extracting | Installing |
                    Configuring | Activating | HealthChecking | RollingBack |
                    Completed | Failed | Cancelled | Interrupted

InstallationOperationDto =
  operationId, service, kind, state, stage, progress?, problemCode?,
  createdAt, startedAt?, completedAt?, cancellable
```

`progress` 是当前阶段的 `0..100` 整数，不是跨阶段累计值；`Downloading` / `Copying` 仅在总长度可信时带值。安装领域可定义额外的私有阶段，但必须映射为上述冻结枚举和本地化 message key，不能把命令文本、文件名或错误原文传给客户端。

```text
POST /api/v1.0/installations/{service}/{kind}       -> 202 InstallationOperationDto
GET  /api/v1.0/installations/{operationId}          -> 200 InstallationOperationDto
POST /api/v1.0/installations/{operationId}/cancel   -> 200 InstallationOperationDto
GET  /api/v1.0/installations/active?service={id}    -> 200 InstallationOperationDto | 404
```

每个 POST 均要求 `Idempotency-Key`，并只接受由该服务专属 DTO 表达的、已确认的选项；公共路由绝不接受 `command`、`arguments`、package/feature ID、仓库 URL、环境变量、路径或 base64 脚本。路由与调用方在一次 breaking change 中迁移；删除原有 Nginx、FRP、SMB 安装状态路由。

## 5. 调度、持久化与恢复

`InstallationOperationStore` 是宿主全局、secret-free 的唯一安装任务账本。它持久化 service、kind、operation ID、请求者安全引用、idempotency key 的不可逆引用、状态、阶段、可选进度、时间、problem code、取消能力和锁资源；不持久化请求秘密、下载 URL、文件路径、包管理器输出、配置或 stderr。写入采用原子替换，读取失败必须 fail closed 为稳定 problem code，不能重新执行未知任务。

`InstallationCoordinator` 依服务注册的资源声明加锁。SMB/Nginx/Docker/Git 的 Linux 安装同时声明 `linux:apt-dpkg`；FRP 和 Mihomo 对各自 runtime 根加锁；Nginx 对其实例加锁；Docker 对 engine 安装布局加锁。锁在预检前取得，任务结束或明确中断才释放。若同一资源已有活跃任务，等价 idempotency 请求返回原任务；不同请求返回稳定冲突问题码。

启动恢复规则如下：

1. 尚未调用领域适配器的 `Queued` 任务标记 `Interrupted`。
2. `Running` 任务先由领域恢复器做只读检测；可证明目标已健康则标记 `Succeeded`，可证明未完成则 `Interrupted`，无法判断则 `Failed` 并返回专属恢复问题码。
3. 不自动重放下载、APT、Windows role 安装、删除或激活操作。用户以新 idempotency key 重新发起，领域适配器自行处理已有的安全暂存物和回滚点。
4. `Cancelled` 只表示取消被领域在安全边界接受；已提交给不可取消的 OS 安装时 `cancellable=false`，取消请求返回稳定的不可取消问题码而非假装成功。

## 6. 领域适配器与特权边界

`IInstallationService` 只包含 `GetActive`、`Start`、`Cancel`、`Recover` 和强类型进度报告；服务注册表按 `InstallationServiceId` 解析它。它协调领域安装器，但不能访问 shell 或任意文件系统。每个实现必须提供：固定输入校验、预检、资源锁声明、可恢复边界、完成后的真实检测、稳定 problem code 和无秘密审计映射。

```text
Endpoint / authorization
  → InstallationCoordinator / durable operation store
  → IInstallationService (Smb | Nginx | Frp | Mihomo | Docker | Git)
  → domain installer / platform adapter
  → typed PrivilegedHelper operation or fixed runtime installer
```

Linux 特权操作继续由 root-owned Helper 的封闭 operation 完成；Windows 继续由已认证的 LocalSystem pipe 或受限系统 API 完成。Helper stdout 只能是受版本控制的 NDJSON progress/result 帧，内部排空子进程 stdout/stderr；Server 严格验证帧大小、总数、枚举和百分比。任何未知帧、超限值或非 JSON 输出都使任务以 helper-protocol problem code 失败。

## 7. 授权、审计与 Client

- 查询仅限任务发起者或拥有该服务安装权限的管理员；任务 DTO 不泄露其他用户活动、目标路径、版本外的敏感部署细节或诊断输出。
- 每个服务保持独立 read/manage/install 权限及 HostElevationCapability。安装权限不由普通应用 manifest 权限替代；Nginx、SMB、FRP/Mihomo、Docker、Git 不互相蕴含。
- 创建、开始、阶段变更到终态、取消和恢复判断记录一次审计；轮询不产生审计噪音。审计只记录 actor 引用、服务、kind、operation ID、结果、problem code、时间和受控资源 hash。
- Client 以 operation ID 轮询（500 ms–2 s 退避）；重开窗口从持久的 UI 工作区状态恢复。阶段使用本地化 key，确定进度显示百分比，不确定进度显示活动指示。网络查询失败只提示连接状态，不能把 Server 任务改成失败。

## 8. 实施顺序与验收

1. 新建公共 Protocol、problem-code 命名规则和 `InstallationOperationStore`；覆盖序列化、身份隔离、幂等、资源锁、原子持久化及启动恢复测试。
2. 实现 Coordinator、授权策略、通用 Endpoint 和 Client 轮询组件；直接迁移 Nginx 与 Git 的受管安装及取消流程，删除 `WebServerOperationStore` 的安装用途、Git 同步安装响应和旧安装路由。
3. 迁移 SMB：固定 APT `Status-Fd`/Windows role 阶段映射、真实检测和单 SMB 资源锁；更新 File Services Client 与其所有测试。
4. 迁移 FRP 与 Mihomo：将进程内/专用 operation 状态升级为持久操作、固定发布清单校验、版本切换和健康检查；保留 runtime 管理的领域模型，不保留旧安装状态 API。
5. 实现 Docker 安装服务：仅实现支持矩阵中的安全方案，Windows Server 继续拒绝自动安装；部署环境验证后才开放 UI 执行入口。
6. 在隔离 Ubuntu 与 Windows Server VM 验收：缓存命中/未命中、无 Content-Length、慢网、校验失败、APT/dpkg 冲突、Helper 协议损坏、服务健康失败、取消边界、Server 重启、权限隔离、重复请求、并行服务以及 Client 断线恢复。

完成条件：所有纳入表中的服务都只经统一安装任务路径显示与查询；进度可追溯且不伪造；不存在同步长请求、旧安装 contract、跨用户泄露、任意特权执行或中断后永久运行的任务。
