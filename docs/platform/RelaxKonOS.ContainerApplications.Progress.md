# 容器应用平台首版进度

2026-11-10。目标和验收门槛见[容器应用平台设计](RelaxKonOS.ContainerApplications.Design.md)。第 7.1 节待验收。

| 切片 | 当前实现 | 下一步 |
| --- | --- | --- |
| CA-01 | 官方目录按精确发布查询。桌面通用表单、兼容升级准入和保留数据重装已接入。Server 核对平台、回环端口、镜像摘要及 Engine 镜像 ID。 | 完成真实有状态应用的业务读写、升级、重装及同一组二进制增包验证。 |
| CA-02 | Docker 管理器拒绝变更受管资源。磁盘清单采用 schema 3，结构化双服务拓扑由真实 Compose 解析后复核。 | 接入窄执行端口、真实 Web 和数据库夹具，验证双服务安装、业务数据及跨实例拒绝。 |
| CA-03 | 应用与 Compose 操作账本尚未连接。 | 建立持久阶段执行键和原回执查询。注入已受理未关联的中断与缺失结果，核对写入门禁和无重复派发。 |

## 验证记录

| 环境 | 命令或步骤 | 结果与证据 |
| --- | --- | --- |
| Windows x64，.NET SDK 10.0.300 | `dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false` | D 盘产物复制失败（MSB3021/3027）。在 C 盘隔离源码副本复验通过，3 条既有警告，0 错误。日志位于会话的 `ca-build/server-tests-build.log`。 |
| 同一隔离构建，TEMP/TMP 指向隔离目录 | 运行测试 DLL，依次使用 `--catalog-update-only`、`--deployment-definition-only`、`--docker-compose-code-only` | 三个命令均退出 0。前两个在沙箱内首次运行因密钥文件重命名被拒，获准独立执行后通过。日志为 `ca-build/*-escalated.log`。目录契约、受限身份 HTTP 拒绝、回执重放和 Compose 代码驻留属于受控验证。 |
| Docker Hub Registry | 用仓库作用域匿名令牌查询并提取 Linux amd64 清单摘要 | Nginx 1.27.3-alpine、Uptime Kuma 1.23.16/1.23.17 和 File Browser v2.31.2 取得摘要。Webhook Tester 原 2.8.2 返回 404，改用上游 2 系列清单的固定摘要。四个随附发布见 `RelaxKonOS.Server/Assets/ApplicationCatalog/`。 |
| Linux x64 实机 | 用已知主机密钥执行既有 SSH 主机的 OS、Engine、Compose 只读预检 | 主机密钥通过，当前 SSH 认证被拒。Windows Docker Desktop Engine 29.8.0 和 Compose v5.5.1 可读，只有 Docker Desktop 的 WSL 发行版。第 7.1 节的业务、归属、对账和同一组二进制复用证据待补。 |

桌面 Client 和 ServerModules 测试项目也在同一隔离副本构建通过，日志为 `client-build.log` 和 `server-modules-build.log`。追加的受控 Engine 镜像身份与卷归属用例位于隔离副本的 `RelaxKonOS.Server.Tests/CatalogUpdateChecks.cs`，三个专项退出码均为 0。D 盘无可用空间，追加用例与本详细记录待同步。仓库设计文档第 8 节已追加简要进度。

局域网和 Tailscale 地址使用同一个已固定的主机密钥均通过主机验证。当前 SSH agent 没有身份，非交互登录 `nanami` 在两个地址都返回 `Permission denied (publickey,password)`。

发布方案、运维恢复说明、发布检查清单和下一阶段优先级待第 7.1 节取得实机数据后评审。

## CA-01 桌面目录

隔离副本增加桌面官方目录、精确版本选择和由包字段生成的安装表单。表单显示回环端口和卷路径，秘密字段遮蔽；同一表单值重试沿用幂等键，收到安装回执后选择对应实例并跟踪操作。下一步是兼容升级、保留数据重装和 Linux 端到端验证。

使用 Windows x64、SDK 10.0.300，在 `ca-build` 隔离副本构建 `Tests/Client/RelaxKonOS.WindowPreviews.Tests/RelaxKonOS.WindowPreviews.Tests.csproj`，并以 `--deployments-only` 运行测试。构建和执行记录为 `catalog-client-build.log` 与 `catalog-client-checks.log`。第 7.1 节要求的同一组二进制新增包并真实安装尚待验证。

首次无头测试发现新资源键位于 JSON 片段外层，移入 `Strings` 后重跑通过（退出 0）。通过项包含三语标签解析、目录入口绑定、任意发布及字段元数据传递和安装回执选择正确实例。无头测试以受控客户端回执驱动。

原 D 盘可用空间为零。桌面变更为 `CA-01-client.patch`，在仓库上执行 `git apply --check` 通过。仓库第 8 节已补记同步状态。局域网 SSH 使用原固定主机密钥复验，仍在 `nanami` 认证阶段被拒。

## CA-01 保留卸载与重装

目录实例卸载保留原 ID、修订、数据卷和秘密版本，清除容器绑定。显式重装使用原发布摘要与 Engine 镜像 ID，核对原秘密版本和卷所有权、使用方及端口。缺失卷禁止重建。恢复时未知结果保持原回执并阻止同一实例继续写入，当前切片没有人工解除入口。受管双服务的跨账本恢复仍由 CA-03 跟踪。

Windows x64、SDK 10.0.300，构建 Server Tests 和 WindowPreviews Tests，参数为 `--no-restore -m:1 -p:UseSharedCompilation=false -v:q`。运行 Server 测试 DLL 的 `--catalog-update-only`、`--deployment-definition-only`、`--docker-compose-code-only`，以及桌面测试 DLL 的 `--deployments-only`。证据为 `retained-*-build.log`、`retained-catalog-update.log`、`retained-deployment-definition.log`、`retained-docker-compose-code.log` 和 `retained-client-checks.log`。第 7.1 节的真实业务数据、重装、跨实例卷尝试仍需 Linux 专用 Engine 实测。

合并补丁 `CA-01-client-and-retention.patch` 包含上一切片的桌面变更和本次生命周期改动，已对仓库执行 `git apply --check`。D 盘可用空间仍为零，待同步。局域网 SSH 复验返回 `Permission denied (publickey,password)`。下一步为兼容升级门禁和双服务计划及执行。

两个构建和四组测试均退出 0。受控测试证实保留记录可重新打开、跨实例卷在派发前被拒、旧修订请求被拒、原未知回执不触发 Engine 重派并拦截冲突请求，以及桌面显示重装且隐藏原访问入口。成功重装路径仍缺少 Linux Engine 证据。

## CA-01 兼容升级

官方包以 `upgradeFrom` 声明经过验证的来源发布与不可变镜像。Server 核对已提交修订的归属、来源版本、摘要、平台和卷布局。提交还绑定预览中的目标镜像及原修订，拒绝未声明的来源、同镜像替换和普通部署路由绕过。目录实例的回滚保持关闭。桌面提供精确版本选择、只读预览、阻止原因和显式确认。

当前正向路径使用只随测试项目分发的目录夹具。Windows x64 / SDK 10.0.300，在隔离副本构建 Server Tests 与 WindowPreviews Tests（`--no-restore -m:1 -p:UseSharedCompilation=false -v:q`），运行 `--catalog-update-only`、`--deployment-definition-only`、`--docker-compose-code-only` 与 `--deployments-only`，均退出 0。证据为 `upgrade-*-build.log`、`upgrade-catalog-update.log`、`upgrade-deployment-definition.log`、`upgrade-docker-compose-code.log` 和 `upgrade-client-checks.log`。官方生产目录中的兼容边待 §7.1 真实有状态应用验证后登记。

Android 调用方改用 schema 2 并随确认提交预览中的目标镜像；保留状态在 Android 只读。Android 验证采用 Gradle 9.7.1 的 `--offline --no-daemon :app:testDebugUnitTest`，筛选 `DeploymentRevisionHttpTest`、`ApplicationDeploymentWireTest`、`CatalogUpdateEditorTest`、`DeploymentRevisionRepositoryTest` 和 `DeploymentHttpTest`。现有 C 盘 JDK 缓存作为可写目录，D 盘依赖只读复用，复验日志为 `upgrade-android-retry.log`。

首次 Android 测试发现两个 schema 断言仍沿用旧值。将正常夹具期望改为 schema 2、将拒绝夹具明确设为 schema 1 后，44 项全部通过。此结果验证客户端当前 wire 契约。

合并补丁 `CA-01-through-upgrade.patch` 从 D 盘当前工作区生成，包含已验证的桌面目录、保留生命周期和升级准入。D 盘空间仍为零，改动保存在 C 盘隔离副本。下一切片为 CA-02 受管双服务计划及执行端口。

## CA-02 Docker 管理器归属保护

容器、网络和卷的管理器写入按 Engine 实查名称和归属标签拒绝受管资源。容器和网络别名解析到原对象 ID；容器创建拦截应用网络、命名卷及宿主绑定挂载，禁止请求伪造应用归属标签。Compose 预览与部署验证有效资源名、标签、挂载类型和卷驱动选项。部署重解析并比较已校验的解析结果，随后将这一份配置交给 `up`，另存用户提交源。

环境为 Windows x64、.NET SDK 10.0.300、Docker Compose v5.5.1。Server 测试项目以 `--no-restore -m:1 -p:UseSharedCompilation=false -v:q` 构建。Server 测试 DLL 分别运行 `--catalog-update-only`、`--docker-compose-code-only`、`--docker-compose-config-only` 和 `--deployment-definition-only`。真实 Compose 解析器用普通命名卷和转义后的受管卷名复验，后者在 `up` 前被拒，测试不需要 Docker Engine。用 `docker compose --project-name ordinary --file <文件> config --format json` 复验两次解析输出一致，并验证字面美元转义。证据位于 `ca-build/ca02-*.log`。

桌面 WindowPreviews Tests 构建及 `--deployments-only` 通过。Android Gradle 9.7.1 使用 `--offline --no-daemon :app:testDebugUnitTest` 筛选 `DockerLabelsTest`、`DockerResourceHttpTest`、`DockerWireTest`，9 项通过。桌面、Android 和原样重解析日志分别为 `ca02-desktop-checks.log`、`ca02-android-checks.log` 和 `ca02-compose-roundtrip.log`。

Server 构建和上述四组 Server 测试最终均退出 0。评审分支 `codex/container-applications-preview` 从 `020dc9c0` 建立，包含 CA-01 与本轮归属边界。D 盘原工作区和 MB-04 并行改动独立保留。

下一步接入双服务包的结构化计划和受管执行端口，并实现跨账本原 ID 对账。当前证据没有覆盖双服务安装和业务读写。

## CA-02 结构化拓扑预检

Server 随附清单使用唯一当前磁盘契约 schema 3。所有发布均声明服务列表、Web 入口及按服务绑定的参数，单服务也使用相同结构。目录 HTTP 摘要的 schema 2 保持原字段语义。拒绝依赖环、未声明依赖、重复卷和跨服务非法参数绑定。

目录拓扑接口调用受管 Compose 解析路径，输入是服务、内部依赖、端口、命名卷和注入位置。Server 生成专属网络和实例归属标签，解析后核对镜像摘要、资源上限、网络、挂载、依赖及回环端口。预检使用临时目录与固定占位值，响应只包含公开拓扑。未接入受管执行的多服务发布不接受安装。夹具只分发给测试项目，使用合成摘要，不能据此声明业务验收。

环境为 Windows x64、.NET SDK 10.0.300、Docker Compose v5.5.1。Server Tests 构建与 `--catalog-update-only`、`--docker-compose-config-only`、`--docker-compose-code-only`、`--deployment-definition-only` 复验日志位于隔离目录的 `ca02-topology-*.log`。下一步将计划交给受管执行端口，再连通 CA-03 的原操作关联和恢复门禁。

Server Tests 与桌面 WindowPreviews Tests 均以 `-m:1 -p:UseSharedCompilation=false` 构建。四组 Server 专项和桌面 `--deployments-only` 均退出 0。首次真实 Compose 复验发现解析器补齐空 `command`、`entrypoint`，改为允许空默认值并继续拒绝覆盖后复验通过。受控 HTTP 测试核对匿名拒绝、读者可预检且零操作记录，以及执行端口未开放时管理者安装无持久副作用。真实解析用例证实 Web 端口发布且数据库端口未发布。Linux 实机 SSH 仍拒绝认证，第 7.1 节待验收。

## CA-02 受管 Compose 派发

Docker 操作协调器接受结构化计划和应用操作 ID。以实例隔离的请求键先持久化底层回执，再调用受管执行器。按应用身份和原键可以在账本重开后查到同一个 ID。Docker 管理器禁止取消该回执。终态受管回执保留，后续 CA-03 需以应用账本确认关联后再定义清理边界。

执行器限 Linux x64 本地 Unix socket。固定项目、容器、网络和卷名称，在调用 Compose 前后核对已有同名资源和项目容器的实例及 Compose 标签。配置文件只含环境变量引用，具体值经子进程环境传入。解析结果在内存核验，命令原始输出不进入操作诊断。Docker 管理器隐藏受管容器的环境字段，并在读取容器日志前检查归属。

环境为 Windows x64、.NET SDK 10.0.300 和 Docker Compose v5.5.1。执行 `dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v:q`，以及 Server 测试 DLL 的 `--stack-operations-only`、`--docker-compose-config-only`、`--docker-compose-code-only`、`--catalog-update-only`、`--deployment-definition-only` 和 `--module-boundary-milestones-only`，全部退出 0。证据位于隔离构建目录 `ca-build/ca02-managed-*.log`。真实 Compose 解析验证服务级秘密注入的美元符号与引号处理。受控执行验证原键重试不重派、同键不同秘密版本冲突、账本重开可查原 ID、持久化无明文，以及 HTTP 通过别名隐藏环境值并拦截日志。

双服务安装仍受门禁保护。下一步是把应用意图、秘密版本和底层回执关联持久化，以原键覆盖强制中断窗口，再接入可核验的双服务业务探针。SSH 两个地址均通过同一主机密钥校验，但当前客户端没有可提交的密钥。Linux Engine 上的 Compose 写入、业务读写和第 7.1 节中断注入仍待实测。

## CA-03 两个账本的派发意图与关联

应用操作在不可取消的 Creating 阶段持久化目标修订、计划摘要与秘密版本引用摘要，再派发到底层。底层受理后保存原 Docker 操作 ID。重启扫描活动和待核验的应用回执，按原实例、原应用操作 ID、计划和秘密版本引用在 Docker 账本查找。查到的底层回执关联到原应用操作。未确认业务结果时保留原回执和写入门禁。缺失回执时也保持待核验，同键重试仅查询。恢复路径不会调用单容器补偿。

受控测试覆盖正常派发、仅底层已持久化以及完全缺失底层回执三种落盘状态。尚需将此内部派发接入目录安装、核对实际镜像及业务就绪、提交活动修订，并在 Linux 专用 Engine 上以强制终止 Server 复验第 7.1 节。

环境为 Windows x64、.NET SDK 10.0.300、Docker Compose v5.5.1。运行 `dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v:q`，并执行 Server 测试 DLL 的 `--catalog-update-only`、`--deployment-definition-only`、`--deployment-progress-only`、`--stack-operations-only`、`--docker-compose-config-only`。证据位于 `ca-build/ca03-handoff-*.log`。早期运行分别发现夹具子目录未创建及请求摘要未使用 64 字符十六进制值，修正夹具后目录专项退出 0。日志保存了复验中的一对原应用和 Docker 操作 ID。

最终构建和五组专项均退出 0。重开账本后原键、原应用 ID 和原 Docker ID 一致，缺失回执保持待核验，两个场景都拒绝冲突写入且未增加底层派发次数。此切片使用受控执行器重建中断时的持久状态。

## CA-02 / CA-03 双服务安装与活动修订

现有目录安装路由增加默认关闭的 `ApplicationDeployments:EnableManagedComposePreview` 验收开关。控制者提交后，Server 检查 Linux x64 本地 Unix Engine。目录安装回执落盘后才解除工作者等待，确保底层受理窗口内可以按同一安装键查到原应用操作。

按目录字段只保存一个受保护秘密版本，再按服务注入。活动修订记录每个服务的摘要引用和 Engine 镜像 ID。激活前核对容器身份、项目归属、专属网络的连接集合、卷归属和使用方、Web 回环端口以及数据库未发布。Web 就绪须返回 HTTP 2xx。若 Server 在底层成功后中断，启动恢复通过原回执和同一组只读检查提交原活动修订。证据不足时原回执保持待核验。

Windows x64 / .NET SDK 10.0.300 环境构建 Server Tests（`--no-restore -m:1 -p:UseSharedCompilation=false -v:q`）。`--catalog-update-only` 使用真实回环 HTTP 和受控 Engine、Compose 及健康响应，验证默认关闭、读者拒绝、安装回执先落盘、单秘密版本注入、活动修订提交，以及重建中断状态后按原 ID 激活。首次启用测试撞到重复的宿主平台检查，将宿主和本地 socket 判定集中到实际 Engine 端口，应用运行层仍独立核对 Engine 平台。复验退出 0，证据见 `ca-build/ca03-install-catalog-update.log`。

最终构建及 `--deployment-definition-only`、`--deployment-progress-only`、`--stack-operations-only`、`--docker-compose-config-only`、`--module-boundary-milestones-only` 均退出 0。桌面 WindowPreviews Tests 构建及 `--deployments-only` 退出 0。对应记录见 `ca-build/ca03-install-*.log`。

下一步是用真实 Web＋数据库镜像夹具、Linux 专用 Engine 和真实业务探针替换受控响应，验证强制终止 Server、兼容升级及重装证据，并接入通过验收后开放的客户端能力。当前没有实机写入、业务数据或同二进制新增包证据。

## 第 7.1 节业务夹具与镜像证据

加入可重复构建的 Web＋PostgreSQL 和上游 ntfy 双版本夹具。Web 就绪、写入和读取均访问 PostgreSQL。ntfy 2.37.0 和 2.37.2 使用同一个 SQLite 消息缓存路径。清单生成器只接受本机回环 Registry 的固定摘要，拒绝外部镜像引用与覆盖已有清单。测试资产留在测试目录，生成清单时显式选择隔离 Server 发布目录。见[夹具操作规程](../../RelaxKonOS.Server.Tests/Fixtures/ContainerApplications/README.md)。

Windows x64、Docker Desktop Linux amd64 Engine、Compose v5.5.1、Python 3.14.8、.NET SDK 10.0.300。以 `docker build --platform linux/amd64` 构建镜像，在临时 Compose 项目中执行 `up`、`stop db`、`start db` 和 `up --no-deps --force-recreate web`，并用 `curl` 复验。数据库无宿主端口绑定，停库后 Web 返回 503，恢复数据库并重建 Web 后读回同一随机标记。以 `docker run` 对两个 ntfy 版本复用独立测试卷，升级和同版重建后均通过业务 API 读回原消息。证据见 `ca-build/ca-fixture-web-db-{build,smoke}.log` 和 `ca-build/ca-fixture-ntfy-smoke.log`。

在 Docker Desktop 的 Linux 守护进程网络中使用临时回环 Registry，执行 `docker push` 和按摘要 `docker pull`，三份镜像均确认 Linux amd64。先写入两个 ntfy 清单，再添加 Web＋数据库包。用 `--catalog-fixtures-only <目录>` 调用正式目录加载器复验三份清单及精确升级边，Server 构建产物哈希未变。第一次使用 Windows 端口映射时，Docker 守护进程自己的回环无法连接。改在 Linux 守护进程网络内监听后复验退出 0。证据见 `ca-build/ca-fixture-daemon-registry*.log`。容器、网络、卷和测试 Registry 均已清理。

本次 Server Tests 构建命令为 `dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v:q`，并复跑 `--catalog-update-only`。两项均退出 0，证据见 `ca-build/ca-fixture-server-build.log` 与 `ca-build/ca-fixture-catalog-regression.log`。

上述镜像和业务证据来自 Docker Desktop 隔离测试项目。待在专用 Linux Engine 上通过完整 Server 与客户端目录流程复验安装、兼容升级、保留卸载重装、归属和越权、强制终止原 ID 对账，以及同一组 Server 和客户端二进制新增包。当前 Windows SSH agent 仍无密钥，两个实机地址此前均通过同一主机密钥校验。

## CA-02 客户端验收入口

Linux x64 Server 仅在应用部署和 Docker 模块可用、且显式打开验收开关时，在登录描述符中声明双服务能力。桌面目录按登录描述符与包声明判定可用性，同一客户端能渲染新增包的字段。双服务表单要求预览精确版本及端口，显示 Web 回环发布、内部服务、依赖和卷路径。修改版本或端口会清除预览。提交端点独立检查本地 Engine 与 Compose 解析，解析拒绝发生在创建实例或秘密之前。

Windows x64 / .NET SDK 10.0.300，使用 `dotnet build --no-restore -m:1 -p:UseSharedCompilation=false -v:q` 分别构建 Server Tests 与桌面 WindowPreviews Tests。Server 测试 DLL 的 `--catalog-update-only`、`--deployment-definition-only`、`--docker-compose-config-only` 和桌面测试 DLL 的 `--deployments-only` 均退出 0。受控 HTTP 测试确认 Compose 解析拒绝不留下定义和派发记录。无头桌面测试确认同一客户端按能力开关决定可用性、遮蔽秘密、显示数据库卷、使过期端口预览失效，并使用原目录字段提交。日志见 `ca-build/ca-client-preview-*.log`。

实机验收仍需使用完整 Server、专用 Linux Engine 和固定客户端二进制执行。登录描述符表示验收入口已配置，实际安装仍由本地 Engine、解析器和权限检查决定。

## 第 7.1 节完整 Server API 验收入口

加入 `verify_live.py`。两阶段分别调用真实目录、应用操作和 Docker API，记录专用 Engine ID、版本、Server 二进制哈希、原实例及操作 ID，以及卷、镜像和双服务子回执。状态应用阶段验证业务读写、兼容升级、原卷重装及另一实例的卷删除被拒；双服务阶段验证读者拒绝时资源无变化、数据库无宿主绑定、业务标记及同键原回执。脚本只接受 Linux x64 和本地 Unix socket，要求 Engine ID 与事先选定的专用 Engine 一致。令牌由环境传入，HTTP 禁止重定向，证据文件禁止覆盖。

Windows x64 / Python 3.14.8，运行 `python -B verify_live.py --help`、Windows 平台拒绝用例和 Python AST／受控边界检查。复验确认拒绝远端 socket、错误的 Engine ID 及重定向，Windows 退出前未创建证据文件。记录见 `ca-build/ca-live-harness-checks.log`。现有 Docker Desktop 报告 Engine 29.8.0 Linux amd64，WSL 清单只有 Desktop 自管发行版，SSH agent 没有登录密钥。尚未在真实 Server 与专用 Engine 上运行这两阶段。

下一步是专用 Linux 环境复验这两阶段、在完整 Server 上执行强制终止窗口和缺失回执注入，并记录相同桌面客户端二进制的实际新增包操作。见[夹具操作规程](../../RelaxKonOS.Server.Tests/Fixtures/ContainerApplications/README.md#server-api-验收与证据)。
