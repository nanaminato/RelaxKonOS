# 代理安全

## 文件所有权与提权边界

代理运行时、配置、状态、GEO 数据和诊断日志是 Server 服务账号拥有的固定目录；Server 对这些目录直接进行原子写入，不经由通用提权接口。Linux 安装器将 `/var/lib/relaxkonos/proxy`、`/etc/relaxkonos/proxy` 与 `/var/log/relaxkonos/proxy` 设为服务账号私有目录；Windows 安装器只向 Server service SID 授予 ProgramData 中 Proxy 根目录的 Modify 权限。每次创建 Proxy 状态、审计或配置目录时，Unix 文件模式会再次收紧为 `0700`；状态、审计、配置及控制器秘密文件均为 `0600`。

只有 systemd unit 的安装、删除及固定服务动作会经由受限的 root Helper。该 Helper 不接受任意文件路径、可执行文件、参数或命令文本。安装、卸载与回滚前后由 Server 记录无秘密操作状态；无法访问受保护目录或 Helper 时安全返回问题码，绝不尝试 sudo 命令回退。

Windows 的“系统代理”实际是交互用户的 `HKCU` 设置。System Mode 中 Server 运行在服务账户，不会把值写入服务账户自己的 HKCU 并谎称已生效；此类变更会失败关闭，直到有受认证的每用户 companion 在目标登录会话中应用它。

订阅内容可能携带代理凭据，绝不写入日志（包括开发环境）。诊断只记录经过脱敏的事件、结果码、字节数及不可逆的来源主机哈希。

公共 API 仅公开引擎无关的安全 DTO。它绝不返回原始 YAML、控制器地址或密钥、订阅凭据、私钥、任意命令参数。控制器日志有大小限制并会脱敏。

任何代理工作流都不会禁用 Defender 或防火墙、开放公共控制器端口、请求操作系统密码，或提供通用特权命令端点。
