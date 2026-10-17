# 代理服务端验收记录

日期：2026-10-17。范围：本次代理服务端设计与实现审查后的工作区代码。运行时固定为 sing-box 1.14.3，下载包已与 `SingBoxManifest` 的 SHA-256 核对。

## 修复与补齐

- Linux systemd 允许内核初始化所需的 `AF_NETLINK`，保留 DynamicUser、只读系统目录和私有配置凭证。
- 正确识别禁用开机启动的 systemctl 返回码；主动停止同时取消自动重启，拒绝停止无本应用所有权标记的 unit。
- 发布失败恢复配置时同步恢复摘要；补偿启动可处理 systemd 的失败次数限制及已卸载的 unit。
- Server 在调用 Helper 前持久化候选事务；重新读取或启动时按 Helper 摘要确认提交/补偿。回滚使用相同发布事务，并保留单步边界；无法确认时阻止普通变更，紧急停止仍可用。
- 拒绝通过回滚重新启用已撤销、轮换或过期的凭据；拒绝保留凭据时改变入站认证类型；停用或过期凭据不再显示为已应用。
- 订阅签发持久化幂等记录，重放只返回原记录，不再次生成或披露 token。撤销统一返回异步操作，桌面与 Android 使用一致契约。
- 导出保留内置标签名称，避免选择器自引用和重复标签；YAML 字符串使用转义标量，保留多行及控制字符的值而不改变文档结构。
- 在线安装的总超时覆盖正文读取；受保护的固定 `runtime.archive` 缓存使用相同摘要、大小及归档校验。

## 已通过

| 验证 | 结果 |
| --- | --- |
| Server、Helper、Avalonia 客户端构建 | 通过 |
| `Tests/ProxyServer` 服务层回归 | 通过，含幂等冲突、秘密保护、凭据形状、过期、回滚及提交中断模拟 |
| 固定 Windows 内核 `sing-box check` | 五种预设的服务端及 sing-box 客户端配置均通过；TLS 测试使用即时生成的测试证书 |
| Linux systemd 实机 API | 登录、能力发现、安装、保存、原生校验、发布、启动、三格式导出通过 |
| Linux 原生协议连接 | 使用 API 导出的 sing-box 配置，经 VLESS + REALITY + Vision 访问外部 HTTPS，HTTP 200 |
| 目的网络限制 | 本机可直接访问的管理健康端点无法经代理访问 |
| 凭据轮换 | 旧配置连接失败，新导出配置仍可连接 |
| 凭据停用及到期 | 应用停用后连接失败；定时到期发布完成后连接失败 |
| HTTPS 订阅 | 匿名下载、no-store、签发重放、异步撤销及撤销后 404 通过 |
| 端口冲突补偿 | 候选监听与 Server 的 5000 端口冲突时发布失败，旧配置恢复运行；再次真实连接成功 |
| Linux 生命周期 | 开机启动 enable/disable 状态、同版本 upgrade、保留数据卸载并重装通过 |

Android 相关测试为 `ProxyServerRepositoryTest` 5 项和 `OperationCenterTest` 7 项，均无失败或跳过。移动端支持范围仍以 [Android 专属文档](../../Client/RelaxKonOS.Client.Android/docs/features/ProxyServer.md) 为准。

可重复的本地原生配置检查：

```powershell
dotnet run --project Tests/ProxyServer/RelaxKonOS.ProxyServer.Tests.csproj -- <受信任的本机 sing-box 可执行文件>
```

## 验证边界与测试主机状态

本次没有执行 Windows SCM 的实际安装与生命周期、其他四种协议的真实客户端互通、v2rayNG/v2rayN/Mihomo 的客户端矩阵、UDP/IPv6/公网 NAT、防火墙自动规则和强制断电测试。原生配置校验不等于协议互通验收；中断模拟也不等于真实主机断电。统计和日志仍按能力明确不支持。

测试服务器保留本次 Server/Helper 构建及可回退的旧版本。测试入站、使用者和凭据已清理，代理内核已停止且禁用开机启动；运行时保留安装。测试监听只绑定回环地址，没有新增防火墙开放规则。

构建存在原有 NuGet 依赖安全告警及审计源不可用警告；本次未升级无关依赖。
