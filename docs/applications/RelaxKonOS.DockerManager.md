# RelaxKonOS DockerManager 设计

> MB-05A（2026-11-04）：Compose 栈协调器已采用同一驻留协调器的 resident 工作 owner。异步准入在持久 queued 之前取得真实租约，任务先登记再启动，覆盖 Compose、观察和终态持久化；Stop 关闭准入并等待真实 Task，等待超时不清除保护。未知终态写入失败隔离该代 owner 并阻止新副作用，不自动重放 Compose。Core Engine/安装/代理端口和外部容器不随管理生命周期停止。此阶段不是动态休眠、程序集卸载或生产内存收益验收，真实 Engine 的部署/观察/修复/幂等/卷生命周期和工作排空已验收，重启与故障联合矩阵仍开放；整体进展由[模块边界设计](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md)拥有。

> 内置 Docker 管理器。它管理 **RelaxKonOS.Server 所在宿主机** 的本地 Docker Engine；客户端只负责本地 UI 渲染，不直连 Docker socket、不保存 Docker 凭据，也不将守护进程 API 暴露到网络。

> **模块归属（2026-10-28）**：容器/镜像/网络/卷编辑、Compose、栈操作协调器、引擎生命周期与 Docker 代理界面归官方模块 `relaxkonos.docker`，默认启用；设置 `Modules:Docker:Enabled=false` 并重启后，`/api/v1.0/docker/*` 与 `server.docker` 能力消失，桌面 Docker Manager 因缺少该能力在启动前被拒绝。**引擎传输本身留在 Core**：应用部署、Docker 运行时安装、Nginx/Mihomo/FRP 与受管运行时下载都直接使用 `IDockerEngineService`、`IDockerRuntimeInstaller`、`IDockerProxyResolver` 与 `IOutboundHttpClientFactory`，禁用管理界面既不会卸载 Docker，也不会让这些下载意外走直连。详见[模块设计实施记录](../architecture/RelaxKonOS.ModulesAndExtensions.Design.md#docker-模块与引擎传输边界2026-10-28)。
> **停用门禁（2026-10-28）**：Compose 栈操作账本（`stack-operations.json`）留在 Core，并被 Host 的停用门禁读取。若账本里还有 `Queued`/`Running` 的操作，`Modules:Docker:Enabled=false` 会让 Server **拒绝启动**并打印具体操作 ID 与项目名——先启用模块让协调器把操作对账成 `interrupted`，再停用即可。账本不可读同样按「状态未知」阻止，不会为了允许停用而清账。
>
> 当前状态：**已实现**本机 Engine 状态、容器/镜像/网络/卷列表、容器生命周期与安全的原地重命名，以及网络、卷和容器的只读详情查看。Compose 编排支持项目列表、定义预览、`up` 部署、服务查看、项目级启动/停止/重启/删除，以及**持久 Stack 操作记录**（断开请求、App 被回收或服务端重启后仍可查询结果、按项目互斥、幂等重放与部分失败分类）。编排会展示 Compose 文件来源；点击来源可路由至内置文件浏览器。RelaxKonOS 部署的 Compose 文件与操作账本保存在服务器受管目录，停止后的项目仍会显示。桌面端已接入 Ubuntu Docker Engine 安装与可恢复任务进度；终端、流式统计和审计仍为**设计中**。
>
> - 架构与内置应用边界：[`RelaxKonOS.Architecture.md`](../architecture/RelaxKonOS.Architecture.md)
> - 协议契约规则：[`RelaxKonOS.Protocol.md`](../architecture/RelaxKonOS.Protocol.md)
> - 权限与危险操作：[`RelaxKonOS.Security.md`](../platform/RelaxKonOS.Security.md)
> - 内置应用通用约束：[`RelaxKonOS.BuiltInApplication.Conventions.md`](../development/RelaxKonOS.BuiltInApplication.Conventions.md)

---

## 1. 目标、范围与非目标

`RemoteDockerManager`（应用 ID：`relaxkonos.docker`）面向单台 RelaxKonOS Server，覆盖 Docker Engine 的完整日常运维闭环：发现或安装运行时、验证、镜像和容器生命周期、Compose Stack、网络与卷、日志/终端、资源统计、备份与审计。

设计参考了 Portainer 的环境、Stack、模板、镜像仓库与按角色授权的组织方式，以及 Docker Engine 的版本化 API；但 v1 **只管理本机单一 Engine**，不实现 Swarm/Kubernetes、多节点代理或远程 TCP Docker API。

Docker Engine API 本身是面向 daemon 的版本化 REST API；Compose 用于描述多容器服务、网络和卷。[Docker Engine API](https://docs.docker.com/reference/api/engine/) [Compose 文件参考](https://docs.docker.com/compose/compose-file/) [Portainer 文档](https://docs.portainer.io/)

### 1.1 v1 必须交付

| 领域 | 能力 |
|---|---|
| 运行时 | 检测版本、API 兼容性、运行状态、根目录、存储/驱动、Linux/Windows 容器模式、资源与警告；可执行安全预检和受确认的安装/启动/升级流程 |
| 容器 | 列表、搜索、详情、创建、启动、停止、重启、暂停、删除、重命名、复制、日志、资源实时统计、文件/挂载/环境变量/端口/网络查看、受控终端 |
| 镜像 | 搜索/拉取、导入/导出、标签、构建、历史、删除与未使用项清理预览 |
| Stack | 新建、从 Compose 粘贴/上传/受信 Git 源部署、编辑、校验、`up/down/redeploy`、查看服务和变量；保留部署来源与上次成功版本 |
| 网络与卷 | 列表、详情、创建、连接/断开容器、导入/导出卷、删除前依赖检查 |
| 安全与可追溯 | 最小权限、敏感值脱敏、危险操作确认、操作审计、失败可诊断但不泄漏密钥 |

### 1.2 明确不在 v1 范围

- 不开放 `tcp://0.0.0.0:2375`，不把 Docker Unix socket 或 Windows named pipe 转发给 Client。
- 不代替镜像仓库、密钥管理器、CI/CD、Kubernetes 或 Swarm 控制平面。
- 不自动删除容器、镜像、卷、网络或 Docker 数据目录；“清理”一律先生成影响预览。
- 不承诺 Docker Desktop 的安装许可、Windows Server 的容器运行时选择，或任意第三方脚本的安全性。

> **部署警告（当前授权模型）**：Docker Manager 设计为独立、由管理员使用的单实例管理工具。当前 Docker API 以已登录用户为边界，并未为多人共享部署提供服务端的 Docker 读写角色隔离；不要向非管理员、观察者或不受信任用户分发该实例的登录凭据，也不要将它作为多租户 Docker 控制面暴露。

---

## 2. 体验与信息架构

主窗口使用左侧导航和右侧工作区，初始尺寸 `1180 x 760`。没有 Engine 时只显示“开始使用”页；安装、升级、切换容器模式等长任务采用任务抽屉与可恢复进度，不冻结窗口。

```text
概览
├─ 运行时与安装
├─ 引擎控制          启动 / 停止 / 重启整机 Docker 引擎（需确认）
├─ Containers       列表 / 详情 / 创建
├─ Stacks           Compose / 模板 / 部署历史
├─ Images           本地镜像 / 拉取 / 构建
├─ Networks
├─ Volumes
├─ 网络代理          守护进程层与构建层的 HTTP/HTTPS 代理
├─ Registries       仅保存连接元数据；凭据引用安全存储
├─ Events & Audit
└─ Settings         显示、刷新、日志与危险操作偏好
```

容器详情固定分为“概览、日志、终端、检查、挂载、网络、环境、事件”页签。操作按钮按当前状态显示；例如停止中的容器不可重复停止。删除、强制停止、重建 Stack、镜像/卷清理必须展示影响对象、不可逆性和确认文本。

### 2.1 从安装到管理的流程

```text
打开应用 → 连接本地 Engine
   ├─ 成功 → API 版本协商 → 功能探测 → 概览
   └─ 不可用 → 读取平台/虚拟化/磁盘/权限预检
                    ↓
              选择安装方案并阅读影响
                    ↓
         用户确认 + 宿主 OS 提权委托
                    ↓
            安装或启动 → hello-world 验证
                    ↓
              Engine 已连接 → 正常管理
```

安装向导只生成并展示将要执行的计划；执行前要求已授权的管理员会话确认。它不收集 sudo、Windows 管理员或 Registry 密码。任何失败保留步骤结果、已修改组件和官方恢复链接。

### 2.2 平台支持矩阵

| 平台 | v1 管理方式 | 安装策略 | 备注 |
|---|---|---|---|
| Ubuntu 22.04/24.04/26.04 LTS | 本机 Unix socket `/var/run/docker.sock` | 官方 APT 仓库安装 `docker-ce`、CLI、`containerd.io`、Buildx、Compose 插件，分别使用 jammy/noble/resolute | 先检查冲突包、防火墙和现有数据；Docker 文档特别指出 Docker 发布端口会绕过部分 UFW/firewalld 规则，必须在向导中警告。 |
| Windows 10/11 | 本机 named pipe `npipe://./pipe/docker_engine`；应用部署器只接受 Linux 容器模式 | 内置引导要求用户从官方渠道安装已获许可的 Docker Desktop，并选用 WSL 2 后端 | **单交互操作员开发路径**：Docker Desktop 所有者、Server 进程和登录的宿主账户必须是同一 Windows 用户；不建议在任意 WSL 发行版中手工安装 Docker Engine。 |
| Windows Server | 管理已安装且经能力探测合格的本机 Engine-compatible runtime | **不自动安装**；运行时供应商、容器模式与许可由管理员明确选择后再增加专用安装提供方 | 防止将桌面安装器误作为生产服务器自动部署方案。 |

Ubuntu 方案以 Docker 官方安装文档为唯一命令来源；该文档要求先移除冲突包，推荐官方 APT 仓库，并以 `hello-world` 验证。[Docker Engine on Ubuntu](https://docs.docker.com/engine/install/ubuntu/) Windows 端 Docker Desktop 的安装需要选择 WSL 2 或 Hyper-V 后端。[Docker Desktop on Windows](https://docs.docker.com/desktop/setup/install/windows-install/)

桌面端连接 Linux 服务器且状态为 `docker.not_installed` 时，点击顶部状态按钮会确认安装影响，再向通用安装 API 提交 `DockerInstallationRequest(Confirmed: true)`。底部显示安装进度、操作日志与可恢复任务，完成后刷新 Docker 状态。

自动安装仅支持 Ubuntu 22.04/24.04/26.04 的 x64/ARM64 主机，需已部署特权 Helper 并通过 `--docker-access` 授权 Server 服务账户；其他 Linux 发行版由服务端拒绝。首次授予 Docker 组权限后可能需要重启 RelaxKonOS Server，界面保留对应提示。

Windows 10/11 的 Docker 不可用时，Docker Manager 的状态按钮会直接打开内置引导：检查 WSL 2/虚拟化，安装并启动 Docker Desktop，选择 WSL 2 backend，确认 Linux containers 模式，然后刷新 Engine 状态。

该引导不自动安装 Docker Desktop、启用 WSL、接受第三方许可或配置 Windows Server。Docker Desktop 的 WSL 2 容器与镜像不跨 Windows 用户共享；它不是供服务账户或另一名 RelaxKonOS 登录用户代管的系统 Engine。

开发时必须用 Docker Desktop 所有者启动 Server，并用该同一宿主账户登录；不同账户只能做不含 Docker 的 Helper/文件边界测试。Windows Docker Desktop 路径用于开发、个人自托管和验证；无人值守或生产部署优先使用独立 Linux 主机或 VM。

---

## 3. 架构

```text
RemoteDockerManager (Client 本地 UI)
   │ IRemoteDockerClient / HTTPS + JWT
   ▼
Docker endpoints (RelaxKonOS.Server)
   │ 授权、验证、审计、任务编排
   ▼
IDockerEngineService ── IDockerRuntimeInstaller ── IDockerComposeService
   │                         │                         │
   ├─ Docker Engine API       └─ UbuntuInstaller /      └─ 受限 docker compose
   │  Unix socket / pipe         WindowsGuidedInstaller    命令执行器
   └─ API 协商、流式日志/统计/事件
```

### 3.1 服务端边界

- `IDockerEngineService` 是唯一可访问 Docker 的业务边界，封装 API 版本协商、列举、生命周期操作、stream 和错误映射。
- `IDockerComposeService` 只接受结构化 `StackDefinition`，在服务器受控工作目录写入 Compose 源文件，调用经过白名单构造的 `docker compose` 子命令；不得拼接用户 shell 字符串。已部署的 Compose 源默认放在 Linux `/var/lib/relaxkonos/docker-compose`、Windows `C:\\ProgramData\\RelaxKonOS\\docker-compose`，绝不写入安装目录或项目源码；管理员可通过绝对路径配置 `DockerCompose:DataDirectory` 覆盖。开发环境默认使用当前用户的 LocalApplicationData 目录。
- `DockerStackOperationCoordinator` 是 `IDockerComposeService` 的**唯一调用方**：端点只提交请求和读取记录，执行、幂等、按项目互斥、结果分类与重启核对都归它。任何绕过协调器直接调用 Compose 的路径都会重新引入“HTTP 断开即失去结果”的问题。
- `IDockerRuntimeInstaller` 返回 `InstallationPlan`，再由独立的受提权宿主操作执行器运行。安装器永远不能自行提升权限。
- Linux socket、Windows named pipe、CLI 路径和平台判断全部封装在 Provider 内；Endpoint、Client 和 ViewModel 不出现平台分支或 Docker CLI 命令。
- Docker 原始错误转为稳定问题码，例如 `docker.not_installed`、`docker.permission_denied`、`docker.api_incompatible`、`docker.conflict`，细节仅写入管理员审计。

### 3.2 协议与端点（拟定）

在 `Shared/RelaxKonOS.Protocol/Docker/` 放置零依赖 `sealed record` DTO、路由常量和枚举；Client/Server 都只能依赖此项目。

| 方法 | 路由 | 权限 | 说明 |
|---|---|---|---|
| GET | `/api/v1.0/docker/status` | `server.docker.read` | 状态、能力、安装建议，不含密钥 |
| POST | `/api/v1.0/docker/installation/plan` | `server.docker.install` | 仅预检与生成计划 |
| POST | `/api/v1.0/docker/installation/execute` | `server.docker.install` | 明确确认后启动受提权任务 |
| GET/POST | `/api/v1.0/docker/containers` | read/manage | 列表、创建 |
| POST/DELETE | `/api/v1.0/docker/containers/{id}/{action}` | manage | 生命周期、删除、复制、exec |
| GET | `/api/v1.0/docker/containers/{id}/logs` | read | 带游标/时间范围；follow 用 SignalR |
| GET | `/api/v1.0/docker/stacks` | read | Compose 项目列表；`stacks/{name}/services` 读取项目服务 |
| POST | `/api/v1.0/docker/stacks/preview` | read | 用服务端的 `docker compose config --format json` 解析定义，返回服务/命名卷/网络与 `definitionVersion`。**不落地任何改动**，是操作者批准的对象 |
| POST | `/api/v1.0/docker/stacks/deploy` | manage | 提交部署（携带 `definitionVersion` 与 `Idempotency-Key`），返回 `202` + 持久操作记录 |
| POST | `/api/v1.0/docker/stacks/{name}/{action}` | manage | 项目级 start/stop/restart/delete（携带 `Idempotency-Key`），返回 `202` + 持久操作记录 |
| GET | `/api/v1.0/docker/stacks/{name}/operations`、`.../operations/active` | read | 项目操作历史（最新在前）与当前活动操作 |
| GET | `/api/v1.0/docker/stack-operations/{id}`、`.../diagnostics` | read | 单次操作的权威记录，以及该步已脱敏、限长、可标记截断的输出 |
| POST | `/api/v1.0/docker/stack-operations/{id}/cancel` | manage | 请求取消；仅仍在活动的操作可取消 |
| GET/POST/DELETE | `/api/v1.0/docker/images|networks|volumes` | read/manage | 资源管理，删除前依赖检查 |
| GET | `/api/v1.0/docker/events` | `server.docker.read` | 过滤后的事件和审计只读流 |
| GET/PUT/DELETE | `/api/v1.0/docker/proxy` | read/manage | 读取、写入、移除守护进程层与构建层代理；写操作返回完整状态，被拒绝时返回稳定问题码 |
| POST | `/api/v1.0/docker/engine/{action}` | `server.docker.manage` | 启动/停止/重启整机 Docker 引擎；`stop`/`restart` 必须带 `confirmed`，返回操作后的引擎状态 |

长任务（拉取、构建、部署、导入导出、安装）返回 `OperationId`，以通用 SignalR 任务通道推送阶段、百分比、可本地化消息键和终态。日志与终端必须设置最大帧、速率限制、取消和断连清理；浏览器/客户端不保留 raw Docker stream。

**Compose 操作不依赖推送通道**：它由 `DockerStackOperationCoordinator` 在发起请求的 HTTP 调用之外执行，结果写在宿主上的 `stack-operations.json`（与 Compose 源同目录，原子替换 `*.tmp` → `File.Move`）。SignalR 只是可用时的加速手段——没有它（例如 Android 客户端）客户端按持久记录轮询即可拿到同一答案，断线或服务端重启后也能补读。

### 3.2.1 持久 Stack 操作的语义边界

- **偏移/结果的唯一真相是服务端记录**，客户端不从自己的请求推断“部署成功”。
- **幂等键绑定请求指纹**：`Idempotency-Key` 相同但请求不同（例如换了一份 YAML）返回 `docker.stack_idempotency_conflict`；不会把一次批准套用到另一份文档。客户端在为同一份文档重试时复用键，请求变了就换键。
- **按原请求核实回执**：`GET /api/v1.0/docker/stack-operations/by-request` 携带原 `Idempotency-Key` 请求头，只查询当前认证操作者的现有账本记录，不创建或重放操作。404 不能证明原请求从未提交：请求可能仍在处理，旧终态记录也可能超过账本保留上限。
- **部署绑定 `definitionVersion`**：`preview` 返回的版本随 `deploy` 回传；服务端重新计算不一致即拒绝（`docker.stack_definition_changed`）。所以“批准”的对象是一份确定的文档，而不是一次点击。
- **按项目互斥 + 全局并发上限**：同一项目同时只有一个活动操作（`docker.stack_operation_conflict`）；`DockerCompose:MaximumConcurrentOperations`（默认 2）限制不同项目同时执行的数目。
- **结果是观察到的服务状态**，不是命令退出码。命令成功但服务未达目标状态，或命令失败但有容器残留，都是 `partialFailed`；`RecoveryProblemCode` 只在需要操作者决策且能补充 `ProblemCode` 时出现，二者永不同值。**不声称整组原子回滚**；恢复旧定义也不等于回滚数据迁移。
- **重启核对不重放**：进程停止时仍活动的操作在下次启动被核对为 `interrupted`（`docker.stack_interrupted`），记录观察到的服务并给出恢复动作；引擎失联绝不上报为成功。
- **删除项目保留命名卷**：`compose down` 不带 `--volumes`；卷引用与 `docker.volume_in_use` 见 §3.3。

### 3.3 持久化边界

Docker Engine 仍是容器、镜像、卷、网络和运行状态的数据来源；RelaxKonOS 不复制这些实体到 SQLite。SQLite 仅保存：

- Stack 草稿、已部署 Compose 内容的加密版本快照、来源和部署结果；
- Registry 配置元数据及对 OS 安全存储中机密项的引用；
- 用户偏好、可恢复任务摘要和审计记录；
- `docker_proxy_settings`（宿主全局单行表，`CHECK(settings_id = 1)`）中的 Docker 代理偏好；代理 URL 可能内嵌 `user:pass@`，故经 DataProtection（purpose `RelaxKonOS.Docker.ProxySettings.v1`）加密后落库，`no_proxy` 与各开关明文保存。见 §3.5；
- 管理器不保存 Docker socket、daemon TLS 私钥、Docker Desktop 账户令牌或明文 `.env` 秘密。

**Stack 操作账本**不走 SQLite，而是宿主上的 `stack-operations.json`，与 Compose 源同目录（`DockerCompose:DataDirectory`，`DockerComposePaths` 是两者唯一的路径来源）：

- 记录 200 条操作与 1000 条审计（先淘汰最旧的**终态**操作，活动操作永不被裁剪），审计行随后按仍在册的操作过滤，因此不会出现指向已消失操作的审计；
- 按项目取历史、按 `Idempotency-Key` 的哈希查重放、按项目哈希做互斥；操作者只存 SHA-256 引用，ID 和幂等键都不落原文；
- 诊断输出在写入口即用 `ProxyLogSanitizer` 逐行脱敏、单行限 512 字符、最多保留末 120 行；丢弃了行首就写入 `DiagnosticsTruncated`，读者不会把日志尾部误当全量；
- 单次写入是 `*.tmp` 写完 `Flush(true)` 后 `File.Move(overwrite)`；写失败即整库 fail-closed（`docker.stack_store_unavailable`），不会留下半份账本；
- 打开时校验每条记录的结构（GUID、项目名、枚举、四类 64 字符引用、服务字段长度上限），任一不合法按不可用处理而不是带着坏数据继续运行。

**卷保护**：`docker.volume_in_use` 与 `DockerVolumeDetailsDto.UsedBy` 是同一判据的两面。`GET /volumes/{name}` 用 `docker ps --all --filter volume=<name>` 列出引用容器（**停止**的容器也算占用），`DELETE /volumes/{name}` 在引用非空时返回 `docker.volume_in_use` 而不是先解绑再删。`compose down` 不带 `--volumes`，所以删除项目后卷仍在。

容器日志合并 Docker CLI 的 stdout/stderr，有统一 UTC 时间前缀时按前缀排序，再保留请求尾部；单行最多 512 字符，裁剪或可能截尾时 `Truncated` 为 true。

资源读取采用完整当前事实：容器/镜像/网络/卷列表与统计的 CLI 失败返回 `503` 及实际问题码，不伪造成功空集合；成功但表格列数错误或缺少 ID 同样拒绝。卷引用查询失败禁止继续删除。容器、网络、卷 inspect 的所有权标签必须为当前 Docker 的对象或显式 null；缺失/损坏不能解释为无所有者。`DockerNetworkDetailsDto.Labels` 是共享 REST 必需字段，桌面详情显示该字段。镜像删除接受 Engine 列表提供的完整 `sha256:` + 64 位十六进制 ID，名称/镜像引用拒绝以 `-` 开头的选项文本。

### 3.4 Docker Hub 镜像源

镜像源在 Docker 管理器的“镜像源”页面按 RelaxKonOS 账户配置，而不是写入宿主机的全局 `daemon.json`。用户可维护多个 HTTPS、Docker Hub 兼容的 registry host，并选择其中一个或“默认”。

- 默认：原样执行 `docker pull mysql:8.4`，由 Docker 使用默认 registry。
- 选中镜像源：服务端从数据库读取当前用户的选择，将 Docker Hub 引用转换为 `{mirror}/library/mysql:8.4` 后再调用 Docker CLI。
- 显式 registry（例如 `ghcr.io/owner/image`）不转换，避免把第三方镜像错误发送至 Docker Hub 镜像源。

镜像地址不会由 Docker Manager 客户端随拉取请求发送，因此客户端不能替换其他用户的服务端选择；未来可通过 `ImageMirrorTarget` 扩展到其他镜像类服务。

### 3.5 网络代理

在只能经代理出网的环境中，Docker 需要两处独立的代理配置，二者使用完全不同的宿主机制，因此状态按层分别报告，而不是合并成一个“代理已启用”布尔值：

| 层 | 作用 | Linux | Windows（Docker Desktop） |
|---|---|---|---|
| `Engine`（守护进程层） | 镜像拉取等守护进程自身的出网 | 写 `/etc/systemd/system/docker.service.d/http-proxy.conf` drop-in，再 `systemctl daemon-reload` + `try-restart docker.service` | Docker Desktop 忽略 `daemon.json`，改为编辑 `%APPDATA%\Docker\settings-store.json`（键 `ProxyHTTPMode`/`OverrideProxyHTTP`/`OverrideProxyHTTPS`/`OverrideProxyExclude`），前后 `docker desktop stop`/`start`，写后回读该文件校验取值确实落盘 |
| `Build`（构建层） | `docker build` 的构建期出网 | Server 在 `docker build` 上追加**无值** `--build-arg`（`HTTP_PROXY`/`http_proxy`/`HTTPS_PROXY`/`https_proxy`/`NO_PROXY`/`no_proxy`），代理值放在该子进程的环境变量里；不写宿主文件、不进镜像 `Config.Env`、不出现在命令行 | 同左（`docker build` 的机制与平台无关） |

- **代理来源**：`Custom`（运维填写 URL）或 `ManagedProxy`（复用内置代理运行时，即 mihomo 的 mixed-port 监听）。内置来源不落库任何 URL，避免切回自定义时复活过期值；解析时对 `127.0.0.1:{MixedPort}` 做 TCP 监听探测，不可达则判定失败关闭（`docker.proxy.problem.managed_proxy_unavailable`），不会把守护进程指向无人监听的端口。
- **单一解析器**：`IDockerProxyResolver` 是两层唯一的取值来源（带 5 秒缓存 + 保存后 `Invalidate`），因此同一次保存不可能只作用于其中一层。
- **构建层无须宿主操作**：`DockerCliEngineService` 在 `docker build` 上追加**无值** `--build-arg HTTP_PROXY`（六种拼写），代理值由同一次解析写入该子进程的环境变量。两半必须成对使用（`DockerBuildProxy` 是唯一实现），无值写法让 `docker` 自己从环境变量取值，因此凭据不会出现在命令行（`ps`、任务管理器、shell 历史都看不到），也不会像 `--build-arg NAME=value` 那样被带进进程列表。该层不写宿主文件，也不写入镜像的 `Config.Env`。只有 build 命令会读代理配置，其余 docker 命令保持继承环境不变。`docker compose` 栈路径**不在覆盖范围内**，原因见本节末。
- **凭据处理**：代理 URL 可能内嵌 `user:pass@`。服务端加密存储；**返回界面时不做掩码** —— 已保存的偏好与守护进程自报的有效值都原样返回，只在日志、审计、问题码与层诊断中经 `DockerProxyValidation.MaskProxy` 去掉 userinfo。掩码只面向“非操作者”的读者：若把掩码值回填进表单，下一次保存就会把真实凭据改写成 `***`，等于损坏配置。特权 Helper 也从不回显收到的值（读回校验用文件内容比对而非 `systemctl show`，后者会打印环境变量）。
- **宿主全局**：Docker daemon 是机器资源而非租户资源，故配置为单行表、以最后一位获授权写入者为准，不采用镜像源的每用户模型；`updated_by` 仅用于可追溯。
- **确认语义**：安装/替换守护进程层会重启 Docker 并中断运行中的容器，因此 `SaveDockerProxySettingsRequest.Confirmed` 缺失时该层只返回 `docker.proxy.problem.confirmation_required` 而不写宿主；客户端在提交前用确认对话框取回该确认。移除时仅当本机确曾由 RelaxKonOS 写入过（`engine_applied`）才触碰宿主，避免为一次空操作重启 Docker。
- **读取 Docker Desktop 自身的配置**：Docker Desktop 始终让守护进程连到它自己的内部代理（`docker info` 报 `http.docker.internal:3128`），并把该内部代理的上游动态指向运维填写的地址。因此守护进程的自报值在该平台**永远看不到真实上游**。`IDockerDesktopProxyReader` 直接读取 `settings-store.json` 的 `ProxyHTTPMode` 与 override 键，页面单独展示“Docker Desktop 正在使用的代理”，并提供一键导入表单。
- **“已写入”不等于“已生效”**：守护进程层写入成功后状态先为 `RestartRequired`，再由回读判定。判定依据按平台区分：
  - **Docker Desktop**：以 `settings-store.json` 中实际保存的上游是否等于解析值判定（比较前归一化首尾空白、末尾 `/` 与大小写）。相等 → `Applied`；manual 模式但上游不同 → `RestartRequired` + `docker.proxy.detail.desktop_upstream_differs`；仍是 `system` 模式 → `RestartRequired` + `docker.proxy.detail.desktop_restart_pending`。这样不会因为“守护进程报了个非空代理”（那个代理其实是 Docker Desktop 的内部中转）而误判为已生效。该判定不依赖守护进程是否在运行。
  - **原生 Linux**：只有 `docker info` 回报非空 `HttpProxy`/`HttpsProxy` 才升级为 `Applied`，否则 `RestartRequired`。
- **为什么 Docker Desktop 改代理不需要重启引擎**：Docker Desktop 的 vpnkit 内部代理会在上游变更时**动态重配**，而守护进程指向的地址（内部代理）始终不变，所以引擎进程不需要重启（Docker 官方《How Docker Desktop Networking Works Under the Hood》即此结论）。相对地，原生 Linux 守护进程是从 systemd 单元的启动环境读取 `HTTP_PROXY`，属于启动期配置，必须重启 `docker.service` 才生效。本功能在 Windows 上因此只需重启 Docker Desktop 应用本身；这一步不可省略的原因不是引擎，而是**该应用在退出时会用内存中的设置覆盖 `settings-store.json`**，所以必须先停它再写、写完再启动。若 Server 无法驱动 Docker Desktop，写入仍然落盘，状态提示交由运维手动重启。
- **平台限制**：Linux 经特权 Helper 写入（路径、单元名与命令行均为常量，不接受任意路径/命令，不构成通用提权面）；Windows 走 Docker Desktop 自己的设置文件。其余平台报 `Unsupported`。Windows Server 上的 Docker Desktop 不属于受支持路径，见 §2.2。

#### 构建层：为什么用无值 `--build-arg`（2026-09-20 实测）

实测环境：Windows + Docker Desktop，docker client/server 29.8.0、buildx 0.37.0、Compose v5.5.1。验证方式是把 `env | grep -i proxy` 写进 Dockerfile 的 `RUN` 步骤，逐个机制实测。

| 目标 | Docker Desktop 自身设置 | 客户端进程环境变量 | `--build-arg NAME=value` | 无值 `--build-arg NAME` | `~/.docker/config.json` 的 `proxies` |
|---|---|---|---|---|---|
| 守护进程拉镜像 | ✅ 该设置本身就是这一层 | — | — | — | — |
| `docker build`（BuildKit） | ❌ | ❌ | ✅ | ✅ | ✅ |
| `docker build`（经典构建器，`DOCKER_BUILDKIT=0`） | ❌ | ❌ | ✅ | ✅（该模式已弃用：构建时会打印 legacy builder 将被移除的警告） | ✅ |
| `docker compose build` | ❌ | ❌ | ✅ | ✅ | ✅ |
| `docker compose up` 自动构建 | ❌ | ❌ | 无此参数 | 无此参数 | ✅ |
| `docker run` 容器内环境变量 | ❌ | ❌ | — | — | ✅（副作用） |
| 凭据去向 | Docker Desktop 设置文件 | 仅进程内存 | **命令行可见**（`ps`、任务管理器、shell 历史） | 仅进程内存 | **宿主文件明文** |

选定的是“客户端环境变量 + 无值 `--build-arg`”这一组合：

- 客户端环境变量本身**不生效**（上表那一列的 ❌ 就是它的全部作用面），但它是无值 `--build-arg` 的取值来源：`docker` 解析无值参数时从自己的环境读取，因此值不会出现在命令行上。两半由 `DockerBuildProxy` 一起产生，禁止分开使用。
- 不选 `--build-arg NAME=value`：功能相同，但会把带凭据的 URL 写进命令行，本机任何用户可见。
- 不选 `config.json` 的 `proxies`：它一处覆盖构建、compose、自动构建与容器运行时，但 ① 代理以明文写进宿主文件（而 Docker Desktop 自己也会重写该文件）；② 它同时把代理注入**之后创建的每个容器**的环境变量（实测 `docker run` 容器会多出六个变量），本产品要承载用户容器，等于顺手改了别人的运行期网络。
- **`docker compose up` 的自动构建覆盖不到**：`up` 不接受任何 build 参数（`--build-arg` 只在 `compose build` 上，而本服务只跑 `up`），Compose 也不会把客户端环境变量转给构建。栈里带 `build:` 的服务需先在 Manager 里构建镜像，或在 Compose 文件里自行声明 `build.args`。`DockerComposeService` 因此不做任何注入：保留一个已被实测否定的空操作，只会假装覆盖了这条路径。
- `docker info` 在 Docker Desktop 上恒报 `http.docker.internal:3128`（no_proxy 为 `hubproxy.docker.internal`），**不含真实上游**，不能用作生效判据——这也是引入 `IDockerDesktopProxyReader` 的原因。
- **代理地址必须能被构建容器访问**：构建步骤运行在容器里，`127.0.0.1`/`localhost`/`::1` 指向容器自身。实测该平台上，构建沙箱里既连不上宿主 `127.0.0.1`，也连不上 `host.docker.internal`（该别名在此环境只解析出 IPv6 `fdc4:f303:9324::254`，而构建沙箱没有 IPv6 路由，只有回环；同一别名在普通容器里可以连通）。因此内置代理（地址即 `127.0.0.1:{MixedPort}` 的监听端口）**用在构建层不会生效**：该层仍报 `Applied`（构建参数确实已传入），但附带 `docker.proxy.detail.build_loopback_unreachable` 警告，提示改用构建容器能访问的地址（例如宿主机的局域网地址）。

```text
客户端「网络代理」页 ──HTTPS+JWT──► /api/v1.0/docker/proxy
                                        │
                             IDockerProxyService
                          │            │            │
        IDockerProxyResolver   IDockerEngineProxyConfigurator   IDockerDesktopProxyReader
           （两层唯一取值）        （唯一的平台分支点）              （只读 Docker Desktop 上游）
                                        │
                       Linux: PrivilegedHelper → systemd drop-in
                       Windows: Docker Desktop settings-store.json
                       Build 层: DockerBuildProxy → docker build --build-arg（值走子进程环境，不落命令行）
```

### 3.6 引擎生命周期控制

Docker 引擎是整机资源，控制它中断的是**本机所有容器**，因此与按容器的 `/containers/{id}/{action}` 分开，独立为 `/docker/engine/{action}`，并要求 `server.docker.manage`：

| 动作 | Linux（原生守护进程） | Windows（Docker Desktop） |
|---|---|---|
| `start` | 特权 Helper 在 systemd 上执行 `systemctl start docker.service`；在 OpenRC/SysV 上执行固定的 Docker 服务动作 | `docker desktop start` |
| `stop` | `systemctl stop docker.service`；在 OpenRC/SysV 上执行固定的 Docker 服务动作 | `docker desktop stop` |
| `restart` | `systemctl restart docker.service`；在 OpenRC/SysV 上执行固定的 Docker 服务动作 | `docker desktop restart` |

- **只传动作，不传目标**：请求体只有 `confirmed`；单元名 `docker.service` 是 Helper 常量，平台标识与命令由 `IDockerEngineHostController` 自行决定，端点与客户端都不分支操作系统。因此该能力不会被用来操作任意服务或执行任意命令。
- **Linux init 识别**：控制路径先确认 systemd 运行时存在；没有 systemd 时依次使用固定的 OpenRC、`/etc/init.d/docker` 或 SysV `service docker` 入口。Docker 首次安装仍仅支持文档列出的 Ubuntu + systemd 路径，但已运行的原生 Docker 引擎不应因 init 系统不同而被误报为平台不支持。
- **不改变开机策略**：这些动作不会 `enable`/`disable` 单元，也不会改动宿主上的任何配置文件，仅改变当前运行状态。
- **确认语义**：`stop` 与 `restart` 必须带 `Confirmed`，否则返回 `docker.engine.problem.confirmation_required` 且不触碰宿主；`start` 不需要确认（它不中断任何东西）。客户端在点击后先弹确认对话框。
- **返回操作后状态**：响应同时携带动作后的 `DockerStatusDto`，客户端不必轮询即可知道引擎是否恢复；`stop` 之后守护进程不可达属于预期结果，由状态表达，不计作动作失败。
- **与安装流程的区别**：`DockerEngineInstall` 只负责首次安装与权限授予，安装后需要重启的是 RelaxKonOS Server 自身（见 `docker.access_restart_required`），不是引擎；本节的三个动作不涉及安装。

客户端结果约定沿用 Docker Manager 既有的“HTTP 200 + 结果 DTO”风格：状态对象同时携带两层结果、守护进程自报的有效值与问题码；仅当偏好本身不合法（`DockerProxyValidationException`）时返回 `400` 与 ProblemDetails，客户端读取其中的 `problemCode` 并本地化，未映射的码原样显示而不是被吞掉。

---

## 4. 安全、权限与审计

Docker daemon 的控制权相当于宿主机高权限。故默认原则是“只读可见、按操作授权、明确确认、审计可查”。新增目录权限：

| 权限 | 允许内容 |
|---|---|
| `server.docker.read` | 状态、资源元数据、脱敏配置、日志与事件读取 |
| `server.docker.manage` | 创建和改变容器/Stack/镜像/网络/卷、终端与导入导出、整机引擎启停与重启 |
| `server.docker.install` | 生成并执行 Docker 运行时安装、启动、升级计划 |

- `manage` 不蕴含 `install`；任何删除、强制停止、主机网络/特权容器、Docker socket 挂载、host PID/IPC、`--privileged` 或高危端口发布均须二次确认并说明风险。
- 表单里的 `password`、token、secret 和整个敏感环境变量值默认掩码；日志、审计和异常不得回显它们。代理 URL 的 userinfo（`user:pass@`）按同一规则处理，但**面向操作者的界面是例外**：该值必须原样返回，否则表单回填后再保存会把真实凭据改写成掩码。因此掩码只作用于日志、审计、问题码与诊断信息，见 §3.5。
- 应用只接受 local transport。若将来增加远程 Engine，必须使用 TLS、证书轮换、允许列表、显式环境配置及单独权限，不能复用本机默认。
- 审计事件最少记录操作者、时间、目标、动作、确认方式、结果和关联 `OperationId`；记录命令模板/结构化差异，不记录秘密。
- **输入卫生：不接受的文档必须拒绝，而不是替换**。Compose 定义在准入阶段被逐行检查，拒绝宿主文件系统与越权相关条目（`build`、`privileged`、`cap_add`/`cap_drop`、`devices`、`network_mode`、`pid`/`ipc`/`userns_mode`、`external`、bind mount 的绝对/相对来源、`/var/run/docker.sock` 与 Windows named pipe）并返回 `docker.compose_feature_unsupported`。变量引用同样被拒绝（`docker.compose_variable_unresolved`）：`docker compose config` 会把未设置的变量**静默替换为空串并 exit 0**，而本服务不提供变量输入面，放行等于让操作者批准一份、执行另一份；`$$` 是 Compose 的字面美元转义、注释不参与插值，两者都不算引用。准入不是第二个 Compose 实现：通过后仍由 `docker compose config` 做权威解析。

---

## 5. 实施顺序与验收

1. 定义 Protocol DTO/路由、权限和 `IDockerEngineService`，实现只读 status/containers/images/networks/volumes。
2. 实现 Unix socket 与 named pipe Provider、API 版本协商和 Ubuntu/Windows 探测；在 `Windows Server Test` 做 native transport 验证。
3. 交付容器详情、日志、统计、生命周期和审计，再交付镜像/网络/卷。
4. 增加 Compose 校验、Stack 部署与任务流；先支持本地文本/上传，再支持经过凭据引用的 Git 来源。**已完成**：定义预览（`stacks/preview`）、持久操作账本与协调器、项目级动作、取消、诊断、卷引用检查与 `delete` 保留命名卷；桌面与 Android 都消费同一持久契约。
5. 最后增加 Ubuntu 安装器和 Windows 引导安装器；安装、升级和回滚均须在干净 VM 中验证。

验收至少覆盖 Ubuntu 22.04/24.04 与 Windows 的可用 Engine：无 Engine、权限不足、API 不兼容、拉取失败、断流重连、Compose 失败回滚、运行中资源删除冲突、机密脱敏和审计完整性。代理功能另需覆盖：内置来源不可达时失败关闭、无确认时不下发守护进程层、写入后待重启与重启后生效两种状态、以及凭据在界面/日志/审计中的脱敏。任何平台仅在“安装 + hello-world + 管理 CRUD + 重启后恢复 + 卸载/故障路径”通过后才标记为支持。

Stack 操作的**无 Docker 依赖**部分已由 `RelaxKonOS.Server.Tests` 的 `PASS DOCKER STACK` 覆盖（路由与动作表、名称/版本/问题码规则、账本持久化与重开、幂等回放与冲突、按项目互斥、诊断逐行脱敏与限长、成功/部分失败/失败分类、确认删除、取消、重启核对不重放）。

**真实 Engine** 部分由同一套件的 `PASS DOCKER STACK LIVE` 覆盖（`--stack-live-only`，需要本机 `docker` 与 `alpine:3.20`，无 Engine 时明确 SKIP 而不是假通过）。它在真实 Compose 宿主上端到端跑：解析 → 部署一个「一服务常驻、一服务立刻退出」的项目 → 分类为 `partialFailed` 并记录真实观察结果 → 用修复后的定义更新为 `succeeded` → 同键同文档回放不新建操作 → 停止项目后卷仍被引用且拒绝删除 → 删除项目后命名卷保留、无引用时才可释放。**这项校验发现了两个只有在真实宿主上才会暴露的缺陷**：`ListServicesAsync` 与 `ListAsync` 的 `--format` 模板把标签名写成 `\"name\"`（面向 shell 的转义），而该参数是直接进 `ProcessStartInfo.ArgumentList` 的，Docker 因此以 `failed to parse template: unexpected "\\" in operand` 退出 1、观察结果恒为空——真实宿主上每一次成功部署都会被误判为部分失败，停止项目也不会出现在列表里；改为正确的模板引号后修复。另一个：普通未设置变量（`${NAME}` / `$NAME`）会被 `docker compose config` 静默替换为空串且 exit 0，定义会被「批准一份、执行另一份」，现在在准入阶段以 `docker.compose_variable_unresolved` 拒绝（见 §4「输入卫生」）。

**仍未验收**：以上都是服务端 + 真实 Engine 的证据。带认证的 HTTP 往返（`202` + 轮询）与 Android/iOS 真机矩阵仍未执行——登录需要真实宿主凭据，不能在验收里绕过。任何平台仍只在「安装 + hello-world + 管理 CRUD + 重启后恢复 + 卸载/故障路径」全通过后才标记为支持。

## 桌面执行状态与日志

参考 VS Code 的状态栏与输出面板，以及 JetBrains Services 工具窗口，将任务状态和日志分层组织。Docker、证书与 Web 服务管理器使用窗口底部的统一交互：默认只占一行（38 个逻辑像素），显示简短任务、执行进度和日志入口。长消息省略显示，悬停可查看完整内容。日志默认隐藏，点击入口向上展开 180 个逻辑像素的输出区域，再次点击收起；打开或收起日志都不会停止任务。结束后保留结果和日志，Docker 可关闭已结束的状态记录。

Compose 操作轮询持久操作记录与诊断快照，阶段变化记入日志，快照更新替换旧输出以避免重复。命令输出的可见时间取决于服务端诊断记录的更新时机；这不是逐行输出流。客户端持续观察到终态，不会因本地十分钟观察期限而把仍在运行的任务显示为结束。证书与 Web 服务管理器的日志记录带时间的阶段与结果消息。

参考资料：
- https://code.visualstudio.com/docs/editing/getting-started/userinterface
- https://www.jetbrains.com/help/idea/using-services-tool-window.html

### 组件中心只读状态（2026-10-25）

组件中心通过既有 Docker 状态接口显示 API 可用性和实测版本，不推导系统包所有权，也不把 API 不可用显示成未安装。Windows 宿主上的 Linux 引擎仍使用宿主平台标记。组件安装迁入与版本事务仍未实现，跟踪见[组件实施进度](../platform/RelaxKonOS.ComponentCenter.Progress.md)。


### 桌面安装入口（2026-10-26）

本应用的宿主组件安装已移至组件中心，应用内不再提供安装、更新、修复、回退或卸载入口，也不保留常驻横幅；设置页的共享“组件依赖”区显示宿主实测状态并提供跳转（Project Editor 为“工具 → 组件依赖”）。业务配置与服务管理保留。各平台安装能力、冻结计划与台账路径见[组件中心实施进度](../platform/RelaxKonOS.ComponentCenter.Progress.md#桌面组件安装入口统一2026-10-26)。此项仅集中入口，不扩大运行时、信任来源、多版本更新或平台验收范围。

### 栈操作中断与原回执恢复（2026-11-06）

服务端取消或超时后先清理本地 Docker CLI 并排空输出，清理无法核实则保留原台账和恢复责任。协调器重启后保留原 `Running / Applying` 回执、工作责任及同项目写入门禁。已进入观察阶段的操作可读取真实 Engine 并按原 operationId 记录 Interrupted。相同请求和幂等键始终读回原回执。Engine 已接受的动作可能在 CLI 断开后完成。见[模块边界台账](../architecture/server-module-evidence/Docker.md#mb-05-docker-cli-清理未知的持久门禁)。


2026-11-06 MB-05 隔离预检：当前共享测试机的只读复核确认普通测试账户无 Docker socket 权限且非特权 user namespace 创建失败。独立 Engine daemon 重启、整个 Server 进程故障和真实 HTTP 响应排空仍待验收。 [验收台账 §50](../architecture/server-module-evidence/Milestones.md#50-mb-0305-隔离测试设施复核2026-11-06)

以下早期记录保留当时的实测终态。当前版本对原 `Applying` 回执使用本节末的清理门禁。


Windows Release 已通过 Docker 跨进程回执夹具。真实测试进程在持久 Running 回执和受控部署见证写入后被终止，新进程保留原 operationId 并记录 Interrupted，同键请求返回相同回执且没有新增部署见证。真实 Engine 故障验收仍见边界设计 §45/50。

同一 Release 源码在既有 Linux 测试机分别通过跨进程回执夹具和真实 Engine 专属项目中断续核。操作 ID、制品摘要和清理结果见边界设计 §50。

既有 Linux 测试机已通过真实 Engine 与 Docker 工作进程崩溃联合验收。原 CLI 和容器状态得到独立确认后，恢复协调器以原 operationId 持久化 Interrupted，重复请求不再次停止容器。证据和验收范围见边界设计 §50。

MB-05 已在既有 Linux 测试机验证专属 Engine 重启后的容器与命名卷连续性。专属 daemon 和 containerd 使用独立 socket 与数据目录。原容器按 restart policy 重新运行并读取原卷内容，测试资源随后清理。原操作回执与 daemon 中断的联合验收仍开放。见边界设计 §54。

MB-05 也验证了独立 Engine 在真实停止操作期间中断。daemon 重启后，协调器按原 operationId 持久化 Interrupted，同键重试未增加停止日志。见边界设计 §54。

专属 dockerd 的 SIGKILL 路径也已通过实机恢复。daemon 以 137 退出，重启后按原 operationId 取回 Interrupted 回执，恢复前后的停止次数一致。见边界设计 §54。

MB-05 在隔离 Engine 上通过了 Docker HTTP 客户端断开验收。响应头写出前断开连接，已接受的真实停止操作继续到终态持久化，同一 HTTP 幂等键重试返回原 operationId，未再次停止容器。见边界设计 §54。

HTTP 断开场景已使用生产 Docker resident 工作租约复测。终态事件仍在租约保护下，持久化后排空。见边界设计 §54。

Docker stack 的 HTTP 202 回执在响应执行期间持有独立工作租约。Linux 专属 Engine 验证了客户端断开、业务终态已落盘且响应仍在执行的路径。响应任务退出后归还租约，同键重试仍返回原操作。

Linux 隔离 Engine 已通过完整 System Mode Server 的认证回执验收。匿名请求被拒绝，通过专属 PAM Helper 登录后执行真实停止，重启并重新登录后同键取回原终态，停止只执行一次。

完整 Server 实机复测覆盖了客户端未读取原停止响应。按原幂等键确认受理后关闭连接，重启并重新认证仍取回原 Succeeded 回执，停止未重放。

完整 Server 已通过停止仍在执行时的强制中断验收。Engine 已接受停止且原回执仍为 Running 时终止 Server，重启并重新认证后同键取回原 Interrupted 回执，停止未重放。

完整 Server 重启前重新启动同一专属容器后，已验证原键重试仍返回原中断回执，且容器保持运行。按原 operationId 的查询也返回同一回执。

MB-05 Compose 的执行图在原协调器持久化准入后按操作创建，异步清理结束后才写终态。Linux 私有 Engine 强制中断与完整 Server 经认证停止请求中断均通过原回执恢复验收。恢复只观察容器状态，同键重试未再次停止容器。Docker 管理模块保持 resident。见[模块边界台账](../architecture/server-module-evidence/Docker.md#mb-05-docker-compose-准入与执行作用域)。

Docker Engine 离线时，服务列表返回 `503 / docker.unavailable`。Server 启动后保留等待核验的原栈操作，原键仍返回原回执，同一项目的新变更返回 HTTP 409。Engine 恢复后，原协调器读取项目状态并以原 operationId 写入 Interrupted。完整认证 Server 与私有 Engine 联验通过。见[Engine 离线启动对账](../architecture/server-module-evidence/Docker.md#mb-05-docker-engine-离线时的原启动对账)。

变更命令返回后若 Engine 暂时无法提供观察，原操作保持 Running / Observing。进入只读恢复后，Server 继续观察原项目，直到能以原 operationId 记录 Interrupted。同项目的新变更在此期间返回 HTTP 409。完整认证 Server 已在私有 Engine 上通过首次观察实际断线验收。见[变更后的观察中断](../architecture/server-module-evidence/Docker.md#mb-05-docker-变更返回后的-engine-观察中断)。

用户取消已派发的变更时，若 Engine 暂时无法观察，原操作继续显示 Running / Observing。Server 完成原 CLI 清理并等待只读观察，以原 operationId 写入 Interrupted。同项目在观察恢复前保持互斥。完整认证 Server 已在私有 Engine 上验证首次观察取消、观察断线及原回执接续。见[取消与观察断线](../architecture/server-module-evidence/Docker.md#mb-05-docker-用户取消与观察断线交叠)。

Compose 列表、预览、定义与服务读取在认证后取得工作租约。CLI 与输出清理、实际响应及作用域结束后归还租约，无法核实 CLI 清理时保留故障责任。见[Compose 只读 HTTP 工作责任](../architecture/server-module-evidence/Docker.md#mb-05-docker-compose-只读-http-工作责任)。

Compose CLI 执行、文件处理和输出解析使用按需加载的程序集。最后一个执行作用域结束 30 秒后可卸载，新工作会唤醒执行代码。容器由 Engine 持续管理，原操作回执与恢复由 Server 台账负责。见[Compose 独立执行程序集](../architecture/server-module-evidence/Docker.md#mb-05-docker-compose-独立执行程序集)。

Linux 私有 Engine 已实测在 `docker stop` 仍运行时取消。CLI 退出后，Engine 若暂时无法观察，原操作继续占用项目写入门禁和工作租约。恢复后原 ID 只读结案，同键仍返回原回执。见[变更 CLI 在途取消](../architecture/server-module-evidence/Docker.md#mb-05-docker-变更-cli-在途取消与-engine-观察中断)。

Compose 命令退出但进程或输出清理仍无法确认时，原操作保留活动回执和项目写入门禁。Server 重启后仍可按原键及原 ID 查看回执。即使此前未请求取消，重启也按 `Applying` 阶段保留门禁。Engine 的容器快照不足以证明原 CLI 和输出已排空。见[CLI 清理未知](../architecture/server-module-evidence/Docker.md#mb-05-docker-cli-清理未知的持久门禁)。
