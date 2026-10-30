# Tailscale 与组件中心验证（2026-10-30）

## 结果与范围

目标为 `192.168.1.11`（Ubuntu 26.04 / linux-x64）。桌面与 Android 均作为控制端，管理 Linux 服务器或 Windows 11 用户模式宿主；不在 Android 安装 Tailscale VPN。Linux Server/Helper 测试构建已部署，原发布 ZIP 未改动，不等于重新发布正式版本。

| 验证项 | 结果 | 原操作 ID / 证据 |
| --- | --- | --- |
| 来源缺失阻断与显式配置官方签名来源 | 成功；恢复后仍为 External，没有自动接管 | `13ec2a0a-721a-40cd-8a62-a91438f6b2d2` |
| 组件中心首次受管安装 1.104.2 | Succeeded、Managed、原提交 ID 一致 | `7f935c1a-0f03-4555-9cbb-dcc28520f71c` |
| 真实跨版本更新 1.104.2 → 1.104.4 | Succeeded、Managed | `75c70c4f-2be1-4a4c-bc32-a9101ae71703` |
| 保留数据卸载 1.104.4 | Succeeded、Absent，状态文件保留 | `87961494-2e57-4427-b531-7ee434e1cdb9` |
| 从自有卸载状态重装 1.104.4 | Succeeded、Managed；正常处理 Debian 卸载 mask | `ee3a0eeb-158c-4dd4-9a4a-98dc900ba92f` |
| Server/Helper 重启后复核 | 原生命周期回执均保持 Succeeded，组件仍 Managed 1.104.4 | 原操作逐一 GET；控制读取正常 |
| 每次安装、更新、卸载及维护的同键重放 | 均返回原 operationId | 同上 |
| 最终节点身份与组网 | 节点 ID 与原值一致；保留 `100.113.194.85`，Running；到 Windows 对端 `100.79.97.10` 直连成功，6 ms | 宿主受保护状态观察 |
| Nginx 既有组件安装 | 1.28.3-2ubuntu1.12、Managed，配置与 HTTP 检查通过 | `21963f3c-bba7-4305-b4fe-6ff12b6d37b8` |
| FRP 固定文件安装 | frpc/frps v0.71.0、Managed，同键重放通过 | `cb12a859-3171-4970-8529-87bde489958f` |
| FRP 修正后的生产传输 | 显式代理下载 13,932,092 字节，固定 SHA-256 一致 | `.tmp/frp-live-proxy-download-test.log` |

为建立真实所有权，先备份上一轮外部测试安装的登录状态，显式 `dpkg --remove`，再通过组件中心建立新台账。测试用临时 APT 版本 pin 仅选择 1.104.2，更新前已移除，结束时确认不存在；没有 purge 或 autoremove。

1.104.2 的包 SHA-256 为 `4c342bece5cffda61407099e41ab8de663426246dd2c4ebbb1d11f3db0b989f6`；1.104.4 为 `8768973def76dd530b53a0fe74ca396252fff050f955aeebfabd33c77960625d`。每次均冻结官方仓库、完整 pool URI、大小与摘要。

## 修复与门禁

- FRP：显式发布目录替换默认目录，消除重复 pin 造成的计划 500；只对批准的 GitHub release 资产跳转放行签名查询，初始归档查询和通用清单仍拒绝。
- APT：显式请求 SHA-256；用独立空缓存视图解析 URI，已安装或已缓存的软件包仍能冻结完整制品身份。
- Tailscale：闭集 Linux 包与官方签名来源；Windows 独立 `nativeInstaller` / MSI 目标，Helper 校验发行、摘要、发布者、产品与架构；不自动接管外部安装。
- Helper：持久领域操作按原身份进入领域台账，保留普通动词的传输重放拒绝。包变更、控制、登录与服务恢复共用原生锁；未提交或损坏包记录阻止新控制。Windows 不确定 MSI 保留待恢复，不自动重放；Windows 恢复操作在仍存在 MSI 进程时保守阻断，相关 Windows 分支构建通过，未计为 Linux 实机验收。
- Debian 外部卸载留下 mask 时，首次安装正确拒绝（`795de8eb-ddb7-4e86-bca0-16e400421755`，没有伪造成功）。测试准备显式取消该屏蔽；之后只有自有卸载证明与 Debian 标记一致才允许重装，管理员覆盖仍拒绝。
- 移动端：沿用冻结计划幂等键；明确显示目标宿主、版本、来源、摘要和网络中断提示，取消确认不提交。

## 回归与设备检查

- Tailscale 服务端/Helper 领域专项：658 项通过，包含新增包事务互斥与损坏记录检查。
- 桌面 Tailscale 控制/登录：202 项通过；完整桌面安装交互回归 538 项通过，包含模拟 Windows MSI 安装、更新、保留数据卸载、取消和外部实例门禁。
- 服务端组件专项 188 项、Helper socket 专项通过。
- Android 组件/安装契约专项 69 项通过；Debug APK 打包通过。
- Android 手机模拟器（1080×2400、420 dpi）：生产确认框两项仪器测试通过；1.6 倍字体下宿主、来源、摘要和警告可滚动，取消不提交，卸载保留数据提示与忙碌门禁通过。真实渲染图已查看，位于 `.tmp/tailscale-mobile-review-images/`。
- 平板英文补测因安装的 instrumentation runner 与测试命令不符未启动，不计通过；模拟器尺寸与应用语言覆盖已复原。完整语言、主题、平板与辅助功能矩阵仍待验收。

这些测试没有执行 Windows 原生 MSI 安装、驱动、重启或 ARM64 宿主生命周期，不能以计划、签名/暂存或模拟传输替代 Windows 实机验收。FRP 直连 GitHub 超时不计通过；尚未配置真实 FRP 隧道业务。

## 部署与证据

备份和每步 API 证据位于宿主 `/root/relaxkonos-cc-lifecycle-20261030-2140/`（目录 0700、JSON 0600，不保存密码或令牌）。保留旧 Server、Helper、原数据备份及来源文件。最终部署程序集 SHA-256：

- Server：`fd07dee2cf8f9528f4b92dc7b1cb04c0c5ed8b9521ce580305a87337c4fdbc62`。
- Helper：`eb804158869a928256a893dd35f98de9599f13a73f6e48f2d4172569cf316823`。

部署时曾漏带 `data` 符号链接，导致启动失败；已从原目录恢复链接，原 `/var/lib/relaxkonos/server` 数据未改动。结束检查 Server、Helper、Tailscale、Nginx 均 Active，本机 `/healthz` 返回 200。
