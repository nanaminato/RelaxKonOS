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
- [ ] 隔离 Windows/Linux 主机上的真实 SCM/systemd、重启、升级和卸载验收。
- [ ] 下一阶段：选择性卸载契约、引擎及桌面/Android 界面。

## 当前状态与下一步

已核对：Windows 三类组件原先由 Helper 启动并在 Helper 退出时停止；Linux FRP 原先由 Server 持有。Linux Nginx、Mihomo、SMB 已有独立服务。

Windows 服务宿主复制完整发布目录到 `%ProgramData%\RelaxKonOS-Components\host\<package-hash>`，按组件根目录和 FRPC 配置生成独立服务名 `RelaxKonOSComponent-<kind>-<scope-hash>[-profile-guid]`；服务配置、进程状态及脱敏日志存入 `services/`。Helper 退出不停止组件。宿主与配置只允许 SYSTEM/Administrators 写入，SCM 变更前核验服务 ImagePath 和受保护配置；二进制启动前校验摘要。Job Object 负责宿主意外退出时清理子进程树，SCM 配置失败恢复。

Windows Mihomo 信任清单已移动到 `Shared/RelaxKonOS.Protocol/Proxy/MihomoRuntimeManifest.cs`，Server 与 Helper 共用。Server 暂存 ZIP，Helper 独立核验官方包 SHA-256 并导入管理员保护的 `Proxy/service-runtime/<release>/mihomo.exe`，不把 Server 可写文件直接作为 LocalSystem 服务程序。

Linux FRP 使用 `/var/lib/relaxkonos-components/frp/versions` 与 `instances/`，FRPS unit 为 `relaxkonos-frps.service`，FRPC 为 `relaxkonos-frpc-<guid>.service`。Helper 校验固定发布包 SHA-256、仅导入两个可执行文件、生成受限 TOML 与 unit；服务以非 root 的 `relaxkonos-frp` 系统账户运行，能力仅保留绑定低端口。`/etc/relaxkonos/frp-archive-root` 由 root 部署，限定 Server 上传包来源。外部修改的 unit 拒绝覆盖，服务应用失败尝试恢复前一配置。

Helper 协议已直接升级 1.5，统一为 `ManagedRuntime` 操作、`managedRuntime` 请求字段和 `componentProcess` 观测字段。结构化请求禁止执行路径/命令/环境注入，FRP Start 必须携带应用证明。FRPC 配置指纹、FRPS appliedRevision 均由独立服务持久化，Server 重启后重新读取；状态还核验服务、PID、启动时间与进程映像。原 Windows/Server 子进程宿主路径已移除。

主要实现文件：Helper 的 `WindowsComponentService.cs`、`WindowsComponentJob.cs`、`WindowsMihomoServiceManager.cs`、`LinuxFrpServiceManager.cs`；Server 的 `ManagedRuntimeOperations.cs`、`FrpTunnelProvider.cs`、`ManagedFrpsService.cs`；Shared 的 `ManagedRuntimeContracts.cs`。默认 Linux 卸载不再停止 Mihomo，重新安装也不主动改变保留组件的运行状态。

下一步先在隔离主机验收：服务启动/停止/异常退出、宿主崩溃后无孤儿进程、Server/Helper 停止后组件继续运行、OS 重启、配置回滚与完整卸载。然后实施选择性卸载：新增逐组件保留/移除选项，贯通 ServerCenter 契约、部署引擎和客户端；保留组件时必须保留其配置和所有权记录。

当前边界：选择性卸载界面和选项尚未实现；仍只有保留数据与完整删除数据两种流程。Windows 部分组件目录及 FRP 的控制面数据库仍在原数据根下，当前不得一边删除数据根一边保留这些组件。下一阶段需决定将组件数据/所有权移到独立持久化根，或在保留组件时阻止删除相关数据，不能仅跳过服务卸载。

## 验证记录

已通过：Helper 构建（零警告）；Server/测试工程构建（首次构建有原有 CA1416 平台警告，增量构建零警告）；`--independent-component-services-only`（含真实 Windows Job Object 关闭杀子进程测试）；`--frps-only`（Server Dispose 后服务保留及重建管理器恢复 appliedRevision）；`--frpc-state-only`；`--windows-privileges-only`；卸载 Python 隔离测试、Windows 保留数据/清理回执测试；Bash 语法与 `git diff --check`。

FRPC 的 Linux 生命周期检查已改为独立服务传输替身，并增加重建 provider 后恢复应用证明的断言；当前 Windows 开发机未执行 Linux 专项。真实 SCM/systemd 安装验收未执行，必须使用隔离测试主机，不以本开发机上的宿主服务作为试验对象。测试未创建或修改本机的系统服务。
