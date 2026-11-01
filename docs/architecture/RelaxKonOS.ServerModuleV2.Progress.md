# 服务端 ModuleHost v2 实施记录

更新：2026-11-01。这里只记录当前实现与证据，不将设计目标视为已交付。

| 跟踪项 | 当前状态 | 已有证据 / 余项 |
| --- | --- | --- |
| MD-01 唯一 v2 契约 | 已实现并回归 | manifest / installed / package-plans 使用 schemaVersion 当前值，拒绝旧格式；打包器、CLI、SDK、Server 与桌面同步 |
| DC-01 声明式能力契约 | 已实现（契约冻结） | 清单 schemaVersion=3 能力类别 + 类型化资源范围；`ModuleCapabilityCatalog` 统一类别/动作/可执行矩阵；Broker 按声明范围匹配；秘密引用只保留引用；`service.manage` 与 apt `package.manage` 可执行（后者需 Helper 解析），其余明确拒绝 |
| DC-02 通用包事务 | 进行中（apt 已实机验收；共享依赖/Windows 未交付） | 契约、apt 通用解析、root 拥有的包来源策略、`ModulePackageQuery`/`ModulePackageApply`（重解比对、持久回执、安装后核实）、Broker prepare 冻结 `ModulePackageResolution`、计划存储 schemaVersion=5（DC-03 起 6、DC-04 初版 7、审查修复后 8）、`planHash` 绑定冻结身份、approve 只发送来源引用与冻结身份、Helper `PlanChanged`→`module.privilege_plan_changed` 均已实现；Ubuntu 26.04 实机验证 mysql-server 与 redis-server 的安装/重放拒绝/摘要漂移拒绝/来源撤销拒绝/移除。Windows 适配器、共享依赖与外部安装接管未交付 |
| MD-05 Linux 身份与权限 | 已实现；独立运行时实机验证 | 每模块稳定专用 UID/GID，无附加组、no_new_privs；root 拥有只读版本快照，模块拥有私有数据；root 监督器控制超时/断管清理 |
| MD-05 Windows 身份与 ACL | 已实现；原生隔离及 System 整包验收通过 | LocalSystem Helper、独立账户/S4U、只读快照/私有数据 ACL、认证管道、跨模块拒绝和 Job 后代清理有实测；正式更新保留同一模块 SID |
| MD-06 安装与代码信任 | 已实现并回归 | 冻结计划展示 runtime/privileges；精确信任绑定版本/发布者/归档与清单摘要；更新不继承，撤销立即阻断代理/Broker |
| MD-07 / PB-01/02 Broker 核心 | 已实现并定向回归 | 服务启动/停止/重启；持久计划、确认、幂等、重放/过期/漂移拒绝、并发单执行、响应丢失进入 RecoveryRequired |
| PB-03 Core/SDK/桌面 | 已接线、编译及定向验证 | 前台 prepare 绑定业务请求；后台执行须有显式常设许可；桌面审查单次计划、授予/撤销后台许可，不将密码发给模块 |
| PB-05 审计 / 实机闭环 | 两平台主要故障闭环已实测 | 两平台正常HTTP/批准审计/Core与Helper执行中终止均有实测；Linux及Windows System正式发行安装、跨版本更新通过 |
| PB-06 常设许可/后台提权 | 服务动作两平台实测；DC-05 扩展类别已定向验证 | 管理员显式确认，绑定模块精确身份/单一动作/目标/期限；服务动作两平台真实后台/到期/撤销/重启幂等通过；DC-05 开放 package install/update/repair、file write（内容摘要绑定）、data preserve、app query；删除/备份/包移除/凭据轮换仍逐次确认 |
| PB-07 高信任扩展 | 评审完成，当前不实现执行 | 威胁模型与平台隔离缺口已记录；稳定拒绝码、启动守卫、清单/协议形状已锁定，满足重新评审门槛前不开放 |
| 完整发布安装与更新 | Linux及Windows System整包安装、跨版本更新已实测 | 正式包/Bootstrap/三服务/原生登录/v2模块安装代理与跨版本保留已贯通；Windows Personal整包不在本轮通过范围 |

## 本地验证

正式升级审查发现 Linux Bootstrap 未携带版本目录中的 `appsettings.host.json`，会丢失宿主模块管理员与发布源配置。现已在升级和回滚切换前复制当前配置，并保留原文件所有者与权限；源或目标为符号链接时拒绝操作。`Tests/Deployment/host_config_upgrade_checks.py` 实际执行生产 Bash 函数，配置往返、缺省配置和两端链接拒绝共 3 项通过。这是本地回归证据，尚不构成正式包跨版本升级验收。

构建命令：

```text
dotnet build Tests/ServerModules/RelaxKonOS.ServerModules.Tests.csproj -p:NuGetAudit=false -p:UseSharedCompilation=false -m:1
dotnet Tests/ServerModules/bin/Debug/net10.0/RelaxKonOS.ServerModules.Tests.dll
```

完整 ServerModules 回归 **601 项 PASS**，包括包 HTTP 上传/计划/apply、签名/撤销/路径攻击、安装/更新/回退/启动核实、第三方不进 Core、精确信任、Broker 门控及独立进程路由。此前一次 HTTP apply 503 未在本轮完整回归中再现；本记录不将其隐去或作为已定位根因。

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

内部 `privilege-plans.json` 升为 schemaVersion=3，拒绝旧的审计不完整格式；当时安装清单/installed/package-plans 仍为 schemaVersion=2。未添加迁移或双格式读取。Linux 验收实例仍运行此前二进制及旧内部记录，更新前必须显式归档其测试证据，不能直接重启新二进制并假称升级成功。DC-01 起清单与 `package-plans.json` 升为 schemaVersion=3、计划存储升为 4、许可存储升为 2；DC-02 起计划存储再升为 5（冻结包来源与制品身份），见下表 DC-01 / DC-02 行。

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

### Windows Core 崩溃夹具与当前审批限制（2026-11-01）

Windows 原生夹具已增加真实 SCM StopPending 期间终止独立 Core 的路径：固定测试服务仅在调用者工作目录的标记存在时延迟停止 8 秒；确认持久计划为 Executing 后终止 Core，独立 LocalSystem Helper 继续完成动作。待验收断言包括恢复为 RecoveryRequired、不发新确认令牌、旧批准拒绝且 PID 不变、重新登录后的新确认可执行，以及保留原批准身份但不虚构 AttemptCompletedAt。另检查原计划在 SQLite 中恰有一条 Helper 接受记录，防止仅凭 PID 推测未派发重放。

夹具构建和发布通过，但启动原生验收的提权命令被自动审批额度限制挡住，命令未执行；审批提示 13:25 后可重试。这是审批无法完成，不是安全判定拒绝；没有绕过审批，也没有把新崩溃路径计为通过。此前 50 项原生通过仍是此前版本夹具的证据。

当前可执行的 `WindowsServicePayloadChecks.ps1` 本地 28 项通过，使用模拟 SCM/进程事实核实部署载荷身份，不修改真实服务，不能代替完整发行服务安装/更新。当前 System Helper 有固定生产服务身份约束，后续整包验收不能通过随意改名来冒充等价部署。

### Windows Core 执行中崩溃复测通过（2026-11-01）

审批提示的恢复时间过去后重新提交相同验收请求，实际运行完成 **61 项原生/HTTP 检查**，退出码 0。确认实际 SCM 服务处于 StopPending、计划已耐久写为 Executing 后终止独立 Core；原 HTTP 连接中断，LocalSystem Helper 独立完成服务重启且 PID 变化。新 Core 将计划恢复为 RecoveryRequired，不发新确认令牌；三次查询与旧确认拒绝均不改变 PID，数据库中该原计划的 Helper 接受记录恰为一条。

恢复记录保留原批准用户、owner-device 身份和确认时间，AttemptCompletedAt 仍为 null，不伪造观察到的终态。真实重新登录后，新计划显式确认可执行一次新动作。测试停止延迟标记在 finally 删除，Core 进程按夹具清理，模块信任撤销。

报告 `artifacts/modulehost-windows-native/42f7b031848c409c81740de3b66af994/`；独立核对 succeeded=true/checks=61、cleaned=true、临时管理员数量 0、无两类临时服务。前述审批限制已解除，Windows Core 崩溃验收不再列为未验证；Windows Helper 执行中终止及完整发行服务安装/更新仍有剩余。

### Linux 正式发行包安装实测（2026-11-01）

PowerShell 正式打包入口新增与 Bash 对等的 `-PackageKind all|client|server`，清理旧输出前校验最终绝对路径仍在选定输出目录内。本轮用 `-Version 0.2.2-v2-acceptance -Runtime linux-x64 -PackageKind server` 生成完整服务端包；文件清单验证通过，传输 SHA-256 为 `a0b4813a0fde8c0bed8d3c832ed171129c5034b8befc4f8930574bf34c9ed0b5`。发行包自身的 schemaVersion=1 与模块安装清单 schemaVersion=2 是不同契约。

新增 `verify-linux-v2-release-install.py`，在 root 专用目录保存原配置、Helper 运行时及服务状态，使用新安装根 `/opt/relaxkonos-v2-release-install` 和新数据根 `/var/lib/relaxkonos-v2-release-install`。实际调用包内正式 Bootstrap System 安装入口，核对 Server/Helper/Guardian 三项活动进程的真实可执行路径和 ModuleHost 配置；随后真实 PAM 登录，接纳新生成的临时签名目录，经 HTTP 上传、计划、确认安装、受管启用及重启，生产 ModuleHost 代理 hello 成功。不是手工注册服务或仅运行独立探针。

完整流程退出并恢复原三项服务及独立验收两项服务。root-only 证据 `/opt/relaxkonos-v2-release-evidence/6c2af4b065ed4325aa6b9bdc74dab73b`，包含 installed/restored 断言和未导出的安装日志。独立复核 installed=true、originalServices=true，七项受保护配置逐字节摘要与备份一致，原五项服务均 active。原配置曾为验收暂时切换，不能再表述为整轮始终未改写。

新安装根和数据作为不活动证据保留，不是当前运行的默认服务实例。本轮新实例的测试代码信任未在停机前撤销；脚本已补上撤销及 403 检查供后续运行，该补充尚未重新执行。没有将初次安装或同版本重装记为跨版本更新，更新仍需单独验收。Windows 完整服务安装也仍待验收。

### Windows Helper 执行中终止与 SCM 恢复（2026-11-01）

测试项目的临时 LocalSystem 宿主新增固定标记触发的自身终止：仅当唯一测试目标处于实际 StopPending 才终止当前宿主进程，不接受 PID/命令/路径指令；注入逻辑不在生产 Helper 项目。临时服务配置 1 秒 SCM 恢复动作，清理前先禁用测试服务，避免删除过程中再次恢复。

实际运行 **67 项原生/HTTP 检查通过**：Helper 在执行停止阶段终止后，Core 返回 RecoveryRequired；SCM 恢复出新的 Helper PID，目标最终保持 Stopped、没有新目标 PID，证明原 Restart 只部分执行而非已完整成功。重复查询及旧批准均不补做启动；显式重启 Core 恢复 ModuleHost、真实重新登录后，新计划确认才启动目标并返回成功。完整原生隔离、Core 崩溃、审计和安装函数检查继续通过。

报告 `artifacts/modulehost-windows-native/5cd3ecf286b949b1a3fbce79b31d14bd/`；独立核对 succeeded=true/checks=67、cleaned=true、临时管理员 0、无两类测试服务。此项解除 Windows Helper 中断的验收缺口；完整 Windows 发行服务安装与跨版本更新仍待完成。

### Windows Helper 审计发起身份（2026-11-01）

修正 Windows transport 的安全审计缺少 ActorReference：与 Linux 一致，优先使用后台 `PrivilegedAuditActorScope`，否则读取已认证 HTTP 用户 subject（支持标准 sub/NameIdentifier）。接受与完成事件保留同一 operationId；正式写入器仍对身份引用做摘要化，不在报告暴露原始用户标识。

真实静默管道测试增加两个审计检查：显式后台身份优先于 HTTP 身份，起止事件操作 ID 一致；无后台作用域时回退至 HTTP subject。当前完整回归 **595 项 PASS**、退出码 0。此处使用捕获式 audit writer 检查事件内容，尚未将该修复重新发布到完整 Windows HTTP 夹具。该修复补齐发起者引用，不代表已补齐实际验证管理员身份、批准时间及待恢复状态的完整审计链。

### Linux 正式跨版本更新及目录身份防护（2026-11-01）

PowerShell/Bash 发行构建器现在将发行版本写入程序集 InformationalVersion，避免仅更换归档名和安装记录。正式包 `0.2.4-v2-acceptance` SHA-256 为 `4ce02d5e621657d2b075a707841d485f8017946b8a11d8e4698328c85dc17116`。真实验收从正式 `0.2.2-v2-acceptance` 包安装，经 PAM 登录、签名 v2 模块安装与启用，再调用新包内 Bootstrap upgrade。核对新旧 Server DLL 摘要不同、Server/Guardian 实际进程路径切换、宿主配置逐字节保留、已安装包清单不变、隔离模块自动恢复和代理 hello 成功；随后撤销测试代码信任并验证代理 403。

成功验收使用 `/opt/relaxkonos-v2-release-upgrade-2` 和 `/var/lib/relaxkonos-v2-release-upgrade-2`。root-only 证据 `/opt/relaxkonos-v2-release-evidence/6feea430c43948f89520a0889b627897`，独立复核 upgraded=true、distinctServerBinary=true、hostPolicyPreserved=true、actualServicePayloads=true、restored=true；本次原安装记录与快照逐字节一致，五项原服务 active。该结论只覆盖 Linux；Windows 正式整包安装与更新仍待执行。

首次升级验收脚本漏传 `--data-root`，导致读取原实例记录，并将原记录安装根写成测试根。该次失败现场保留于 `/opt/relaxkonos-v2-release-evidence/e8af2726a26c4e088ca4836cdd43a969`。原服务/配置已恢复，安装记录依据实际运行 `/opt/relaxkonos/versions/0.2.2/server/RelaxKonOS.Server` 修复；无法确认的原 previousVersion 置 null，不臆造回滚历史，修复时间和原因另有 root-only 记录。因此不能声称该次原安装记录逐字节恢复。

修复后的脚本显式传递安装根和数据根，备份/恢复范围补入原安装记录。Linux 与 Windows Bootstrap 对非首次安装操作均核对记录中的 installRoot/dataRoot，拒绝把另一安装的数据根用于当前安装根。生产函数/AST 回归分别 4 项、3 项通过；发行清单 9 项、相邻证书策略 4 项通过。测试不将失败尝试计为升级成功。

### Windows 模块宿主配置保留修复（2026-11-01）

审查发现仅在 Bootstrap 复制 `appsettings.host.json` 不足以保留策略：System/Personal 安装器随后重新生成配置，遗漏模块管理员、发布源与 Broker 白名单。两个入口现在共用 `ConvertTo-RelaxKonOSModuleHostConfigurationJson`，保留 ModuleManagement/ModulePackages/ModulePrivileges/ModuleHosts/ModuleHealth/ModuleResidency/Modules，并以深度 32 序列化完整策略；程序路径、传输秘密等由安装器重新生成，不从旧配置覆盖。System 遇到无法读取的既有宿主配置时拒绝替换，不再吞掉错误并丢失策略。

`ModuleHostConfigurationChecks.ps1` 三项、既有运行时快照四项与 Windows 服务载荷二十八项本地回归通过。新增 `Verify-WindowsReleaseInstallation.ps1` 使用正式两个包、三项固定生产服务身份及真实 SCM，拒绝接管已有服务，临时管理员密码仅在内存中，最后按 SID 清理管理员和模块账户，恢复既有 Proxy ACL。当前实际验收已走过正式 System 安装、健康及原生管理员登录；签名包夹具类型引用已修复，正在继续完整安装/更新链，尚未将整条链记为通过。夹具通过 Windows PowerShell 5.1 调用正式包内安装器。

### Windows 正式 System 整包与跨版本验收通过（2026-11-01）

正式包 `0.2.4-v2-windows-acceptance`（SHA-256 `2c26d6a8b59423cb3bdde6e5e8c1032a7ca98ca9a5e6c9e1d103c0551d66da67`）安装后，调用 `0.2.5-v2-windows-acceptance`（SHA-256 `e151bb823aed6f73db3c3034329875263895a20c202bf3b247e636bfff233f2e`）包内正式 Bootstrap 升级。两包清单验证通过。实际走通三项固定生产 SCM 服务、健康及载荷身份核验、临时管理员原生登录、临时签名目录接纳、HTTP v2 上传/计划/安装、受管启用及生产独立账户代理；升级后 Server DLL 摘要不同，发布源/管理员/Broker 服务白名单、已安装包和精确信任保留，代理继续成功。

最终复测额外核对同一模块账户 SID 跨版本稳定；通过 HTTP 撤销代码信任后代理返回 403。报告 `artifacts/windows-release-install/e348d7cae53c453bae5e894d62bc49d1/result.json` succeeded=true、cleaned=true；受保护原始证据位于 `C:\Program Files\RelaxKonOS-Release-Acceptance\e348d7cae53c453bae5e894d62bc49d1`。清理前禁用并删除三项测试服务，使用生产身份记录核对后清理模块账户，按原 SID 删除临时管理员，既有 Proxy 根 ACL 的 SDDL 与测试前精确一致。

独立提升权限的 NetUserGetInfo 复核返回管理员及模块账户均为 NERR_UserNotFound（2221），三项服务数量 0，见同目录 `independent-cleanup-audit.json`。普通会话 NetAPI 返回 AccessDenied（5），未将其冒充账户不存在。此轮为 Windows System Mode 完整实测；Personal Mode 只覆盖本地配置保留修复，不能据此声称 Personal 整包验收完成。

Broker 当前仍只开放 `service.manage` 的启动/停止/重启。设计中包/文件动作和 PB-06 常设许可/后台任务仍为后续项，未因两平台生产服务验收通过而改写为已实现。

### PB-06 固定服务后台许可与两平台实测（2026-11-01）

新增 `ModulePrivilegeConsentStore`，使用独立 `privilege-consents.json` schemaVersion=1（不是模块清单格式）。许可绑定模块版本/发布者/归档与清单摘要、源/信任/管理修订、单一服务动作和目标、授予用户、有效期及幂等键。确认计划五分钟失效；许可期限最长三十天。只有当前 `ServerModulesManage` 主机管理员能创建、审查或批准；批准仍须实际管理员验证或已验证身份，并提交一次性确认令牌。其他主机管理员可撤销已有许可或拒绝待确认许可。记录保留实际管理员身份/验证方式、确认和撤销证据，不保存管理员密码或原始确认令牌。

公共 Core API：POST/GET `/api/v1.0/server/module-privileges/consents`，GET 同路径 `/{id}` 仅向原会话签发审查令牌，POST `/{id}/approve` 与 `/{id}/revoke`。列表不返回确认令牌。SDK 新增 `ExecuteBackgroundAsync(consentId, request, cancellationToken)`，走私有 `/background`；模块身份只从当前进程服务令牌得到，不接受用户/JWT/密码自报身份，也不依赖已经结束的业务请求 context。无许可、目标越界、到期、撤销或模块/信任漂移均在派发前拒绝，不排队。

每次后台操作仍进入原 `ModulePrivilegePlanStore`，以 `consent:<许可ID的N格式>` 会话关联原许可，记录授予用户和 `standing-consent:<原验证方式>`；原许可记录保存实际确认时间。原 operationId 直接关联真实 Helper 审计。相同幂等键返回原结果；Core 重启和响应丢失不自动重放未知结果。许可撤销与派发串行化：已经授权的在途动作不冒充已取消，撤销返回后不再接收后续动作。后台许可不替代前台 `PrepareAsync` 的逐次确认流程。

本地：完整 ServerModules 回归 **623 项 PASS**；其后新增的未知后台结果不重放、过期后的创建幂等回执有定向检查。桌面组件中心接入三语言的审查、显式授予、许可列表与撤销，显示精确模块/版本/发布者/摘要、操作/目标及期限。客户端拒绝审查范围替换、列表泄露令牌和登录切换后的旧客户端提交；`--modules-only` 通过。已检查 Headless 图 `server-module-consents.png`，按实际工作区的禁用横向滚动设置核验换行及控件；不将 Headless 渲染冒充真实桌面交互验收。

Windows 最新报告 `artifacts/modulehost-windows-native/0c48588c2c3446feaa4df2872bf27bdd/`：succeeded=true，**74 项**原生/HTTP检查通过，cleaned=true。新增实测包括：无许可后台任务不派发、真实错误管理员密码拒绝、业务请求结束后的实际后台 SCM 动作、Core 重启后同一任务不重放、后台 Helper 审计有原授予者归属、撤销及实际十二秒到期后任务拒绝；原有隔离、Core/Helper 执行中崩溃和审计检查继续通过。

Linux 脚本 `verify-linux-v2-standing-consent.py` 更新独立 `/opt/relaxkonos-v2-test`，使用新签名源与新模块版本，不接管原三项服务。运行时归档 SHA-256 `d4af306ac3f8e3508a1430a66fcb10f0bcb8d3d6f47925b3374242c5d880df84`。实测 PAM 确认、真实后台 Helper 派发、Core 重启幂等、撤销及实际十二秒到期后拒绝；最后两项许可和测试代码信任均撤销，SessionValid 恢复。root-only 证据 `/opt/relaxkonos-v2-standing-evidence/c98eb71d3d8040d9b5c814ba6ad36494` 的独立审计核对确认两项许可、一次后台派发、真实 PAM 身份及 Helper 操作者记录；通用 Linux 审计脚本也已复核通过。

许可当前仅覆盖 `service.manage` 的启动/停止/重启，采用明确到期日并在模块更新后失效；不表示已经实现任意包、文件、网络、任意进程操作，也不表示已有系统后台任务调度器。模块自己调度任务，Core 只验证并执行获准的封闭动作。

### DC-02 apt 包计划接线（2026-11-01）

新增 `ModulePrivilegePackage` 内部记录，把 Host 批准的来源引用与 Helper 解析出的 `ModulePackageResolution` 一起冻结；`privilege-plans.json` 升为 schemaVersion=5，旧格式拒绝读取且不迁移。`planHash` 增加独立的 `packageHash`，持久化记录的来源、仓库、版本、架构、制品摘要、大小或依赖闭包被改写即 `module.privilege_recovery_required`；服务计划携带包身份、包计划缺失身份同样拒绝。

prepare 对 `package.manage` 先按声明的 apt 能力调用 Helper `ModulePackageQuery`，再按 Debian 版本序核对声明区间；Helper 不可用、来源/仓库未批准、包不在来源策略、解析失败、目标不一致或版本越界都不会生成计划。计划 DTO 新增 `ModulePackagePlanIdentity` 与服务影响/维护者脚本警告，桌面确认页展示来源、仓库、制品与依赖文件清单；客户端拒绝“包计划无冻结身份/服务计划带包身份”的响应。

approve 只发送来源引用与冻结身份。Helper 先按 operationId 查持久回执（重放与中断不会被重新求解），再重解：主制品必须完全一致，apt 仍要下载的依赖必须属于冻结闭包，已安装而不再下载的冻结依赖不算漂移。重解失败、来源/仓库撤销或身份不一致统一返回 `PlanChanged`；Broker 映射为 `module.privilege_plan_changed`，把计划持久化为 `Failed`（已知未执行）并向调用方返回该错误，而不是 `RecoveryRequired`。响应丢失仍是 `RecoveryRequired`，不自动重放。DC-02 时常设许可只允许 `service.manage`；DC-05 起扩展到受控类别（见后节）。

真实 Ubuntu 验收已执行（Tailscale 主机 `100.113.194.85`，Ubuntu 26.04 LTS，x86_64，2 vCPU / 5.2 GiB）：root 运行自包含 linux-x64 的 `--module-package-linux-acceptance`，`mysql-server` 8.4.11-0ubuntu0.26.04.1（8 项依赖）与 `redis-server` 5:8.0.5-1ubuntu0.1（2 项依赖）各 9 项 PASS——策略加载、精确解析、真实 apt 安装与版本/架构核实、同一 operationId 重放拒绝（`Conflict`）、摘要漂移拒绝、来源撤销拒绝（`PlanChanged`）与移除后 `dpkg-query` 核实。验收后已 purge 本次新增的全部包、删除验收载荷与 `/etc/relaxkonos`、`/var/lib/relaxkonos/module-packages`，nginx 等既有包未受影响。原始输出在仓库外 `.artifacts/module-package-ubuntu-acceptance-20261101.log`。

验收同时发现并修复两处缺陷：策略加载器缺少 camelCase 命名策略（文档格式实际不可读）；apply 把“依赖已安装、apt 不再列出”误判为闭包漂移。对应回归加入策略 camelCase/未知字段/PascalCase 拒绝与“依赖子集不是漂移、新增或变更制品是漂移”的检查。

定向检查：`--module-privilege-checks` 88 项 PASS（含包计划冻结、版本区间、冻结身份篡改、Drift 与响应丢失），`--module-package-transaction-checks` 20 项 PASS（含 Debian 版本序、策略解析与依赖子集匹配），完整 ServerModules 回归 **674 项 PASS**，`dotnet build RelaxKonOS.sln` 0 错误。Windows `windows-msi` 适配器、共享依赖/外部安装接管与真实 MySQL 服务闭环仍未交付，因此 DC-02 不标记关闭。

### DC-03 Linux 配置、数据与 MySQL 闭环（2026-11-01）

`file.manage` 的 write/delete 与 `data.manage` 的 preserve/backup/delete 已进入 Host 执行矩阵，但仅 Linux。prepare 先调用 Helper `ModuleFileQuery`/`ModuleDataQuery`，把规范路径、前置状态、内容摘要/大小/模式（文件）或结构摘要（数据根）冻结进计划；approve 只发送冻结的根、路径、前置状态、摘要和确认过的内容。`privilege-plans.json` 升为 schemaVersion=6，`planHash` 分别绑定 `fileHash`/`dataHash`；文件内容不落盘，Core 重启后待确认 write 明确变为 `Expired`；`set-mode`/`set-owner`/`restore` 在 prepare 前返回 unsupported。

Helper 只读 root 拥有、非链接、group/other 不可写的 `/etc/relaxkonos/module-resources.json`，文件根另带 mode/owner/group。文件写入使用同目录暂存加原子 rename，再核对摘要/大小/模式/属主；数据操作递归冻结常规文件相对名、长度和 SHA-256，拒绝符号链接/特殊文件；`preserve` 写保留回执，`backup` 在 Helper 保护状态根生成副本与 manifest，`delete` 在摘要复核后只清空精确根。每个操作在变更前写耐久回执，重放/中断返回 `Conflict`，观察漂移返回 `PlanChanged`。没有脚本、shell、声明根外路径或任意属主名入口。

组合工作流按业务顺序逐项请求已有类型化能力，Host 不接受多步脚本或批量计划。MySQL 闭环顺序为停止实例 → 文件配置发布 → 启动并实测配置生效 → 停止实例 → 数据备份/保留 → 包卸载保留配置/数据 → 独立确认后删除数据。

验证：`--module-resource-checks` **44 项 PASS**（策略边界、file/data 计划冻结、内容不落盘、重启过期、路径/状态/上限、`set-mode`/`restore` 拒绝、PlanChanged、持久化身份篡改与跨类别串用拒绝）；`--module-privilege-checks` 88 项、`--module-package-transaction-checks` 20 项继续通过；完整 ServerModules 回归 **722 项 PASS**；`dotnet build RelaxKonOS.sln` 0 错误。Ubuntu 24.04 root 容器 `--module-resource-linux-acceptance` **13 项 PASS**（配置原子发布、漂移/重放拒绝、数据摘要/备份/保留/清空、根撤销），日志 `.artifacts/module-resource-ubuntu-acceptance-20261101.log`；`--module-mysql-linux-acceptance --install` **14 项 PASS**（真实 MySQL 8.0、`max_connections=123` 生效、真实 `apt-get remove` 后配置/数据保留、随后确认删除），日志 `.artifacts/module-mysql-ubuntu-acceptance-20261101.log`。容器验收中的包/服务步骤由宿主 `apt-get`/`service` 执行，文件/数据步骤由 Helper 执行；不新增 Windows、Ubuntu 26.04 主机或 Helper `NativeServiceAction` 容器证据。Windows、`restore`、`set-mode`/`set-owner` 和配置内秘密值仍未交付；文件/数据常设许可由后续 DC-05 交付。

### DC-04 MySQL 有凭据改密与秘密存储（2026-11-01）

`app.manage` 的 `query`/`rotate-credential` 已进入 Host 执行矩阵；`configure`/`recover-credential` 仍返回 unsupported。请求必须携带一个与清单 `app.manage` 实例/动作及 `secret.reference` purpose/target/permission 匹配的引用；引用不含秘密值、秘密路径、SQL、argv 或账户密码。prepare 调用 Helper `ModuleApplicationQuery` 冻结实例、协议、账户、账户主机、服务器版本、账户存在性和账户秘密的随机 revision；`privilege-plans.json` 升为 schemaVersion=7，`planHash` 绑定独立 `applicationHash`，计划/日志/客户端均不出现密码或秘密路径。

Helper 只承认 root 拥有、非链接、group/other 不可写的 `/etc/relaxkonos/module-applications.json`（当前仅 `mysql-admin`/Unix socket），并读取 root-only 0600 的管理与业务凭据文件。执行时用管理凭据通过固定 `mysql` 客户端（SQL 经 stdin，密码仅走进程环境）执行 `ALTER USER`，用新凭据实测认证，再原子替换业务账户秘密文件并推进 revision。失败会用未变化的管理凭据尝试回滚；回滚失败或提交证据不完整返回 `RecoveryRequired`，Broker 置为待恢复且绝不自动重放。同一 operationId 的已完成改密重放返回待恢复，revision/实例策略/秘密文件漂移返回 `PlanChanged`。

仍未交付：丢失管理凭据恢复（受限恢复启动、停机确认、配置恢复、健康核实和人工恢复均未实现，因此明确拒绝）、MySQL 以外的数据库协议、Windows 应用适配器、模块侧秘密读取/其他秘密值交付、HSM/KMS 或加密秘密存储（当前是 root-only 0600 文件）；应用类别常设许可由后续 DC-05 交付（仅 query）。

验证：`--module-application-checks` **29 项 PASS**（策略/秘密文件格式与边界、query/rotate 冻结、秘密引用匹配、无密码落盘、schemaVersion=7、revision/账户篡改拒绝、Drift、Configure/Recover 拒绝与恢复状态）；Ubuntu 24.04 root 容器 `--module-application-linux-acceptance --install` **13 项 PASS**（真实 MySQL 管理/业务账户、Helper 查询、真实改密、旧密码拒绝、新密码认证、秘密文件原子更新、重放待恢复、revision 漂移拒绝、策略撤销），日志 `.artifacts/module-application-ubuntu-acceptance-20261101.log`；完整 ServerModules 回归 **751 项 PASS**；`dotnet build RelaxKonOS.sln` 0 错误。容器验收不代替 Ubuntu 26.04 或 Windows System/User Mode 实机结论。

### DC-05 受控常设许可与后台执行（2026-11-01）

常设范围扩展为 `service.manage` start/stop/restart、`package.manage` install/update/repair、`file.manage` write、`data.manage` preserve、`app.manage` query。`package.manage remove`、`file.manage delete`、`data.manage backup/delete`、`app.manage rotate-credential` 以及 `configure`/`recover-credential` 始终要求新的具体确认；数据删除永远不能由常设许可授权。文件写入许可新增可选 `contentSha256`（64 位十六进制），许可请求与计划均不携带内容；后台执行时 Broker 重新计算请求内容摘要，缺失、非法或不匹配返回 `module.privilege_confirmation_required`，其他类别携带该字段被拒绝。

后台路径仍经 SDK 进程令牌认证，不使用业务请求 context 或管理员凭据；许可只放行状态 Active、未过期且模块/类别/动作/目标完全匹配的请求。随后仍进入原计划存储：package 重新解析冻结制品，file/data/app 重新调用 Helper 查询冻结当前状态，再由常设管理员身份批准计划。撤销与派发线性化：派发中撤销等待动作完成，不把已派发动作冒充为已取消，撤销完成后拒绝后续派发。到期、模块更新、信任/策略漂移继续使许可失效。`privilege-consents.json` 保持 schemaVersion=2，新增可选 `contentSha256`；桌面组件中心新增 package install/update/repair、file write、data preserve、app query 选项及内容摘要输入，三语言文案同步更新。

验证：`--module-privilege-checks` **106 项 PASS**（原 88 项服务/包检查加 18 项 DC-05：范围矩阵、禁止动作、文件摘要绑定与不匹配拒绝、package/data-preserve/app-query 派发、撤销竞态、更新/信任失效、许可存储与审计）；`--module-resource-checks` 44 项、`--module-application-checks` 29 项、`--module-package-transaction-checks` 20 项继续通过；完整 ServerModules 回归 **769 项 PASS**；`dotnet build RelaxKonOS.sln` 0 错误。原有服务动作两平台真实后台 Helper/撤销/到期/重启幂等证据继续有效；DC-05 新增类别复用既有计划与 Helper 适配器，没有新增任意命令入口。Windows 新类别后台实测、数据备份/删除/文件删除/包移除/凭据轮换的常设许可（按设计不开放）以及 Ubuntu 26.04/Windows System/User Mode 组合仍未验收。

### DC-06 高信任扩展评审结论（2026-11-01）

完成威胁模型与平台隔离评审，结论为当前版本不实现高信任扩展执行。评审覆盖恶意/被攻陷模块、供应链替换、权限扩大、混淆代理、TOCTOU、秘密外泄、资源耗尽、持久化/横向移动、失败残留和审计误导；Linux 的 UID/capability/namespace/seccomp/cgroup/systemd 沙箱与 Windows 的受限令牌/AppContainer/Job Object 均被列为候选但未完成真实逃逸与失败终止实测，因此不作为已交付能力。完整内容见 [高信任扩展评审](RelaxKonOS.HighTrustModuleExtensions.Review.md)。

拒绝边界已锁定：`process.execute`、`high-trust.extension`、`shell.execute`、`command.execute` 返回稳定错误 `module.privilege_high_trust_extension_unsupported`；能力目录不包含这些类别；清单 schemaVersion=3 拒绝未知 `highTrustExtensions` 字段；Helper 协议无 `ExecuteCommand`/`ProcessExecute`/`Shell` 操作，请求 DTO 无 executable/argv/command-line 字段；`ModuleHighTrustExtensions:Enabled=true`、非布尔值或 allowlist 设置使 Server 启动失败，缺省或显式 `Enabled=false` 保持拒绝。普通能力缺少适配器时不会自动升级到高信任执行。

验证：`--module-high-trust-checks` **15 项 PASS**（配置守卫、稳定拒绝码、类别缺失、协议形状、清单未知字段）；完整 ServerModules 回归 **785 项 PASS**；`dotnet build RelaxKonOS.sln` 0 错误。未执行真实高信任程序，也未宣称隔离或失败终止已交付；只有满足评审文档中的重新评审门槛后才考虑实现。


## DC-04 审查修复（2026-11-01）

本轮修复应用策略漂移与改密未知结果：`ModuleApplicationState` 和计划视图新增必需的 `policyRevision`，以非秘密摘要绑定完整实例策略与管理员/业务秘密随机 revision；执行前及秘密提交前复核。摘要不包含密码，路径只在 Helper 内参与摘要。`privilege-plans.json` 当前为 schemaVersion=8，拒绝版本 7，不提供兼容读取或迁移。

改密命令超时、非成功退出、响应未知及变更阶段提交异常均进入 RecoveryRequired；不以普通 Failed 表示数据库未变更，不自动重放。新增策略/socket/秘密修订漂移、存储版本/摘要篡改和真实 ALTER USER 已提交后丢失响应的故障验收。本轮 `dotnet build RelaxKonOS.sln --no-restore -m:1 -p:UseSharedCompilation=false` 通过（0 错误，103 个警告，含 NuGet 漏洞服务不可达的 NU1900）；Linux x64 自包含发布通过。Ubuntu 26.04 上完整 ServerModules 回归 **794 项 PASS**，定向回归 **223 项 PASS**（应用 38、包 20、资源 44、许可 106、高信任拒绝 15），MySQL 8.4.11 实际协议验收 **21 项 PASS**。真实验收包括同一数据库的 socket 别名与秘密路径漂移、管理员秘密修订变化、正常改密、原密码失效、新密码认证，以及真实 ALTER USER 提交后丢失响应和超时均进入待恢复、原操作重放拒绝。

证据：`.artifacts/declarative-fix-solution-build.log`、`.artifacts/declarative-fix-full-regression.log`、`.artifacts/declarative-fix-summary.log`、`.artifacts/declarative-fix-application-ubuntu2604.log`、`.artifacts/declarative-fix-cleanup.log`。完整回归初次因缺少 dotnet、随后因 ZIP 解压未保留 ModuleHost 执行权限中断；补齐运行时和权限后重跑通过。测试账户、秘密、策略及 socket 别名均已清理，原四个 Server/Helper 服务仍 active。为测试安装的 MySQL 与 ASP.NET Core 10 运行时保留，独立发布产物位于 `/home/nanami/declarative-fix-20261101/`。直接调用执行器的真实验收不代替生产 IPC、Windows 或完整 System/User Mode 验收。
