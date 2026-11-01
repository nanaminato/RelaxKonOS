# 服务端 ModuleHost v2 实施记录

更新：2026-11-01。这里只记录当前实现与证据，不将设计目标视为已交付。

| 跟踪项 | 当前状态 | 已有证据 / 余项 |
| --- | --- | --- |
| MD-01 唯一 v2 契约 | 已实现并回归 | manifest / installed / package-plans 使用 schemaVersion=2，拒绝 v1；打包器、CLI、SDK、Server 与桌面同步 |
| MD-05 Linux 身份与权限 | 已实现；独立运行时实机验证 | 每模块稳定专用 UID/GID，无附加组、no_new_privs；root 拥有只读版本快照，模块拥有私有数据；root 监督器控制超时/断管清理 |
| MD-05 Windows 身份与 ACL | 已实现；原生隔离验收通过 | LocalSystem Helper、两个真实专用账户/S4U、只读快照/私有数据 ACL、认证管道、跨模块拒绝和 Job 后代清理共 14 项实测通过；完整安装/更新仍待验收 |
| MD-06 安装与代码信任 | 已实现并回归 | 冻结计划展示 runtime/privileges；精确信任绑定版本/发布者/归档与清单摘要；更新不继承，撤销立即阻断代理/Broker |
| MD-07 / PB-01/02 Broker 核心 | 已实现并定向回归 | 服务启动/停止/重启；持久计划、确认、幂等、重放/过期/漂移拒绝、并发单执行、响应丢失进入 RecoveryRequired |
| PB-03 Core/SDK/桌面 | 已接线并编译 | SDK 仅在 Server 代理签发的当前请求上下文中 prepare；Core 用户 API 查看/批准/拒绝；桌面输入 planId 审查和提交，不将密码发给模块 |
| PB-05 审计 / 实机闭环 | 部分收口 | 两平台正常 HTTP 闭环和完整批准审计、Linux Helper 停止/响应丢失已实测；执行中断与发行安装仍待验收 |
| PB-06 常设许可/后台提权 | 未实现 | 无当前请求 context 一律拒绝 prepare；没有自动执行或后台队列 |
| 完整发布安装与更新 | 运行时安装路径已实测，整包升级待验收 | Linux provisioner 与 Windows 生产 ModuleHost 安装函数已实测；整包服务安装/更新仍不能由独立验收实例推定完成 |

## 本地验证

构建命令：

```text
dotnet build Tests/ServerModules/RelaxKonOS.ServerModules.Tests.csproj -p:NuGetAudit=false -p:UseSharedCompilation=false -m:1
dotnet Tests/ServerModules/bin/Debug/net10.0/RelaxKonOS.ServerModules.Tests.dll
```

完整 ServerModules 回归 **566 项 PASS**，包括包 HTTP 上传/计划/apply、签名/撤销/路径攻击、安装/更新/回退/启动核实、第三方不进 Core、精确信任、Broker 门控及独立进程路由。此前一次 HTTP apply 503 未在本轮完整回归中再现；本记录不将其隐去或作为已定位根因。

可单独运行 `--module-host-checks`、`--module-package-checks`、`--module-privilege-checks`。Windows 沙箱不允许所需 loopback/临时文件原子替换，以上实际检查在获准的沙箱外执行。现有编译警告尚在；没有将“零错误”表述为“零警告”。

## Linux 测试服务器证据

服务器：100.113.194.85，Ubuntu 26.04 x64。未在文档、脚本或仓库保存登录或提权密码。

独立测试运行时位于 `/opt/relaxkonos-modulehost-test`，测试源包位于测试用户的 `modulehost-v2-test/packages`，隔离根为 `/var/lib/relaxkonos-module-host-test`。初始独立探针的配置绑定测试用户 UID。随后安装器已为完整 v2 验收实例重建 `/etc/relaxkonos/module-host.json`，绑定现有 `relaxkonos-server` 服务账户 UID（999），源包根指向该验收实例数据。

`Tests/ServerModules/verify-linux-module-host-isolation.py` 已针对实际 root 启动器运行，验证：

- 模块实际身份为 `rkmod-*` 非 root 账户；
- 不可写自己的只读清单；自己的 Storage 可读写；
- 不可读 `/etc/shadow` 与实际存在的旧 Server `data/module-management/state.json`；
- 不可连接实际 `/run/relaxkonos/privileged.sock`；
- ready / health / shutdown 完整退出通过。

实测暴露并修正了 root 运行时目录权限问题：从 Windows 生成的 tar 解包可能将顶层目录置为 0777；启动器拒绝这种路径。生产安装脚本在授予固定 sudoers 前检查整个运行时及祖先必须 root 所有且不可由 group/other 写入。测试副本已收紧权限后复测通过。

Linux 尚未验证：模块无限阻塞的超时清理、完整发布包升级、Helper 执行中断/响应丢失、owner-device/sessionValid 关闭。实际跨模块私有数据拒绝、普通及脱离后代的 shutdown/断管清理与 Helper 请求前不可用已通过下述实测。Windows 专用身份与后代清理已通过下述原生复测。原有 Linux Server/Helper/Guardian 实例仍运行原版本；新完整验收实例单独运行，未替换旧实例。

## 约束

生产不使用 ModuleHosts:Entries 指向任意包目录；这些配置仅在 Development 下供诊断。生产从已安装、已启用、有精确信任的 module-host 记录启动。Linux SourceRoot 只用于 root 启动器经 openat2 读取并验证文件；模块只能访问其复制快照和私有数据，不获得 Server 数据目录遍历或 Helper socket 组。

当前 Broker 的操作集合是显式封闭的三个服务动作；Host 的 ModulePrivileges:Services 与 Helper 的 root 管理服务白名单都必须允许目标。包/文件/网络/任意进程操作没有实现，也不会回退到 shell。


### 完整 Linux 正常闭环实测

`/opt/relaxkonos-v2-test` 使用本轮构建的新 Server/ModuleHost；`relaxkonos-v2-test.service` 在现有非特权服务账户下监听 `127.0.0.1:16591`。独立测试 Helper 使用 `/run/relaxkonos-v2-test/privileged.sock`。现有三项原服务未停止或改写服务配置。

`verify-linux-v2-broker.py` 已执行并通过：真实 PAM 登录；本地签名目录接纳；第三方 v2 包上传、运行模式/提权预览、确认安装与精确信任；受管启用计划及重启；专用 ModuleHost 代理访问；错误管理员密码不执行；Core 确认后真实 Helper 重启固定 `relaxkonos-module-broker-fixture.service` 并以原生 PID 变化核实；重复批准被拒绝且 PID 不变；管理员验证记忆仍要求新计划确认；撤销精确信任立即阻断业务代理与待执行计划。

脚本通过 getpass 读取凭据，只在内存中用于 sudo/PAM/Core，不输出或保存令牌/密码。验收结束时该第三方模块信任已撤销。测试固定服务仅运行 `/usr/bin/sleep infinity`。

新实例曾因父数据目录缺少服务账户遍历权限、缺少 Production 必需的观测配置启动失败，已在测试准备脚本修正。未导出原始 journal；只使用预定义分类输出辅助排查。正常闭环实测不能替代中断/恢复验收。

用户已明确要求 Linux 与 Windows 均实现与测试；Windows 原生隔离已复测通过，但完整 Broker 与安装验收仍有剩余，整体目标保持未完成。

### Windows 实现与验收入口

Helper 的 `moduleHosts` 策略固定运行时、源包根及隔离根；Server 不向 Helper 提供可执行文件或任意命令。独立管道使用机器密钥 HMAC、每连接随机挑战、方向与递增序号绑定；机器密钥不传入模块。Helper 在 Server 调用者身份下读取源包，再验证 v2 清单和文件摘要，复制为受保护快照。

每模块账户名称由隔离根与模块 ID 派生；记录 SID，拒绝接管同名未知账户。账户禁止交互登录，模块只获得自身快照读取/执行与私有数据修改权限，不获得 ACL/所有权修改权限。进程以专用账户启动并在恢复执行前进入 Job；断管关闭进程树。以上是代码实现说明，不能代替原生系统验收。

`Tests/ServerModules/Verify-WindowsModuleHostIsolation.ps1` 使用唯一的临时 LocalSystem 服务和两组模块账户，验收真实 SID、跨模块读取拒绝、Server 数据/Helper 管道拒绝、私有存储和后代清理；结束时清理测试服务与记录的账户。运行入口：

```powershell
./Tests/ServerModules/Verify-WindowsModuleHostIsolation.ps1 -RunnerDirectory ./artifacts/windows-module-host-runner -RuntimeDirectory ./artifacts/windows-module-host-runtime -ServerDirectory ./artifacts/windows-v2-server -ConfirmMachineChanges -ConfirmTestAdministrator
```

本轮原生测试 UAC 被取消，未取得客户端成功报告。取消启动时不再额外请求清理 UAC；一旦提权进程已启动，即使安装失败也仍执行清理。随后自动审批因额度限制未执行提权检查，当前不能证明是否存在部分测试残留，恢复验收前必须先检查唯一测试服务和记录。

2026-11-01 再次执行 `--windows-module-host-checks`：15 项 PASS。完整回归在 `ModuleRecoveryHostChecks` 的 loopback 连接处受沙箱 Socket 10013 限制中止。因此历史 566 项通过仅代表此前构建，不能作为当前 Windows 改动后的完整回归结果。Windows 真实 Broker、Helper 中断恢复和完整安装/更新链仍待验证。

同日 Broker 定向回归：默认临时目录原子替换受访问限制；将测试进程的 TMP/TEMP 指向仓库内 `artifacts/module-v2-validation/unit-temp` 后，28 项 PASS。新增检查覆盖未知结果的幂等 prepare、无新确认令牌、重启后拒绝旧确认，以及 Helper 回应前重新打开持久化 Executing 记录转为 RecoveryRequired。该测试验证存储恢复逻辑，尚未替代真实 Helper 中断或进程崩溃验收。当前测试项目构建通过（0 错误、64 警告）。

Windows 发布完整性门已补齐：`Complete-Package` 要求 ModuleHost 可执行文件、共享安装脚本和部署核验脚本同时存在。`release_inventory_checks.py` 新增测试直接提取并执行生产函数，分别移除这三项，确认归档前拒绝且不生成 manifest；PowerShell 7 下测试通过。Windows PowerShell 5.1 的首次尝试受脚本执行限制，未作为通过证据。该检查不等同于完整发布包安装验收。

### Windows 原生隔离复测通过（2026-11-01）

自动审批恢复后，先确认此前取消未留下专用测试服务，再运行上述验收入口。14 项全部通过：实际调用者可连接受保护 Helper 探针；业务 HTTP 强制进程令牌；真实专用非管理员账户；Server 私有标记、不可变包、机器配置读取拒绝；Helper 管道拒绝；环境隔离；自身数据读写；两个并发模块 SID 不同；实际跨模块私有数据读取拒绝；health；真实后代创建；断管回收完整进程树；另一模块独立 shutdown。安全报告位于本地 `artifacts/modulehost-windows-native/4c3fb88b971241c2beae3d18f00fdde9/`。该报告不包含测试机器密钥或凭据。

清理报告 `cleaned=true`，随后查询无专用测试服务。worker 报告记录主动断管对应的 EndOfStreamException；不将 worker 的最终 `ready=false` 当作启动失败，也不以其替代客户端逐项行为验收。

同轮在获准的沙箱外重跑当前完整 ServerModules 回归：**586 项 PASS**，退出码 0。该结果取代前述受沙箱限制中止的回归结果；历史受限记录保留用于说明排查过程。Windows 完整 Broker 管理员授权闭环、两平台真实 Helper 故障恢复、发布安装/更新链仍不能由这两项通过结果推定完成。

### Linux 真实 Helper 停止/恢复（2026-11-01）

新增 `verify-linux-v2-helper-failure.py`，与正常验收脚本共用 `linux_v2_acceptance_support.py` 中的凭据内存读取、HTTP 和固定 sudo 工具。独立 Helper 的原临时 systemd 单元已不存在，准备脚本改为持久的独立验收单元，含 RuntimeDirectory；不替换原 Helper。

第一次实测的旧测试 Server 未将 Helper 不可用映射为 RecoveryRequired，断言失败。重新发布当前 Linux Server，备份并只更新 `/opt/relaxkonos-v2-test` 的托管程序集后复测通过：真实管理员验证与前置动作成功；停止独立 Helper 后批准返回 RecoveryRequired，目标 PID 不变；恢复 Helper 后重复查询不发新确认令牌、不改变 PID；重放原批准返回 409；新计划在显式确认后成功执行。最终恢复 Helper 并撤销模块信任。未读出或保存登录/提权密码。

该实测证明请求前 Helper 不可用与恢复时不重放；尚不证明执行中途杀死 Helper、动作已执行但响应丢失、Core 崩溃后的真实恢复。上述情况仍属剩余验收。

### Linux 真实跨模块数据与后代清理（2026-11-01）

扩展 `verify-linux-module-host-isolation.py`，支持传入实际 peer 私有文件和 shutdown/disconnect 模式。先以 root 确认 `fixture-http/data/hello.txt` 真实存在，再以配置绑定的 `relaxkonos-server` 调用者运行固定 sudo 启动器，加载独立 `fixture-host` 探针。两种模式均通过：专用非 root 身份、只读快照、Server 数据/Helper socket/环境拒绝、自身 Storage 可写、不能读取 peer 的实际私有文件；模块经业务接口创建实际 `/usr/bin/sleep` 后代后，分别正常 shutdown 与关闭启动输入通道，均确认后代不再运行。

进程检查明确把已退出等待 init 回收的 zombie 视为不再运行；未将其表述为 PID 一定不存在。该探针没有模拟 setsid 脱离、恶意信号处理或无限阻塞，不能替代这类攻击验收。测试包仅为隔离启动器的独立探针，没有修改已安装的 `fixture-http` 归档、信任或启用记录。

### Broker 恢复文件完整性（2026-11-01）

加载持久化计划时增加请求、身份及组合计划摘要重算，验证固定五分钟期限、已知生命周期状态、确认令牌哈希格式，以及终态不得保留确认令牌；空记录/空集合进入 recovery_required。摘要用于发现记录损坏，不是对拥有 Server 文件写权限者的密码学认证。没有引入旧 schema 适配。

新增六项损坏记录检查通过，Broker 定向共 **34 项 PASS**；当前完整 ServerModules 回归 **592 项 PASS**、退出码 0；构建 0 错误、9 警告。该恢复校验是 Linux 上一次实机部署之后的代码变更，尚未重新部署至 Linux/Windows 实机，因此不将前述实机通过结果延伸为该变更的实机验收。

### Linux 脱离后代与忽略信号（2026-11-01）

测试专用 HostFixture 的 `/spawn-child?detached=true` 使用固定 setsid 与固定 sleep 命令，创建独立 session 且忽略 SIGTERM 的实际后代；该入口只存在于测试 DLL，不是生产 Broker 的任意命令能力。隔离脚本先读取 `/proc/<pid>/stat` 验证 session ID 等于后代 PID，再从 `SigIgn` 确认 SIGTERM 被忽略，随后分别执行 shutdown 和关闭输入通道。两次实机验收均通过，后代不再运行。该证据补齐前述普通后代测试未覆盖的脱离/忽略信号情况，尚未覆盖 ModuleHost 自身的 health/shutdown 永久阻塞。

### Windows 服务动作无响应超时（2026-11-01）

审查发现 Windows NamedPipe transport 对 NativeServiceAction 发出请求后使用无限读取等待，Helper 接收后无响应可让批准永久停留在执行中。已为该操作的写入及响应读取增加统一的 TimeoutSeconds 有界期限（1–120 秒）；超时返回 TimedOut，Core 现有映射将其视为未知结果而非自动重试。其他长时间操作的既有超时策略未改动。

新增真实本机命名管道测试：对端读取完整服务动作请求后保持静默，客户端配置 1 秒期限，在外部 5 秒测试上限内返回 TimedOut。Windows 定向 **16 项 PASS**；当前完整模块回归 **593 项 PASS**，退出码 0；构建 0 错误、45 警告。这是传输故障注入，不能代替 LocalSystem Helper 真实服务动作与 Core 身份授权闭环验收。

### Windows 真实 Helper 服务动作（2026-11-01）

原生夹具已由仅提供 ACL 的探针管道改为生产 `WindowsPrivilegedPipeServer`，增加唯一命名的临时服务供操作。审查发现原实现将重启发给不支持该动作的 `sc.exe restart`；现改为 SCM API 的停止、等待、启动与等待，整个操作共用 30 秒期限；启动/停止已处于目标状态时保持幂等。停止明确设置 `stopDependentServices:false`，不得因目标被允许而级联停止其他服务。Linux 继续使用 systemctl。

最终实现的真实 LocalSystem 验收 **22 项通过**：原 14 项隔离检查、白名单外服务拒绝、停止/启动/重启及 SCM 状态核实、重启实际 PID 变化。报告：`artifacts/modulehost-windows-native/b0ba259001314bf29d9e1144442b7d24/`；`cleanup.json` 为 cleaned=true，随后查询无两个前缀的临时服务。测试账户已由夹具按记录 SID 清理。该场景没有依赖服务，因此未将“不级联停止”计为实测覆盖。

这证明 Windows 生产管道、Helper 固定策略和真实 SCM 动作；未走 Core 用户登录、管理员验证和确认端点，因此 Windows Broker 完整授权闭环仍未收口。最新构建 0 错误、48 警告。

### Windows 临时管理员与 Broker 原生联调（2026-11-01）

用户明确授权创建临时管理员后，夹具新增唯一 `rktest-*` 管理员与随机密码，要求显式 `-ConfirmTestAdministrator`。凭据文件位于受保护的临时运行目录，使用当前 Windows 账户的 DPAPI 加密；密码仅经子进程标准输入进入验证器，不放入命令行、模块环境或报告。账户先记录 SID，再授予 Administrators 成员资格；清理核对名称与实际 SID 一致后删除。

当前 **27 项原生检查通过**：实际 LogonUser 拒绝错误密码、验证真实管理员并返回规范 Windows SID；Broker 持久计划在错误验证时不派发，正确验证与显式确认后经生产管道执行一次真实 Helper 重启（核实 PID 变化），随后拒绝旧确认重放；原 22 项服务与隔离检查继续通过。报告：`artifacts/modulehost-windows-native/0bdb7a2e251e46b095a2f61069342499/`。清理报告 cleaned=true；独立查询该临时管理员数量为 0，两类临时服务均无残留；受保护目录及加密凭据随夹具删除。

这轮直接组合生产管理员验证器、计划存储和 Helper 传输，尚未通过完整 Core HTTP 登录/代理/批准端点，也没有验证 owner-device 与 SessionValid 开关。因此不将其表述为 Windows 全链路验收完成。

### Windows 完整 Core HTTP 正常闭环（2026-11-01）

新增 `WindowsCoreAcceptance.cs`，启动独立 Production Server，使用独立 SQLite、随机 JWT/audit 密钥和已授权临时管理员；不读取现有 Server 凭据配置。使用生产打包器构建临时签名第三方 `fixture-broker` v2 包；停机时由生产 catalog store 接纳签名目录，启动后所有安装、启用、代理、计划与批准均走真实 HTTP 端点。该目录接纳是测试部署的本地信任配置步骤，不冒充 HTTP 目录刷新验收。

本轮共 **35 项原生/HTTP 检查通过**，新增覆盖：真实 Windows 密码登录；v2 安装计划精确展示 runtime 与 privilege target；确认安装与受管启用；生产 supervisor 经 LocalSystem Helper 启动已安装专用 ModuleHost；认证 Core 代理；错误管理员密码不改变目标服务 PID；显式批准后真实 Helper 重启且 PID 变化；重复批准 409 且 PID 不变；撤销精确信任立即阻断代理。首次尝试因夹具服务名含大写被 v2 清单拒绝，已改为规范小写目标，不增加大小写兼容解析。

最终报告：`artifacts/modulehost-windows-native/3ba93d3ba12f47738694525b57d02d41/`。独立核验 cleaned=true、临时管理员数量 0，无两类临时服务。新增第三个模块账户也按记录 SID 清理；独立 Core 进程在 finally 终止，密码与 DPAPI 凭据未留在仓库/报告。包与 Core 测试数据保留在受调用者 ACL 保护的测试工作目录，代码信任已撤销。

Windows 正常 Core HTTP 授权闭环至此已有证据；owner-device/SessionValid 开关、真实执行中断/响应丢失，以及完整发行包经安装器部署/更新仍未验收。独立测试实例成功不能替代发行安装/更新。

### Windows SessionValid 与 owner-device HTTP 验收（2026-11-01）

在同一个真实 Core/Helper/SCM 夹具中，新增通过实际 runtime-settings GET/PUT 切换 sessionValid 的检查：开启时仅记忆验证身份，新计划在确认前 PID 不变；显式确认可免重复密码；关闭立即清除旧验证记忆；关闭后新密码批准仍可执行，但后续计划不免密码；重新开启不恢复已清除的旧验证。所有拒绝路径均核对目标服务 PID 不变。

随后将 sessionValid 关闭，使用真实 HTTP bootstrap 注册内存 P-256 私钥对应的公钥。错误密钥签署的挑战被拒绝；正确 DER ECDSA 签名登录取得 owner-device controller session；已消费挑战不能重放。owner-device 仅 prepare 不执行，新的显式确认可免密码并实际重启服务（PID 变化），证明 owner 认证与验证记忆策略分离。测试未使用 loopback Negotiate local-bootstrap；未测试多设备撤销。私钥仅存于内存，未写入测试目录。

本轮 **46 项原生/HTTP 检查通过**，报告 `artifacts/modulehost-windows-native/b44cdc76cd6148a7ab4c0b5dbc3c8cfe/`。清理报告 cleaned=true，独立查询临时管理员数量 0、无两类临时服务。完整发行安装/更新、真实执行中断/响应丢失和管理员审计信息仍有剩余。

### Linux 实际成功响应丢失（2026-11-01）

新增 `verify-linux-v2-response-loss.py`。独立非特权 Unix socket 代理以配置授权的 Server UID 运行，仅接受同 UID 调用者；原样转发实际握手、PAM 与 Helper 请求，不输出/持久化 payload 或凭据。只对固定验收目标的首次服务重启，在收到真实 Helper 终态后丢弃成功响应；安全收据只保存请求次数和结果是否成功。独立测试 Server 暂时切到代理，finally 恢复原 Helper socket、重启、撤销模块信任并停止代理。

实测通过：真实服务 PID 已变化、代理确认收到 success=true、Core 返回并保存 RecoveryRequired；三次查询不发确认令牌且请求次数仍为 1；旧批准 409、PID 与请求次数均不变；新计划显式批准成功，计数恰为 2。第一次夹具错误使用 Success 字段，导致证据收据断言失败；按现行协议修正为 success 后重新完成上述验收，没有增加双格式解析。

独立复核：两项验收服务和原 Server/Helper/Guardian 均 active；代理 unit 为 not-found/inactive；配置分类检查 private-helper-route-restored=True。实测证明动作成功但响应丢失时不重放；尚不等同于动作执行中杀死 Helper/Core 的故障验收。当前脚本进一步限定 serviceAction=Restart；该最后的选择条件收紧尚未重新上传执行。

### 批准审计与真实落库（2026-11-01）

持久计划新增发起用户、同会话批准用户、规范管理员身份/验证方式、确认与执行尝试结束时间；发起者绑定冻结摘要。管理员记忆保留最初实际验证身份，文件及通用提权入口也记录该身份，Broker 不以缺失身份的记忆代替验证。owner-device 记录实际设备 ID。Executing 崩溃恢复仍保留已确认身份，不自动重放。

内部 `privilege-plans.json` 升为 schemaVersion=3，拒绝旧的审计不完整格式；**安装清单/installed/package-plans 仍为 schemaVersion=2**。未添加迁移或双格式读取。Linux 验收实例仍运行此前二进制及旧内部记录，更新前必须显式归档其测试证据，不能直接重启新二进制并假称升级成功。

定向审计检查新增六项，Broker 共 **40 项 PASS**；完整回归 **601 项 PASS**。Windows 原生/HTTP **48 项 PASS**，实际读取计划文件核实原生 SID、owner-device、用户、时间和不含凭据；读取实际 SQLite 安全审计核实同一计划 ID 的 Helper 起止事件及发起者引用。此轮发现并修复批准请求未传 OperationId、导致 transport 另生成 ID 的关联缺口。另一次停机接纳目录时锁尚不可用，夹具改为在进程退出后有界检查独占锁，未绕过锁。

最终报告 `artifacts/modulehost-windows-native/15f3f14f237b44ecad534e98272c0115/`；独立确认 cleaned=true、临时管理员 0、两类测试服务无残留。此证据取代早期仅用捕获式 writer 的审计验证限制。Linux 新版审计部署、真实执行中杀进程，以及完整发行安装/更新仍未验收。

### Linux 新版批准审计部署与复核（2026-11-01）

当前 Linux Server 已重新发布并只更新独立 `/opt/relaxkonos-v2-test` 的托管程序集。先停机确认旧内部存储为版本 2，再将旧计划、初始化标记和原托管程序集备份至 root 专用 `/opt/relaxkonos-v2-audit-evidence/20261101T032154Z`，明确保存旧证据后由新版本建立版本 3 存储；没有自动迁移、双格式读取或删除旧证据。现有配置与原三项服务未改写。

更新后重新执行收紧为固定 serviceAction=Restart 的真实响应丢失脚本，全部通过，最终恢复原私有 Helper 路由、停止临时代理并撤销模块信任。再执行新增 `verify-linux-v2-audit.py`，直接只读检查持久计划和 SQLite：未知/成功两种尝试在 Core 重启后保留实际 PAM 管理员 UID、发起/批准用户、确认/尝试结束时间；按原计划 ID 均能找到至少两条带 actor 引用的 Helper 审计记录；计划无密码、服务令牌、访问令牌或明文确认令牌字段。脚本只输出三项 PASS，不输出原始身份或记录。

因此前述“Linux 新版审计尚未部署”和“最后的请求选择条件尚未实测”的限制已解除。执行中杀进程与完整发行安装/更新验收仍未完成。

### Windows 生产运行时安装与快照验证（2026-11-01）

原生夹具不再手工复制 ModuleHost 运行时，改为调用 System/Personal 共用的生产 `Install-RelaxKonOSModuleHostRuntime`，使用其实际返回的不可变运行时和隔离根进行全套 Core/Helper 验收。相同来源复用同一摘要快照；额外快照文件、损坏执行文件均在 Helper 启动前被拒绝。测试损坏只发生于唯一临时安装根，恢复后再验证并执行，未触及现有安装。

修正安装函数的验证缺口：源文件长度/摘要在复制前冻结，目标与冻结证据比较，不能因源同时变化而接受错误快照；源与快照逐级枚举，遇到链接立即拒绝，不先递归跟随；完整文件数量也必须一致。新增 `Tests/Deployment/ModuleHostRuntimeChecks.ps1` 四项内容检查通过，覆盖稳定复用、额外文件、等长摘要损坏，以及复制前源变更后源/目标相同仍拒绝。该本地测试桩替换 ACL 命令，只证明内容校验；真实 ACL 证据来自原生夹具。

生产安装函数输出上的原生/HTTP **50 项 PASS**，报告 `artifacts/modulehost-windows-native/a4ea05cee3f54875b9c7e9eb57b87ca1/`。安装报告各项为 true、cleaned=true；独立查询临时管理员 0、无测试服务，保护安装根不存在。该结果证明生产 ModuleHost 运行时安装函数，不等同于整个发行包的服务安装/更新验收。

### Linux Core 执行中崩溃实测（2026-11-01）

`verify-linux-v2-response-loss.py --core-crash` 为固定 sleep 验收服务添加独立 8 秒 ExecStop 覆盖，先确认实际服务为 deactivating、原请求已转发一次且持久计划为 Executing，再对独立 Core 主进程发送 SIGKILL。真实 Helper 与故障代理处于不同服务单元，继续完成已接收的动作。

实测通过：Helper 完成后 PID 变化，新的 Core 将遗留 Executing 转为 RecoveryRequired；查询不发新确认令牌、不再转发请求；旧批准被拒绝，计数仍为 1；真实重新登录后，新计划显式批准只增加一次请求并成功执行。首次夹具仅接受 409，实际旧会话在批准前因有效性检查返回 403，已将这两种明确拒绝作为不重放断言，并要求新操作先重新登录。

在途被杀的 Core 没有机会记录尝试结束时间或 Helper 终态审计，保持 AttemptCompletedAt=null 与未知状态，不能伪造完成证据。只读审计检查现在区分此情况（必须有原始接受事件和恢复后的未知计划）与已观察到终态的尝试（要求起止事件）；三项审计断言复测通过。

最终独立复核：验收 Server/Helper 与原 Server/Helper/Guardian 均 active，代理 unit not-found/inactive，固定测试服务覆盖已移除，private-helper-route-restored=True，模块信任已撤销。该证据覆盖 Core 执行中崩溃，不冒充执行中杀死 Helper 或 Windows 崩溃验收。

### Linux Helper 执行中终止（2026-11-01）

新增并实测 `verify-linux-v2-response-loss.py --helper-crash`：实际服务进入延迟停止、计划持久化 Executing 后，SIGKILL 独立 Helper 整个服务组；Core 收到断管后返回 RecoveryRequired，而 systemd 独立完成已接收的重启。恢复 Helper 后，重复查询/旧确认不再派发，请求计数保持 1；新显式确认只执行一次新动作，计数为 2。此模式未收到 Helper 成功响应，收据保留 droppedSuccess=false，不将 systemd 完成动作冒充收到成功结果。

脚本完整退出并确认恢复私有 Helper 路由、撤销模块信任、停止代理及删除固定服务的测试覆盖。Windows 对应崩溃与完整发行安装/更新仍待验收。

### Windows Helper 审计发起身份（2026-11-01）

修正 Windows transport 的安全审计缺少 ActorReference：与 Linux 一致，优先使用后台 `PrivilegedAuditActorScope`，否则读取已认证 HTTP 用户 subject（支持标准 sub/NameIdentifier）。接受与完成事件保留同一 operationId；正式写入器仍对身份引用做摘要化，不在报告暴露原始用户标识。

真实静默管道测试增加两个审计检查：显式后台身份优先于 HTTP 身份，起止事件操作 ID 一致；无后台作用域时回退至 HTTP subject。当前完整回归 **595 项 PASS**、退出码 0。此处使用捕获式 audit writer 检查事件内容，尚未将该修复重新发布到完整 Windows HTTP 夹具。该修复补齐发起者引用，不代表已补齐实际验证管理员身份、批准时间及待恢复状态的完整审计链。
