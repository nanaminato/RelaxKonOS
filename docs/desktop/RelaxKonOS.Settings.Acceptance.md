# 桌面设置验收记录

验收日期：2026-10-06（Asia/Shanghai）。当前代码的自动化测试与远程已部署版本的实机结果分别记录，不能互相替代。

## 实机

用户授权移除旧服务后，`192.168.1.2`（Ubuntu 26.04）已替换为当前测试构建，`192.168.1.5`（Debian 13）已新装当前测试构建。两台地址均为 `https://IP:5000`；Server/Guardian 为 active，运行目录为 `/opt/relaxkonos/settings-test-20261006/`，Server 用户为 `relaxkonos-server`。192.168.1.2 原数据保留，原配置与数据的保护副本位于远程 root 私有目录，不包含在仓库内。

| 能力 | 结果 | 范围 |
| --- | --- | --- |
| 时间格式 | 通过 | 实际修改、GET 读回、确认持久化、旧 revision 返回 409；原值已恢复 |
| 内置壁纸 | 通过 | 实际修改、GET 读回、确认持久化、旧 revision 返回 409；原值已恢复 |
| 深浅模式 | 通过 | 实际修改、GET 读回、确认持久化、旧 revision 返回 409；原值已恢复 |
| 时区 | 两台通过 | 真实授权、普通计划应用和读回；通过普通修改手动恢复原时区 |
| 主机名 | 读取与预览通过 | 当前值的重复修改被拒绝，合法测试名称成功创建计划；未应用重命名或触发重启 |
| 出站代理 | 读取通过 | 未修改代理或重启 Docker |
| 远程网络读取 | 两台通过 | 新接口返回真实远程网卡；192.168.1.2 无 NetworkManager，明确只读；192.168.1.5 的 ens33 可配置 |
| Debian 远程 IPv4 / DNS | 完整实机通过 | 保持原地址 192.168.1.5/24，切换手动 IP/DNS，NetworkManager 未确认自动恢复；确认后保留更改；普通修改恢复 DHCP/自动 DNS；最终原 IP 保持不变 |
| Workspace 环境 | 两台通过 | 唯一临时变量实际写入、GET 读回和删除；未修改宿主变量或其他工作区变量 |

两台基础设置验收各 19 项通过，时间格式、壁纸和主题恢复前检查 revision，避免覆盖其他客户端的并发更改。网络测试仅在新装 Debian 主机执行，保留原 IP、前缀和网关；未安装新的网络管理器或迁移 Ubuntu 的网络 owner。测试凭据与令牌不进入源文件、结果报告或日志。复用入口为 [SettingsApiAcceptance.ps1](../../Tests/Deployment/SettingsApiAcceptance.ps1)、[HostTimeApiAcceptance.ps1](../../Tests/Deployment/HostTimeApiAcceptance.ps1) 和 [HostNetworkApiAcceptance.ps1](../../Tests/Deployment/HostNetworkApiAcceptance.ps1)。

## 当前代码自动化

以下项目通过：

- `RelaxKonOS.Settings.Tests`：导航、200 项搜索、主机名规则、环境变量、Linux 代理/恢复、系统风格与主题规范、设置 CLI。
- `RelaxKonOS.WorkspacePreferences.Tests`：真实页面修改、令牌刷新、连续保存、身份隔离、失败分类、草稿保留与过期响应隔离。
- `RelaxKonOS.Wallpaper.Tests`：内置图片解码、渐变、自定义图状态、偏好往返和内置壁纸不上传/下载。
- `RelaxKonOS.WindowPreviews.Tests --settings-interaction-only`：设备偏好、键盘与滚动、三语言、窄窗口、远程网卡、授权取消、确认操作及断连不重放。
- `RelaxKonOS.Server.Tests --settings-only`：SQLite 重启持久化、并发与租户隔离、设置通知、HTTP 错误分类与日志、宿主授权、Workspace 环境和终端消费。
- `RelaxKonOS.Server.Tests --host-settings-only`：远程网络参数验证、授权、操作归属、确认后不重放及现有宿主接口。

补测发现新增网络和个性化渐变使用固定颜色，导致系统风格检查失败。已改用 Accent 和 TextOnAccent 等主题资源，系统风格与页面回归重新通过。

网络验收脚本的两处错误已修正：PowerShell 数组的 `Address` 方法不能用来读取每个地址元素；自动 DNS 的空列表必须写为 `[]`，不能让管道将它转换成 `null`。实际网卡配置、独立超时恢复与确认保留均经远程 API 和 NetworkManager 状态交叉核对。

## 待完成

仍未实机覆盖 Windows 网卡写入／恢复、真实 Wi-Fi 硬件，以及两个 GUI 客户端同时接收更新。Windows 写入与 Wi-Fi 扫描/加入不能用本次 Linux 以太网验收代替；Wi-Fi 扫描/加入和 IPv6 写入仍未实现。Ubuntu 的非 NetworkManager 网络配置保持只读。192.168.1.4 无法访问后按用户指定改用 192.168.1.5。
