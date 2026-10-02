# Android 公共运行时安装链路

> 当前功能说明，对应 BP01-M1-1～BP01-M1-5。公共数据层、安装任务观察、恢复与取消已接入；Nginx 表单与实例管理已随 BP03-M1 接入，见 [Nginx 管理](Nginx.md)；FRP 运行时表单已随 BP05-M1 接入，见 [FRP 客户端与运行时](Tunnels.md)；Mihomo 表单已随 BP06-M1 接入，见 [Mihomo 管理器](Proxy.md)；SMB 安装和文件服务已随 BP08-M1 接入，见 [SMB 文件服务](Smb.md)；Docker Linux 安装与原任务恢复已随 BP09-M1 接入，见 [引擎与镜像源](DockerEngine.md)；其他服务表单与完整管理流程仍随 BP10 交付。当前测试状态和未关闭检查看 [Verification](../status/Verification.md#1-bp-测试进度)，设备与真实安装尚未验收。

## 1. 契约与支持边界

[Installations.kt](../../app/src/main/java/app/relaxkonos/mobile/core/net/Installations.kt) 直接投影共享 [InstallationContracts](../../../../Shared/RelaxKonOS.Protocol/Installations/InstallationContracts.cs)。请求没有服务器路径或命令字段；路由与共享 InstallationApiRoutes 一致，JSON 枚举遵循 Server 的 camelCase 配置。未知枚举、无效操作 ID、缺少必需字段或异常阶段进度均作为传输/契约不可用处理，不推断成功。

| 服务 | 公共安装动作 | 安装包与回滚 |
| --- | --- | --- |
| SMB、Docker、Git | Install | 使用服务端安装来源 |
| Nginx | Install / Upgrade / Repair / Uninstall | 包引用仅用于 Install |
| FRP、Mihomo | Install / Upgrade / Repair / Uninstall | 包引用仅用于无回滚的 Install；回滚使用 Repair + rollback |

这是当前 Server 安装服务的动作上限，不代表所有宿主一定支持执行。登录响应的 `server.host.capabilities.privilegedOperations` 为真且相应服务能力存在时，Repository 才能准备提交、运维中心才自动发现安装任务。User Mode 的 Git 浏览能力不代表可以安装 Git。权限、宿主运行模式与实际安装条件继续由 Server 校验；服务专属界面仍需核对各领域当前能力。

## 2. API、提权与包来源

[RelaxKonGateway](../../app/src/main/java/app/relaxkonos/mobile/core/net/RelaxKonGateway.kt)、[RelaxKonApi](../../app/src/main/java/app/relaxkonos/mobile/core/net/RelaxKonApi.kt) 和 [InstallationRepository](../../app/src/main/java/app/relaxkonos/mobile/data/InstallationRepository.kt) 提供提交、按 ID 查询、按服务查询活动任务、取消、创建服务器文件引用与手机包上传。活动查询的无正文 404 表示没有活动任务；带问题码的拒绝和普通操作查询 404 保持失败/缺失语义。

手机包接收现有 `PickedDocument`，以 `package` multipart 字段流式发送到专用暂存端点，不借用普通文件上传地址。已知和实际读取长度都受 128 MiB 限制；源流在每次受认证授权的 401 重试时重新打开，未知长度不把整个包读入内存。上传仅产生限时引用，不自动安装，不提供续传或后台任务承诺。服务器文件来自现有远端文件选择能力，其路径只进入创建引用请求。

安装请求只携带 FileReferenceId。引用模型提供失效时间；文件不可用或过期提示重新选择，由 Server 在使用时再次验证身份、服务、长度和有效期。包引用不会写入任务索引。暂存完成与安装完成是两个独立事实。

### 安装来源与自行下载

Mihomo、FRP 和 Windows 宿主的 Nginx 安装表单统一提供“服务器下载官方包”“从服务器选择安装包”“从手机选择安装包”，来源选项整行可点击。服务器来源提供远端文件浏览和限时引用；手机来源使用 SAF 选包并上传。切换来源、版本或服务器路径会清除旧包引用，过期引用不能提交安装。Nginx 在版本目录可用时默认选择稳定版，未提供稳定版则使用主线或目录首项；Mihomo 使用宿主推荐版本。

“我要自行下载”直接显示与当前版本匹配的官方下载 URL，可选择文字、复制链接或交给系统浏览器打开。Mihomo 使用宿主受信版本列表里的 URL；FRP 使用运行时下载查询；Windows Nginx 使用 `GET /api/v1.0/webservers/managed/download?version=...`。失败可再次点击重试；不推测或拼接未获宿主确认的官方包地址。切换版本后隐藏旧链接。浏览器打开不携带 RelaxKonOS 会话凭据。

自行下载后，用户仍需选择服务器或手机上的文件，准备限时引用并显式确认安装。获取 URL、复制或打开链接都不会上传或自动安装。包引用仍仅支持 Install；FRP/Mihomo 的 Upgrade/Repair 沿用宿主下载，回滚和卸载不显示包来源及自行下载操作。Linux Nginx 继续使用宿主包管理器安装，不显示 Windows ZIP 来源。

提交复用 AuthSession 的一次 401 刷新和 ElevationRepository 的一次显式提权；六类服务使用对应安装 capability 与服务目标。重试闭包保持同一幂等键，管理员密码使用可清零字符数组。提权提示、刷新重试、请求和返回结果都校验当前会话；切账号或宿主后的旧操作不得发给新会话或覆盖新显示。

## 3. 幂等与恢复

领域界面确认后调用 `prepare(owner, kind, request)`，保留返回的 InstallationSubmission，再调用 `submit(intent, provider)`。同一未决请求重新准备必须保持相同服务、动作与选项摘要；显式重试复用原键。Repository 不自动重试无结论的传输失败；显式重试先查询活动事实，再以原键和原选项调用幂等入口。保有原操作 ID 时只查询该 ID。

[InstallationRequestJournal](../../app/src/main/java/app/relaxkonos/mobile/data/InstallationRequestJournal.kt) 在 `noBackupFilesDir` 保存服务器/账号、服务、动作、幂等键、选项 SHA-256 摘要、是否已尝试以及可用的原操作 ID。它不保存请求正文、路径、引用载荷、密码或状态推断。存储损坏/写入失败会阻止创建新提交键；未决请求不因容量限制而自动逐出。

成功响应先持久化原操作 ID，再写 OperationIndex，最后清除未决提交。索引和未决记录分别支持进程回收后的查询。明确的首次前置拒绝可结束这次提交；曾有未知结果的请求保持未决，后来的拒绝不能证明原任务没运行。

当前 API 提供按 ID 和活动任务查询，没有按幂等键只读查询。首次响应完全丢失且任务已结束时，活动列表不能确定原任务 ID 或终态：运维界面持续显示提交待核实，不把其他同服务活动任务绑定为该请求的结果，也不将空列表判为失败。后续原服务流程可在用户明确重试时重建相同选项并复用持久键；持有原 ID 时可在运维中心手工查询。FRP 页面另提供“明确识别原操作”的确认：`identifyOriginal` 校验返回的服务、动作与已保存 ID（若有）后才结束匹配未决提交并恢复观察。该路径依赖用户识别，不能将任意同服务 ID、活动事实或空列表自动绑定到原键。

## 4. 运维中心

“管理 → 任务与恢复”发现当前能力允许的六类活动安装任务，补录安装领域索引并逐一核实已保存的原操作 ID。详情显示服务、动作、状态、阶段、**当前阶段进度**、稳定问题提示和核验时间；未识别的服务问题码显示通用诊断提示，不直接显示第三方原始错误。诊断导出包含安装服务、动作与阶段进度，不保存安装请求正文或包数据；此契约没有安装日志端点。

可输入原操作 ID 恢复查询，宿主成功核实后重新显示该账号此前在本机隐藏的记录；普通活动扫描仍尊重隐藏标记。Nginx、FRP、Mihomo、SMB、Docker 安装项可返回已实现的领域管理页，按当前服务能力门控；其他服务表单交付后再接入跳转。当前页面每五秒尝试刷新活动或不可用记录，前一次读取尚未完成时不重叠刷新；离页/切会话停止观察并隔离旧响应，停止观察不取消服务端任务。取消经确认后提交稳定键，再查询真实状态；Running + 不可取消仍是执行中，只有服务端 Cancelled 才显示已取消。查询失败保持待核实，本机隐藏只隐藏观察记录。

未关闭设备/宿主检查统一见 [Verification](../status/Verification.md#1-bp-测试进度)，实现进度见 [Progress](../status/Progress.md#2-bp-实现进度)。
