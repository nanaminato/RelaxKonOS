# RelaxKonOS 代理管理器——跳过的平台测试

更新日期：2026-08-31

当前实现仅在活动开发主机上完成编译。以下测试被刻意**不**在此运行：它们需要具备特权、可丢弃的专用 Windows 或 Ubuntu 主机，以及隔离的管理连接。Proxy Manager V1 发布前，必须逐项完成这些测试。

| ID | 平台 | 要运行的测试 | 必需验证 | 当前跳过原因 |
|---|---|---|---|---|
| PM-G5-WIN-01 | Windows / Windows Server | 托管 Mihomo 子进程启动、停止、重启、更新与卸载 | 关闭时清理 Server 拥有的进程树；受保护路径和重启行为有效；不修改 Defender 或防火墙 | 没有隔离的特权 Windows 测试主机，也没有获批准的 Mihomo 运行时。 |
| PM-G5-WIN-02 | Windows / Windows Server | 启用、禁用 TUN 与紧急恢复 | 当前 RelaxKonOS 会话、监听器、网关、局域网、SSH/RDP 路由和 DNS 保持可达；恢复原始网络状态 | 会改变主机路由/DNS，不能首次在开发主机上执行。 |
| PM-G5-WIN-03 | Windows / Windows Server | TUN 激活期间崩溃和重启恢复 | Mihomo、Server 和 OS 重启后发现持久标记并恢复安全路由/DNS 状态 | 需要一次性 VM 和第二个管理客户端。 |
| PM-G5-UBU-01 | Ubuntu / Ubuntu Server | 托管 Mihomo systemd 生命周期 | 服务安装、启动、更新/回滚和无头运行 | 当前运行中没有 Ubuntu/systemd 主机。 |
| PM-G5-UBU-02 | Ubuntu / Ubuntu Server | `/dev/net/tun` 启用、禁用和紧急恢复 | 出站接口、路由/DNS 快照、系统绕过与管理连接保持有效 | 需要 `/dev/net/tun`、根级服务操作和隔离管理路径。 |
| PM-G5-UBU-03 | Ubuntu / Ubuntu Server | TUN 激活期间崩溃和重启恢复 | Mihomo、Server 和 OS 重启后，标记驱动的恢复在无桌面会话时仍可工作 | 需要一次性 Ubuntu Server VM 和第二个管理客户端。 |

## 执行顺序

先运行自动化 Server 测试套件，再按所列顺序在一次性 VM 上执行平台用例。初始验证时切勿在生产主机上启用 TUN。每个用例执行时，都应记录环境、Mihomo 资产哈希、RelaxKonOS 修订版本、结果和问题代码。

`RelaxKonOS.Server.Tests` 的进程内覆盖通过模拟网络平台验证故障关闭的 TUN 事务、标记持久化、回滚和紧急禁用路径；它不能替代上述用例。

## 延后的非平台验证

| ID | 要运行的测试 | 当前跳过原因 |
|---|---|---|
| PM-G6-G8-API-01 | 在为代理授权、幂等性、操作恢复和审计输出提供 API 主机夹具覆盖后，运行 `RelaxKonOS.Server.Tests`。 | 本次实现仅完成编译；未启动 Server 进程或测试套件。 |
