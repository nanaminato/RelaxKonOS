# 独立组件服务改造进度

更新日期：2026-10-05。此文件是本任务的恢复入口；每完成一项实现或验证立即更新。

## 目标与约束

Windows Nginx、Mihomo、FRPC/FRPS 使用独立 SCM 服务；Linux FRPC/FRPS 使用独立 systemd 服务。Server 与 Helper 仅管理这些服务，不持有生产组件进程。保留组件卸载后，组件不依赖 Server、Guardian、Helper 的程序目录或存活。每个 FRPC 配置对应独立实例。配置与状态必须持久化，状态查询不能依赖 Server/Helper 内存中的 PID。

不保留旧进程宿主兼容路径；同步更新安装、卸载、测试和文档。Windows 使用专用服务宿主入口，仅接受受保护的组件配置，不开放通用命令执行。已有用户改动不得覆盖。Android 详细文档仍归 Android docs 所有。

## 实施清单

- [x] Windows 专用服务宿主、受保护配置与独立安装目录。
- [x] Windows Nginx 管理入口改为 SCM 生命周期。
- [x] Windows Mihomo 管理入口改为 SCM 生命周期。
- [x] Windows FRPC/FRPS 按实例注册服务，持久化状态和日志。
- [x] Linux FRPC/FRPS 特权 systemd 管理入口与固定资源边界。
- [x] Server FRP 管理改用独立服务；外部运行时仍归普通 Server 身份。
- [x] 默认卸载保留独立组件服务；彻底卸载先清理服务再删除数据。
- [x] 更新部署、恢复与架构文档。
- [x] 构建、受限路径验证、模拟生命周期与卸载隔离测试。
- [x] 隔离 Windows/Linux 主机上的真实 SCM/systemd、重启、保留数据卸载/重装与组件清理验收。
- [ ] GUI/API 控制面完整回归、版本间升级及选择性删除数据的验收。
- [x] 选择性卸载契约、引擎及桌面/Android 界面（验证结果见后续记录）。

## 当前状态与下一步

已核对：Windows 三类组件原先由 Helper 启动并在 Helper 退出时停止；Linux FRP 原先由 Server 持有。Linux Nginx、Mihomo、SMB 已有独立服务。

Windows 服务宿主复制完整发布目录到 `%ProgramData%\RelaxKonOS-Components\host\<package-hash>`，按组件根目录和 FRPC 配置生成独立服务名 `RelaxKonOSComponent-<kind>-<scope-hash>[-profile-guid]`；服务配置、进程状态及脱敏日志存入 `services/`。

Helper 退出不停止组件。宿主与配置只允许 SYSTEM/Administrators 写入，SCM 变更前核验服务 ImagePath 和受保护配置；二进制启动前校验摘要。Job Object 负责宿主意外退出时清理子进程树，SCM 配置失败恢复。

Windows Mihomo 信任清单已移动到 `Shared/RelaxKonOS.Protocol/Proxy/MihomoRuntimeManifest.cs`，Server 与 Helper 共用。Server 暂存 ZIP，Helper 独立核验官方包 SHA-256 并导入管理员保护的 `Proxy/service-runtime/<release>/mihomo.exe`，不把 Server 可写文件直接作为 LocalSystem 服务程序。

Linux FRP 使用 `/var/lib/relaxkonos-components/frp/versions` 与 `instances/`，FRPS unit 为 `relaxkonos-frps.service`，FRPC 为 `relaxkonos-frpc-<guid>.service`。

Helper 校验固定发布包 SHA-256、仅导入两个可执行文件、生成受限 TOML 与 unit；服务以非 root 的 `relaxkonos-frp` 系统账户运行，能力仅保留绑定低端口。`/etc/relaxkonos/frp-archive-root` 由 root 部署，限定 Server 上传包来源。外部修改的 unit 拒绝覆盖，服务应用失败尝试恢复前一配置。

Helper 协议统一为 1.0，统一为 `ManagedRuntime` 操作、`managedRuntime` 请求字段和 `componentProcess` 观测字段。结构化请求禁止执行路径/命令/环境注入，FRP Start 必须携带应用证明。FRPC 配置指纹、FRPS appliedRevision 均由独立服务持久化，Server 重启后重新读取；状态还核验服务、PID、启动时间与进程映像。原 Windows/Server 子进程宿主路径已移除。

主要实现文件：Helper 的 `WindowsComponentService.cs`、`WindowsComponentJob.cs`、`WindowsMihomoServiceManager.cs`、`LinuxFrpServiceManager.cs`；Server 的 `ManagedRuntimeOperations.cs`、`FrpTunnelProvider.cs`、`ManagedFrpsService.cs`；Shared 的 `ManagedRuntimeContracts.cs`。默认 Linux 卸载不再停止 Mihomo，重新安装也不主动改变保留组件的运行状态。

本轮已完成隔离主机的服务生命周期、异常恢复、OS 重启、保留数据卸载/重装和组件清理验收。选择性卸载已贯通 ServerCenter 契约、部署引擎和桌面/Android 界面，并完成两平台的真实启动器选择性卸载与原路径重装。GUI/API 全流程、跨版本升级及其他组件组合仍需单独回归。

当前边界：已增加 SMB、Nginx、FRP、Mihomo 逐组件移除选择，未选组件保留运行。保留任一组件时保留整个数据根及管理记录；完整删除数据必须移除全部四项。Docker Engine、容器和卷不在这四项清理范围内。Linux 用户模式不拥有系统组件。清理按固定顺序执行并验证所选项目回执，失败保留程序、Helper 与数据。

## 验证记录

### 选择性卸载与重复重启验收（2026-10-05）

已在用户明确授权的隔离主机 `192.168.1.2`（Ubuntu 26.04 x64）和 `192.168.1.9`（Windows Server 2022 x64）部署 `0.3.0-selective-20261005`，共享 Helper 协议保持 `1.0`。凭据只在测试进程中使用，不保存到代码或文档。

| 检查 | Ubuntu | Windows Server |
| --- | --- | --- |
| FRPS、两个 FRPC、Mihomo，Windows 另含 Nginx | 通过 | 通过 |
| 单个 FRPC 停止/启动不影响另一个 | 通过 | 通过 |
| 停止 Server/Guardian 后组件继续运行 | 通过 | 通过，另停止 Helper |
| 真实 OS 重启后自动恢复、重新读取 FRP 应用证明 | 通过 | 通过，五个 SCM 服务恢复 |
| 真实部署启动器选择移除 Nginx/FRP、保留 Mihomo 与数据库 | 通过 | 通过 |
| 按原路径重新安装，Mihomo 保持运行、Server 健康检查 | 通过 | 通过 |
| 缺少 Mihomo 管理记录时阻止完整删除数据 | 本轮以隔离脚本测试覆盖失败保护 | 实机确认失败回执，程序、Helper、数据库保留 |
| 生产 Helper 清理验收程序创建的组件后，完整删除数据 | 通过 | 通过 |

选择性卸载的操作 ID：Linux `fc21f614-df54-43c8-adda-ac33dfcbfbc7`，Windows `c7cda2df-1acf-45d4-bfc1-a3daf0612443`；主机外置部署日志与回执保留。验收程序直接安装的 Mihomo 没有 Server 的管理记录，因此未伪造数据库或运行时状态来绕过保护：先验证拒绝清理，再用生产 Helper 移除测试服务及其固定版本暂存文件，最后运行完整卸载。Linux 的停止服务与移除 unit 是两个动作，验收清理程序现按此顺序执行，最终确认无运行中的测试服务。

本地通过：Server 构建/两 RID 发布、桌面构建、Android `compileDebugKotlin`；Windows 精确选择回执与失败保护、Linux 默认保留和部分选择清理、非法选择/重复选择/保留组件加全量删除拒绝测试；PowerShell/Bash 语法检查。Windows PowerShell 5 的 UTF-8 脚本需 BOM，已保留此编码要求。构建存在原有平台/空值/XAML 警告。

边界：本轮移除组合为 Nginx/FRP，保留组件实际检查为 Mihomo；未声称真实 SMB 共享清理、Mihomo 的完整 GUI 安装/重连、桌面交互或 Android 设备交互已验收。保留整个数据根是当前契约，未实现按组件切分数据库或选择性删除数据目录。Docker Engine、容器及卷不属于四项选择。测试服务与 Server 主数据已清理，外置部署回执保留。

### 隔离主机实测（2026-10-05，已完成本轮）

用户授权在 192.168.1.2（Ubuntu 26.04 x64）与 192.168.1.9（Windows Server 2022 x64）清理旧服务和数据并测试。旧安装已清理；两台主机已部署本次 `0.3.0-independent-20261005` 发布包，Server `/healthz` 均通过。登录凭据不写入本文件或测试代码。

真实协议验收入口为 `Tests/Deployment/IndependentServices.Acceptance/`，必须显式传入 `--confirm-isolated-host`。使用固定的隔离 FRPC profile、回环地址和测试端口；两个实例用于检查实例隔离，独立 runner 进程用于检查持久化状态。

| 验收项 | Ubuntu 26.04 | Windows Server 2022 |
| --- | --- | --- |
| FRPS + 两个 FRPC 注册、状态、appliedIdentity | 通过 | 通过 |
| 单个 FRPC 停止/启动不影响另一个实例 | 通过 | 通过 |
| Mihomo 独立服务启动 | 通过 | 通过 |
| Nginx 独立服务 | 本轮未重装 Linux Nginx | 通过 |
| Server/Guardian 停止后继续运行 | 通过 | 通过 |
| Helper 停止后继续运行 | Linux 为单次调用进程 | 五个 SCM 服务全部通过 |
| 异常退出恢复 | FRPC 子进程 SIGKILL 后 systemd 恢复 | 宿主进程强杀，Job 清理旧子进程，SCM 恢复 |
| OS 重启后自动启动及 FRP 应用证明恢复 | 通过 | 五个 SCM 服务全部通过 |
| 真实默认卸载删除 Server 程序但组件保持运行 | 通过 | 五个组件 PID 不变 |
| 保留数据原路径重装后重新管理 FRP | 通过 | 通过 |
| 占用 FRPS 新端口导致应用失败，恢复旧配置/证明 | 通过 | 修复就绪竞态后通过 |
| Helper 协议卸载所有测试组件 | 通过 | 五个 SCM 服务全部移除 |
| 随后真实 RemoveData 卸载及外置清理回执 | 通过 | 通过 |

实测修复了三项问题：

1. Windows 上传包策略使用经过 `server/data` junction 的路径，而 Helper 禁止 reparse point。部署策略改用物理 `$serverData`；Server FRP/Nginx 暂存目录统一通过 `ServerDataDirectory.Resolve` 取得物理路径，并加入路径解析回归检查。
2. Program Files 的标准目录 ACL 包含 Windows TrustedInstaller 写权限，独立宿主检查误判不安全。仅把固定 TrustedInstaller SID 加入目录级信任名单，文件所有者及其他写权限仍限制为 SYSTEM/Administrators。
3. Windows 以子进程短暂存活判定启动成功，FRPS 端口冲突时未触发回滚。现在要求同一启动时间的子进程持续稳定运行一秒，最多等待十秒；真实占用端口场景已确认恢复原配置与 appliedIdentity。

两台测试主机直连 GitHub 发布资产超时，使用本地下载后核验 SHA-256 的离线资产继续验证。此网络限制不算服务逻辑通过项。验收直接使用生产 Helper 协议与真实服务管理器，不通过桌面/Android GUI；控制面数据库没有人为伪造安装记录。完整删除数据前先通过 Helper 协议移除测试组件，并清除 runner 手工暂存的 Mihomo 文件，再运行真实 RemoveData 引擎。

测试结束状态：两台主机的本轮 Server/Guardian/Helper、独立测试组件服务、程序/数据根和临时上传包已清理，外置卸载回执保留。Windows 未使用的独立宿主缓存一并清理；本地 DPAPI 临时凭据已删除，远程编排目录忽略提交。系统自带 Windows SMB 与 SSH 服务未移除。

已通过：Helper 构建（零警告）；Server/测试工程构建（首次构建有原有 CA1416 平台警告，增量构建零警告）；`--independent-component-services-only`（含真实 Windows Job Object 关闭杀子进程测试）；`--frps-only`（Server Dispose 后服务保留及重建管理器恢复 appliedRevision）；`--frpc-state-only`；`--windows-privileges-only`；卸载 Python 隔离测试、Windows 保留数据/清理回执测试；Bash 语法与 `git diff --check`。

FRPC 的 Linux生命周期单元检查使用独立服务传输替身，并增加重建 provider 后恢复应用证明的断言；实际 Linux systemd 和 Windows SCM 验证已在上述隔离主机完成，本开发机仅执行 Job Object 子进程测试，未创建系统服务。
