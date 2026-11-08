# MB-02/03 Git 执行边界与 BuildKit 验收

Git 仓库、CLI、构建执行器与原构建回执。BuildKit 启动受阻的实测记录与已验证的执行边界分别保留。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [10. Git 执行切片实施（2026-11-03，MB-02/03 部分）](#10-git-执行切片实施2026-11-03mb-0203-部分)
- [11. Git 仓库实现与 Host 端口（2026-11-03，MB-02/03 后续）](#11-git-仓库实现与-host-端口2026-11-03mb-0203-后续)
- [43. MB-03 构建清理责任与测试机预检（2026-11-06）](#43-mb-03-构建清理责任与测试机预检2026-11-06)
- [MB-03 离线制品与启动阻塞](#mb-03-离线制品与启动阻塞)
- [MB-03 独立 Engine 的受限 builder 复核](#mb-03-独立-engine-的受限-builder-复核)
- [MB-03 Git 构建派发检查点与重启对账](#mb-03-git-构建派发检查点与重启对账)
- [MB-03 专属 HTTPS 源仓库夹具](#mb-03-专属-https-源仓库夹具)

## 10. Git 执行切片实施（2026-11-03，MB-02/03 部分）

本节保留首次 CLI/构建切片的证据；仓库实现迁移及当前扩大后的边界以 §11 为准。

### 已落地边界

- `Shared/RelaxKonOS.ServerModule.Git.Contracts/` 持有 Git 构建/CLI 接口、选项、共享问题类型及稳定台账模型。只读 `GitBuildLedger.InspectLedger` 不加载执行程序集，停用门禁仍可检查持久责任。Host 与模块共享 JSON 类型，私有实现类型不会进入持久台账的序列化元数据缓存。
- `ServerModules/RelaxKonOS.ServerModule.Git/` 持有 `HostGitCli`、`GitBuildCommandRunner`、`GitBuildService` 和按代次的 `GitExecutionModule`。该切片无 Server 实现引用；项目构建守卫只允许共享契约依赖，另有测试检查 Host 实际程序集引用。
- Host 的 `GitModuleRuntime` 继续作为原协调器唯一生命周期入口。`CollectibleGitExecution` 从部署目录 `Modules/Git/RelaxKonOS.ServerModule.Git.dll` 加载执行代码，使用 collectible ALC；缺失/链接载荷拒绝，不回退到旧内置实现。当前没有私有托管或原生依赖，不提供任意动态依赖扫描。
- `IGitExecutionModule` 是当前随产品发布的执行切片 ABI；上下文只有内容根路径、构建选项、数据保护、日志和工作租约端口，不传根 DI、完整配置或 Host 环境对象。该入口没有注册路由/根服务的能力。官方进程内代码仍是完整受信代码，这不是安全沙箱。
- 构建服务按线程安全 Lazy 每代创建一次；业务仍通过原类型化代理和后台租约执行。销毁失败保留隔离责任；成功销毁先清空实现引用，再请求 ALC 卸载。工厂仅保存弱引用，最多容纳 4 个尚未回收上下文，超过时拒绝再次加载并由原故障路径报告，避免无限代次累积；生产不强制 GC。
- Server 项目对实现项目仅有构建顺序引用（`ReferenceOutputAssembly=false`），编译时只依赖共享契约；构建/发布将执行 DLL 放入 `Modules/Git/` 并传播到引用 Server 的产物。测试工程可直接引用实现用于受控命令故障注入，不能据测试依赖误判 Host 静态依赖。

### 验证与限制

验证命令仍为 `--module-residency-checks`，已加入默认模块测试入口。真实 `GitBuildService` 在 collectible ALC 执行、凭据写入后休眠、重开原台账、100 轮上下文弱引用回收，以及刻意保留旧实现元数据时第 5 代拒绝均通过；测试 GC 仅用于观察。原有请求/流、依赖、构建取消、清理、宿主关闭及持久回执故障专项继续运行。凭据使用隔离的测试保护器与临时数据，不接触现有仓库或生产凭据。

Windows 最终驻留专项 **203 项**（`residency-windows-final.log`）、Linux 最终驻留专项 **210 项**及模块包 **141 项**（合计 351 条 PASS，`linux-final.log`）通过；原始构建/发布及基线复现证据均位于 `.artifacts/module-boundaries-20261103/`。Windows 的 Git 工作区 64 项及账户隔离/注册身份检查通过；Server.Tests 构建通过；framework-dependent .NET 10 Linux 发布已核对包含模块 DLL。完整 Linux 默认回归在第三方 ModuleHost 进程夹具报 `module-host-package-invalid`，变更前保留产物以同一命令也能复现；此前跨 Windows 打包的 apphost 还需补执行位。未修改生产 RID/包校验或安全环境清理来绕过失败，不宣称完整回归已通过。

这是随 Server 发布的官方执行程序集，**不是新增独立包安装器**。现有官方安装包的 `IInstalledServerModule` 启动注册 ABI 尚未迁移，未增加新旧 ABI 双解析。`IProblemException` 与后台租约接口已直接移到共享 Abstractions，Git 构建/CLI 接口和选项已移到 Git.Contracts；命名空间保持当前源码调用一致，不保留旧程序集类型转发。引用这些类型的内仓调用者和测试已同步构建，旧外部二进制应按当前依赖重新构建，不承诺二进制兼容。首次执行切片尚未迁移 `LocalGitRepositoryService`；这一限制已由 §11 的后续实施解除。静态路由、登记持久化和部署端口仍保持 Host 所有。

MB-01 的当前真实业务内存基线、MB-02 的全模块/安装包 ABI 和 MB-03 的真实构建引擎生命周期仍开放；仓库代码边界的后续进展见 §11。受控构建执行器测试不是实际 Docker/BuildKit 执行中的 collectible ALC 验收；弱引用收集也不证明生产 RSS 已下降。MB-04～MB-08 本轮未实施，未部署这批改动到生产 Server。

## 11. Git 仓库实现与 Host 端口（2026-11-03，MB-02/03 后续）

### 当前边界

`LocalGitRepositoryService` 的全部 partial 实现已移入 `ServerModules/RelaxKonOS.ServerModule.Git/`，覆盖仓库登记业务、分支/提交/历史/图、工作区/冲突/同步、凭据选择与控制台记录。`IGitRepositoryService` 直接移入共享 Git.Contracts，没有旧构造入口或 Host 内实现回退。Host `GitModuleRuntime` 只按共享接口转发；模块内仓库服务与构建服务分别用线程安全 Lazy 激活，共享同一可回收执行程序集。

Host 新增两种窄端口：

| 端口 | Host 拥有 | 模块接收 |
| --- | --- | --- |
| `IGitRepositoryStorage` / `HostGitRepositoryStorage` | EF DbContext 工厂、登记 CRUD、固定应用/用户/作用域下的受保护凭据 JSON 存储及短写入互斥 | 实体化 `GitRepositoryRecord` / `GitCredentialSetting`；无 DbContext、实体跟踪或 IQueryable |
| `IGitRepositoryHost` / `HostGitRepositoryAccess` | 每次调用解析当前身份、固定 Git Helper 操作、路径包含规则、三种冲突文件操作 | 当前模式/用户/家目录、结构化命令结果与共享文件 DTO；无 HttpContext、根 DI、任意 Helper 动作或任意可执行路径 |

上下文增加延迟激活的类型化端口，不把整个根服务容器传入模块。构建与模块状态查询不激活仓库存储；只有真实仓库调用才解析这些依赖。正常元数据查询返回前销毁 DbContext，Git 网络/进程操作不再持有数据库上下文。Host 依然负责普通用户/Helper 身份及底层文件访问，Git 参数策略、权限和租约不因模块迁出而放宽；现有数据库表、凭据保护 purpose 与 JSON 字段保持当前结构。

凭据更新只在短存储编辑期持有 Host 的固定写锁，不执行网络或 Git；替代原来跨服务实例保留的静态逐用户锁字典，避免持续积累用户 ID。读取/编辑单个 host 时按其精确 key 查询，列举/删除才读取该用户的相关集合。编辑口只能写 `accounts-` 命名空间内的固定应用设置，取消和异常通过异步销毁释放上下文与写锁。受保护凭据的持久模型移到共享契约，模块私有类型不会进入 Host 持有的默认序列化元数据缓存；模型本身不改变信任或授权。

仓库写锁与最多 500 条控制台记录仍按代次归模块；销毁时释放信号量、清空缓存，再由 Host 清除整个模块引用并请求卸载。静态路由、无状态编辑器桥、用户权限、元数据存储与外部 Git 工作树仍归原所有者；移除仓库登记不删除实际工作树。

### 验证

新增 `GitRepositoryAssemblyChecks`，接入 `--module-residency-checks` 和默认模块测试入口。真实 Git 初始化/登记、add/status、SQLite 元数据、并发账户保存、用户隔离与控制台记录后能回收真实 ALC；100 轮重开保持同一仓库与受保护账户，并执行真实 `git status`。调用方故意保留完成的凭据请求 Task，旧上下文仍全部可回收。

测试还在实际 Git 执行检查模式时断言最近使用的元数据 DbContext 已销毁，并验证凭据写锁的取消、越界 key 拒绝及后续正常获取。回收观察先退出模块 async continuation 的调用栈，再进行有界的测试 GC；不能在模块方法尚在栈上时把暂时未收集误判为永久泄漏。生产流程不增加等待 GC、反射清空框架缓存或强制回收。

- Windows 最终驻留专项 **217 项**通过；Linux 最终驻留专项 **224 项**及模块包 **141 项**通过（`linux-final.log` 合计 365 条 PASS）。
- Windows/Linux Git 工作区各 **64 项**通过，均包含账户持久化/凭据隔离及 Helper 注册身份检查。
- Windows 冲突处理 **89 项**、推送预览 **13 项**通过；Server.Tests 构建通过。
- framework-dependent .NET 10 Linux Server 发布成功，仍把当前执行 DLL 放在 `Modules/Git/`。本轮独立测试目录未覆盖生产 Server，未操作已有仓库、组件数据或生产凭据。

证据目录为 `.artifacts/git-repository-boundary-20261103/`：`residency-windows-final.log`、`linux-final.log`、`workspace-{windows-final,linux}.log`、`conflicts-windows.log`、`server-tests-build.log` 及 publish 日志。Linux 独立目录为 `/home/nanami/component-catalog-20261102/phase23/git-{repository,workspace}-tests/`。§10 中完整默认模块回归的既有 ModuleHost 夹具失败及基线复现仍是已知限制，本轮不宣称该整套回归通过。

### 仍然开放

本轮实现和实测范围是 Git 业务实现的程序集边界；未改变独立官方安装包的启动 ABI、公开模块诊断 DTO、模块集合启停/版本变更的重启规则。真实 Docker/BuildKit 的执行中取消/清理/恢复与 ALC 联合验收、业务压力下生产内存/延迟基线、其他领域拆分及第三方宿主闲时退出仍待实施。未根据测试 GC 的结果宣称生产 RSS 或整个进程树下降，MB-01/02/03 仍按各自完整完成条件追踪。

## 43. MB-03 构建清理责任与测试机预检（2026-11-06）

Git 构建在失败或取消后继续持有原代次工作租约，并在镜像清理结果确定后才提交终态。删除仅针对当前 operationId 的镜像标签；删除返回非零时，通过 Engine 成功列出该精确标签且结果为空确认其不存在。清理不可确认时，原回执持久化为 interrupted / git-build.image-cleanup-unverified，并保留恢复门禁。若 CLI 进程清理本身不可确认，保留 process-cleanup-failed，避免与未知执行者并发删除。相同幂等键仍读回原回执。已写入的回执继续通过原代次查询；台账写入失败仍隔离代次。该变更未提供解除恢复门禁的自动流程。

测试机以既有固定主机密钥连接。普通 SSH 身份无 Docker socket 权限，以现有 sudo 凭据做资源预检。Engine 29.8.2、Buildx 0.37.1、默认驱动内置 BuildKit 0.33.1；实际验证了缓存 Alpine 3.22 上随机操作标签的删除，以及对已不存在标签的删除失败和精确空列表。测试只添加和删除该随机标签。该检查覆盖 Engine 命令语义；完整服务的 BuildKit 构建、取消、断连、重启与 ALC 联合验收仍开放。现有节点为默认 docker 驱动，缺少 Git 门禁要求的专用受限 remote builder；测试机默认出站代理无法访问镜像仓库，清空代理环境后直连 Docker Hub 与 GitHub 均超时。仍需准备可验证的 rootless BuildKit 制品和可达的专用构建仓库，再进行执行中故障验收。

Linux Release 后台工作专项 15 项通过（--module-background-work-checks），覆盖终态前的原代次租约、不可确认清理后返回原回执且禁止休眠，以及持久化失败隔离。其中 CLI 取消使用真实子进程，构建与清理异常使用受控命令执行器。

MB-05 预检发现同一 Engine 有其他运行中的容器，且测试机存在 Nginx。后续使用独立 Engine 实例、独立 Nginx 配置与端口、可控 CA 和独立挑战域进行故障注入。共享服务重启、CA 订单未知结果、真实响应排空与三域原操作恢复本轮未验收。另见 [MB-05A 联合阶段证据](Milestones.md#39-mb-05amb-06a-与-terminal-t1-阶段验收2026-11-04)。

## MB-03 离线制品与启动阻塞

从 Docker Hub 的 Moby 仓库取得 `moby/buildkit:v0.34.1-rootless`。以 Registry 返回的内容摘要核对索引，并按摘要核对 linux/amd64 清单、镜像配置及各压缩层，同时核对解压层的有序 diffID。平台清单 SHA-256 为 `ace6811c21334fa502644b9711e0ea48e8539c5c2a843c2325dfb7b987ab260b`，配置 SHA-256 为 `9cca736f4c5c924382d15804fdd3e1702823c83ec19546e569e18ae33e700380`。通过固定 SSH 主机密钥离线传输并复核归档 SHA-256，Engine 导入后验证有序 diffID、入口、环境和 UID/GID 1000:1000。本次校验基于官方 Registry 的 HTTPS 与摘要链，未验证发布者签名。

专用 remote builder 使用随机容器和内部网络、独立 Docker CLI 配置、1 GiB 内存、1 CPU 和 128 PID 上限，未使用 privileged、宿主网络或绑定挂载。RootlessKit 在 fork/exec /proc/self/exe 时收到 permission denied，宿主 kernel.apparmor_restrict_unprivileged_userns 实测为 1。Buildx 引导等待未建立连接，按失败记录。容器、网络和本次导入的镜像标签已核对清理。共享 Engine 持续运行，宿主 AppArmor 和 sysctl 未变更。后续需要允许专用 RootlessKit 创建 user namespace 的隔离 Linux 实例或经核准的专用 AppArmor 策略，以及宿主 Git CLI 和 builder 均可达、信任其证书的 HTTPS 443 测试仓库。构建中取消、断连、原回执恢复及 ALC 联合验收仍开放。

## MB-03 独立 Engine 的受限 builder 复核

私有 PID、网络及挂载命名空间内，独立 Engine 导入此前核验的 rootless BuildKit，专属容器使用内部网络、1 GiB、1 CPU、128 PID 限制。RootlessKit 创建子进程仍收到 permission denied，Buildx 未建立连接，探测退出 124。容器和网络已删除，宿主 AppArmor 未修改。日志为 .artifacts/mb03-private-builder.log。需允许专属 RootlessKit 创建用户命名空间的隔离 Linux 环境，以及 Git CLI 和 builder 共同可达且可信的 HTTPS 443 测试仓库。

构建准入现在要求 Buildx inspect 精确报告唯一 remote driver 和唯一指定容器端点，拒绝前缀相似或多节点配置。Linux Release 后台工作回归通过，含两类错误端点、原回执、取消和清理。日志为 .artifacts/mb03-builder-gate-live.log，归档 SHA-256 为 cf7f35e4666c61abd6e669d79b4f17059b20e1339962ce400ca6d9b36d45267f。真实 BuildKit 执行中取消及 ALC 联合验收仍受前述条件阻塞。

当前既有测试机只读复核确认 AppArmor 非特权 user namespace 限制仍为 1，无 RootlessKit 专用 profile、KVM 设备及现成 QEMU。实机 BuildKit 阻塞继续按前述条件记录。日志为 .artifacts/mb03-isolation-readiness.log。

### MB-03 Git 构建派发检查点与重启对账

原 Git 构建台账在 queued 时写入尚未派发的检查点。重新校验 builder 的 CLI 运行期间保留未知责任，预检完整退出后重新持久化未派发。调用 BuildKit CLI 前先持久化可能已派发。写盘失败由原工作租约隔离执行代次。恢复仍由同一构建服务在打开台账时处理，保留原 operationId 和原键。只有明确未派发且无 CLI 清理责任的记录才在持久化 interrupted 后解除准入及卸载门禁。已可能派发、预检中断及缺少新检查点的旧记录继续保留责任。

跨进程 Release 夹具向完整 Git 模块的原台账提供排队、未派发运行、已可能派发运行及未知运行四种状态，两次启动独立进程从原入口读取同一回执，验证第二次打开保持台账字节、检查点和原 ID。已证明未派发的路径可卸载实际 Git 执行代次，未知路径继续拒绝新构建和代次释放。该夹具验证持久恢复及门禁。

Windows 与指定 Linux 测试机均通过跨进程及 100 轮实际 Git ALC 回归。Linux 的受控命令执行器验证预检清理被挂起时 Host 等待并保留工作租约，清理结束后持久化同一中断回执，BuildKit 派发和镜像清理均为零。另验证 BuildKit 命令调用前已读到持久派发检查点，实际子进程取消等待退出的专项通过。Windows MB-05 模块边界按原 `3458e5b7-b363-4d4c-97d7-aefa90270fb8` 读回 Interrupted，受控副作用计数为一。日志在 `.artifacts/mb03-dispatch-recovery/`，Linux 归档 SHA-256 为 `45df763eee4c2d069f15065dce44b60c08f427ed788509c23712b34104cd4e66`。

同机只读复核 AppArmor 非特权用户命名空间限制为 1，`unshare -Ur true` 因写入 uid_map 被拒绝。主机无 RootlessKit、QEMU 和 KVM 设备。真实 BuildKit 在途结果、远端构建清理和动态回收的联合矩阵仍需允许专属 RootlessKit 用户命名空间的隔离环境及受信 HTTPS 测试仓库。生产内存保持未测。

### MB-03 专属 HTTPS 源仓库夹具

既有 Linux 测试机在私有 PID、网络与挂载命名空间内启动独立 Engine。只读 Git smart HTTP 后端在私有桥接地址监听 443，使用专用名称 `repo.mb03.test`、本轮临时 CA 和可离线构建的 `scratch` 上下文。名称解析只写入私有 `/etc/hosts` 挂载和专属容器，信任仅通过 Git 进程的 CA 文件提供。夹具从已核验的本地归档导入原 rootless BuildKit 镜像，无需公网或共享 Docker socket。

宿主 Git 与镜像内非 root Git 都在缺少专属 CA 时拒绝连接，分别报告证书签发者不受信和找不到签发证书。配置 CA 后，两端均通过真实 TLS 浅克隆同一个 `8f29460479e3cc33108fa645c89c671eb7bdc684` 提交，容器中确认构建上下文标记。独立 Engine 中已核对删除专属容器和网络。执行前后共享 Docker、Nginx 的状态及 PID、宿主 hosts 与 AppArmor 设置指纹一致。执行脚本为 `Tests/ManagedRuntimes/run-git-build-https-linux.sh`，在隔离命名空间内传入宿主网络与挂载命名空间 ID、本地镜像归档与镜像标签。原始日志见 `.artifacts/mb03-https-live.log`，镜像归档 SHA-256 为 `3b931425a738347c19895990381c4aa102a8e94d9caaa9fa2d167f4b30529f78`。

专属仓库、名称解析及 Git 客户端信任已在现有机器验证。BuildKit daemon 未参与本次传输。联合验收仍需要允许专属 RootlessKit 创建用户命名空间的隔离执行环境，以验证实际 builder 的证书接入及在途取消、断连、构建清理、原回执和 ALC。共享 AppArmor 策略维持现有配置。
