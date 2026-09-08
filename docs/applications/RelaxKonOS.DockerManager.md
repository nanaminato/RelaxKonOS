# RelaxKonOS DockerManager 设计

> 内置 Docker 管理器。它管理 **RelaxKonOS.Server 所在宿主机** 的本地 Docker Engine；客户端只负责本地 UI 渲染，不直连 Docker socket、不保存 Docker 凭据，也不将守护进程 API 暴露到网络。
>
> 当前状态：**已实现**本机 Engine 状态、容器/镜像/网络/卷列表、容器生命周期与安全的原地重命名，以及网络、卷和容器的只读详情查看。Compose 编排支持项目列表、校验、编辑、`up` 部署、服务查看和项目级启动/停止/重启。编排会展示 Compose 文件来源；点击来源可路由至内置文件浏览器。RelaxKonOS 部署的 Compose 文件保存在服务器受管目录，停止后的项目仍会显示。安装、持久化 Stack 历史、终端、流式统计和审计仍为**设计中**。
>
> - 架构与内置应用边界：[`RelaxKonOS.Architecture.md`](../architecture/RelaxKonOS.Architecture.md)
> - 协议契约规则：[`RelaxKonOS.Protocol.md`](../architecture/RelaxKonOS.Protocol.md)
> - 权限与危险操作：[`RelaxKonOS.Security.md`](../platform/RelaxKonOS.Security.md)
> - 内置应用通用约束：[`RelaxKonOS.BuiltInApplication.Conventions.md`](../development/RelaxKonOS.BuiltInApplication.Conventions.md)

---

## 1. 目标、范围与非目标

`RemoteDockerManager`（应用 ID：`remoteos.docker`）面向单台 RelaxKonOS Server，覆盖 Docker Engine 的完整日常运维闭环：发现或安装运行时、验证、镜像和容器生命周期、Compose Stack、网络与卷、日志/终端、资源统计、备份与审计。

设计参考了 Portainer 的环境、Stack、模板、镜像仓库与按角色授权的组织方式，以及 Docker Engine 的版本化 API；但 v1 **只管理本机单一 Engine**，不实现 Swarm/Kubernetes、多节点代理或远程 TCP Docker API。Docker Engine API 本身是面向 daemon 的版本化 REST API；Compose 用于描述多容器服务、网络和卷。[Docker Engine API](https://docs.docker.com/reference/api/engine/) [Compose 文件参考](https://docs.docker.com/compose/compose-file/) [Portainer 文档](https://docs.portainer.io/)

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

---

## 2. 体验与信息架构

主窗口使用左侧导航和右侧工作区，初始尺寸 `1180 x 760`。没有 Engine 时只显示“开始使用”页；安装、升级、切换容器模式等长任务采用任务抽屉与可恢复进度，不冻结窗口。

```text
概览
├─ 运行时与安装
├─ Containers       列表 / 详情 / 创建
├─ Stacks           Compose / 模板 / 部署历史
├─ Images           本地镜像 / 拉取 / 构建
├─ Networks
├─ Volumes
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
| Ubuntu 22.04/24.04 LTS | 本机 Unix socket `/var/run/docker.sock` | 官方 APT 仓库安装 `docker-ce`、CLI、`containerd.io`、Buildx、Compose 插件 | 先检查冲突包、防火墙和现有数据；Docker 文档特别指出 Docker 发布端口会绕过部分 UFW/firewalld 规则，必须在向导中警告。 |
| Windows 10/11 | 本机 named pipe `npipe://./pipe/docker_engine`，以探测到的 Linux 或 Windows 容器模式工作 | 仅引导安装已获许可的 Docker Desktop + WSL 2/Hyper-V；安装程序由用户选择并以管理员权限运行 | Docker Desktop 的 WSL 2/Hyper-V 选择和许可由用户负责。 |
| Windows Server | 管理已安装且经能力探测合格的本机 Engine-compatible runtime | **不自动安装**；运行时供应商、容器模式与许可由管理员明确选择后再增加专用安装提供方 | 防止将桌面安装器误作为生产服务器自动部署方案。 |

Ubuntu 方案以 Docker 官方安装文档为唯一命令来源；该文档要求先移除冲突包，推荐官方 APT 仓库，并以 `hello-world` 验证。[Docker Engine on Ubuntu](https://docs.docker.com/engine/install/ubuntu/) Windows 端 Docker Desktop 的安装需要选择 WSL 2 或 Hyper-V 后端。[Docker Desktop on Windows](https://docs.docker.com/desktop/setup/install/windows-install/)

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
- `IDockerComposeService` 只接受结构化 `StackDefinition`，在服务器受控工作目录写入临时 Compose/`.env` 文件，调用经过白名单构造的 `docker compose` 子命令；不得拼接用户 shell 字符串。已部署的 Compose 源默认放在 Linux `/var/lib/remoteos/docker-compose`、Windows `C:\\ProgramData\\RemoteOS\\docker-compose`，绝不写入安装目录或项目源码；管理员可通过绝对路径配置 `DockerCompose:DataDirectory` 覆盖。开发环境默认使用当前用户的 LocalApplicationData 目录。
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
| GET/POST | `/api/v1.0/docker/stacks` | read/manage | Compose 项目列表、定义校验与部署；可读取项目服务并执行启动/停止/重启 |
| GET/POST/DELETE | `/api/v1.0/docker/images|networks|volumes` | read/manage | 资源管理，删除前依赖检查 |
| GET | `/api/v1.0/docker/events` | `server.docker.read` | 过滤后的事件和审计只读流 |

长任务（拉取、构建、部署、导入导出、安装）返回 `OperationId`，以通用 SignalR 任务通道推送阶段、百分比、可本地化消息键和终态。日志与终端必须设置最大帧、速率限制、取消和断连清理；浏览器/客户端不保留 raw Docker stream。

### 3.3 持久化边界

Docker Engine 仍是容器、镜像、卷、网络和运行状态的真源；RelaxKonOS 不复制这些实体到 SQLite。SQLite 仅保存：

- Stack 草稿、已部署 Compose 内容的加密版本快照、来源和部署结果；
- Registry 配置元数据及对 OS 安全存储中机密项的引用；
- 用户偏好、可恢复任务摘要和审计记录；
- 管理器不保存 Docker socket、daemon TLS 私钥、Docker Desktop 账户令牌或明文 `.env` 秘密。

### 3.4 Docker Hub 镜像源

镜像源在内置“设置 → 镜像源”中按 RelaxKonOS 账户配置，而不是写入宿主机的全局 `daemon.json`。用户可维护多个 HTTPS、Docker Hub 兼容的 registry host，并选择其中一个或“默认”。

- 默认：原样执行 `docker pull mysql:8.4`，由 Docker 使用默认 registry。
- 选中镜像源：服务端从数据库读取当前用户的选择，将 Docker Hub 引用转换为 `{mirror}/library/mysql:8.4` 后再调用 Docker CLI。
- 显式 registry（例如 `ghcr.io/owner/image`）不转换，避免把第三方镜像错误发送至 Docker Hub 镜像源。

镜像地址不会由 Docker Manager 客户端随拉取请求发送，因此客户端不能替换其他用户的服务端选择；未来可通过 `ImageMirrorTarget` 扩展到其他镜像类服务。

---

## 4. 安全、权限与审计

Docker daemon 的控制权相当于宿主机高权限。故默认原则是“只读可见、按操作授权、明确确认、审计可查”。新增目录权限：

| 权限 | 允许内容 |
|---|---|
| `server.docker.read` | 状态、资源元数据、脱敏配置、日志与事件读取 |
| `server.docker.manage` | 创建和改变容器/Stack/镜像/网络/卷、终端与导入导出 |
| `server.docker.install` | 生成并执行 Docker 运行时安装、启动、升级计划 |

- `manage` 不蕴含 `install`；任何删除、强制停止、主机网络/特权容器、Docker socket 挂载、host PID/IPC、`--privileged` 或高危端口发布均须二次确认并说明风险。
- 表单里的 `password`、token、secret 和整个敏感环境变量值默认掩码；日志、审计和异常不得回显它们。
- 应用只接受 local transport。若将来增加远程 Engine，必须使用 TLS、证书轮换、允许列表、显式环境配置及单独权限，不能复用本机默认。
- 审计事件最少记录操作者、时间、目标、动作、确认方式、结果和关联 `OperationId`；记录命令模板/结构化差异，不记录秘密。

---

## 5. 实施顺序与验收

1. 定义 Protocol DTO/路由、权限和 `IDockerEngineService`，实现只读 status/containers/images/networks/volumes。
2. 实现 Unix socket 与 named pipe Provider、API 版本协商和 Ubuntu/Windows 探测；在 `Windows Server Test` 做 native transport 验证。
3. 交付容器详情、日志、统计、生命周期和审计，再交付镜像/网络/卷。
4. 增加 Compose 校验、Stack 部署与任务流；先支持本地文本/上传，再支持经过凭据引用的 Git 来源。
5. 最后增加 Ubuntu 安装器和 Windows 引导安装器；安装、升级和回滚均须在干净 VM 中验证。

验收至少覆盖 Ubuntu 22.04/24.04 与 Windows 的可用 Engine：无 Engine、权限不足、API 不兼容、拉取失败、断流重连、Compose 失败回滚、运行中资源删除冲突、机密脱敏和审计完整性。任何平台仅在“安装 + hello-world + 管理 CRUD + 重启后恢复 + 卸载/故障路径”通过后才标记为支持。
