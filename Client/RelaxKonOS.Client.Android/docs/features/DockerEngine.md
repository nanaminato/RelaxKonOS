# Android Docker 引擎与镜像源

引擎状态及生命周期操作集中为卡片，镜像源各自成卡。Docker 工作区新增“操作记录”分类，安装进度、取消、恢复、原请求重试和引擎待核实变更集中显示；容器/镜像/网络/卷的已有 ViewModel 结果与待核实标记也在此分卡展示，日志须显式展开。资源页提供记录跳转而不重复展示操作详情；记录不新增完整持久历史。

服务器明确返回 `docker.not_installed` 时，Docker 首页只显示安装入口和一条未安装引导，不展示依赖引擎的 Compose/容器/镜像/网络/卷读取错误或创建入口。引擎管理页显示未安装事实与安装操作，隐藏没有运行时可执行的启停按钮；读取失败、权限拒绝或运行时停止不能当作未安装。镜像源配置仍可独立管理，组件安装后的资源功能继续按已有流程展示。

> BP09-M1 已接入。容器/镜像/网络/卷新增管理动作接续 BP09-M2；已有 Compose 行为见 [Docker 与 Compose](DockerCompose.md)。测试状态和设备/宿主待验收项目见 [Verification](../status/Verification.md#1-bp-测试进度)。

## 引擎与安装

刷新与安装轮询使用固定高度的加载条占位，不插入或移除布局行，保留页面滚动位置。其他管理页面采用同一组件。

Linux 部署显式启用 `--docker-access` 时，即使 Docker 尚未安装，也先创建 Docker 系统组并授予 Server 服务账户成员资格。后续安装由 Helper 再次确认授权，Server 验证实际引擎连接；已有访问权限时完成安装，无需额外重启。旧部署缺少运行中的组权限时，显示 `docker.access_restart_required` 的明确说明：在宿主重启 RelaxKonOS Server 后刷新核验。未启用 Docker 授权的部署仍需在宿主显式配置授权。

管理 → Docker → 引擎与镜像源，入口按当前 `server.docker` capability 门控。页面读取当前 Engine 状态、版本、系统与架构，成功返回 unavailable 与请求未被核验分开显示；只有当前会话的 privilegedOperations 为真才显示写入动作。引擎不可达不自动等于未安装。

Start/Stop/Restart 调用当前 `/docker/engine/{action}`，提交结构化 confirmed。确认指出整个宿主、所有容器、Compose 与应用部署受影响。Linux 使用宿主 Helper 的固定 Docker 服务；Windows 依赖 Docker Desktop 的受支持 CLI。

只有 Linux、`docker.not_installed` 和管理资格满足时提供自动安装；Windows 显示主机侧安装/配置说明。安装复用公共 `DockerInstallationRequest(confirmed)` / Install，没有包、版本、升级或隐藏重装参数。明确 `dockerInstall` / `docker` 提权后复用原幂等键。成功接受任务后按原 ID 查询，页面可见且记录已核验时每 1.5 秒观察；取消需要服务返回 active/cancellable 并再次确认，离页只停止观察。

已知未知提交 ID 优先只读恢复，不依赖活动列表成功；原 ID 完全丢失时可由用户明确识别原请求。任务响应必须属于 Docker / Install。未知安装结果保留恢复标记，不能以空活动列表或 Engine 当前可达证明原安装成功。

刷新读取历史安装记录时，记录不存在、读取失败和历史任务失败独立于当前引擎事实。历史记录查询失败只在操作记录页显示记录 ID 与“原操作记录不可用／结果待核验”，不弹出当前 Docker 状态错误。保留原 ID 恢复入口和未知提交标记；不因引擎可用而删除或推断原安装结果。

## 用户镜像源

镜像源属于当前登录账户，为符合条件的 Docker Hub 镜像引用提供解析前缀。它不修改 daemon registry-mirrors，也不替代 [宿主出站代理](OutboundProxy.md) 的网络消费范围。显式其他 registry 引用保留原来源，已有镜像/容器不随选择变化自动重新拉取。

页面读取默认解析源、保存源和真实选择状态；默认源使用零 UUID 但不可编辑/删除，选择默认提交 `mirrorId:null`。其他源支持创建、完整名称/endpoint 编辑、选择和删除；删除当前源后重新读取默认选择。端点允许 registry host/可选端口或 HTTPS 根 URL，拒绝凭据、路径、查询和片段。点击确认后提交冻结草稿，刷新、返回、离页或打开代理设置前保护未保存字段。600 dp 可用宽度分栏，小屏切换列表与编辑。

## 同步结果未知与运维入口

引擎控制和镜像源 CRUD/选择没有任务 ID、取消或网络幂等键，不能按安装或 Compose 任务处理。提交前持久保存动作、当前宿主/账户及可选镜像源 UUID；本地 markerId 不发送到 Server，名称、endpoint、请求正文和凭据均不保存。提交、认证重试前再次读取并比较批准事实；发生变化时拒绝继续。此比较不构成 Server 原子 CAS。

写入失败、连接中断或成功后的事实读取失败保留未知记录，页面阻止继续提交引擎/镜像源变更；普通刷新不清除记录。显式读取并接受当前事实解除本地标记，不证明原请求成功，也不重放原写入。运维中心展示该待核实入口和安装原任务，按当前宿主/账户/capability 隔离；同步操作不伪造远端任务。

Android 同步 Docker 结果直接使用共享契约的 `logLines/logTruncated`，不再解析旧 `messages`。详细 daemon 原始诊断不进入普通错误文案。

## 代码

- [DockerControl.kt](../../app/src/main/java/app/relaxkonos/mobile/core/net/DockerControl.kt)：当前类型、路由、strict 读取与镜像源校验。
- [DockerControlRepository.kt](../../app/src/main/java/app/relaxkonos/mobile/data/DockerControlRepository.kt)、[DockerControlJournal.kt](../../app/src/main/java/app/relaxkonos/mobile/data/DockerControlJournal.kt)：提交前事实复核、会话隔离和未知记录。
- [DockerControlScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/docker/DockerControlScreen.kt)、[DockerControlViewModel.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/docker/DockerControlViewModel.kt)：引擎、安装恢复和镜像源页面。

资源管理及 Compose 新提交共用 DockerMutationGate；资源未知标记也会阻止引擎/镜像源及 Docker 安装新写入。资源恢复从 [Docker 资源](DockerResources.md) 进入。
