# RelaxKonOS User Mode Server — 实施进度

最后更新：2026-09-16

## 已完成

- 明确 `Server:Mode=user|system` 配置边界。User Mode 只能在非 root Linux 进程中启动，要求 `Identity:LinuxPamTransport=in-process`、`Privileges:Backend=disabled` 和 loopback 监听；环境名不参与判断。
- User Mode 的 PAM service 名称经过安全标识符验证，且必须是已存在的宿主 `/etc/pam.d/<service>` 配置；安装过程不写入 PAM。
- User Mode 登录根已移除 alias、多用户映射与“关闭系统登录”策略：匿名登录只验证运行 Server 的当前 Unix 账号；Alias 设置、文件提权与宿主管理员认证入口均由 Server 端拒绝。
- 新增认证后 `GET /api/v1.0/server/capabilities`，并把同一能力快照附在登录响应的 `server.host` 字段中。快照包含模式、实际执行身份、监听范围、PAM transport、冻结的功能布尔值和稳定限制码。
- User Mode 下禁用 Docker、防火墙、SMB 文件服务、Web Server、证书、隧道、代理、系统安装、宿主设置和特权授权端点。调用返回稳定的 `privileged-feature-unavailable` problem type；特权 transport 也改为无操作拒绝实现，不能意外调用 Helper。
- User Mode 的 TCP 请求只接受 loopback；性能进程列表与终止操作也在服务端按实际 eUID 过滤，跨 UID PID 返回 `user-mode-process-not-owned`。
- User Mode 文件服务仅以当前 Unix home 为可浏览根；所有读写/移动/上传路径均在服务端规范化，并拒绝目录外路径及已存在符号链接逃逸。
- 客户端内置应用根据新的服务端能力声明隐藏 Docker、文件服务、Web Server、证书、代理和隧道入口；Guardian 仍可用。
- Guardian 的 User Mode 路径在 Server 与 Agent 两端均拒绝跨用户 `RunAs`，错误码为 `guardian.cross_user_unavailable`；User Agent 不运行受保护 system-service 监控。
- 新增 `user-server` Linux 发布包，只包含 Server、Guardian、用户 launcher 和显式 `install-relaxkonos.sh --mode user` 安装脚本；不含 PrivilegedHelper、sudoers 或 system-service 安装器。
- 新增 XDG 用户态 `relaxkon` lifecycle launcher：本地/HTTPS checksum 安装、逐文件清单校验、loopback 启动、同 UID Guardian、PID + UID + Linux start-time 校验、私有 Unix control socket、停止、状态、升级及失败回滚和卸载。目录、秘密、状态与日志使用用户私有权限。
- 现有系统安装器改为强制显式 `--mode system`，不再隐式调用 sudo；`--mode user` 交给用户态 launcher，且 root 调用会被拒绝。
- Settings 的只读系统页显示 Server 模式、执行 UID/用户名、监听范围和 User Mode 的 SSH tunnel 提示。

## 已验证

- `dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj --no-restore` 成功（仅现有平台分析警告）。
- `dotnet build RelaxKonOS.Guardian.Agent/RelaxKonOS.Guardian.Agent.csproj --no-restore` 成功。
- `bash -n deployment/user/relaxkon deployment/user/install-relaxkonos-user.sh deployment/bootstrap/install-relaxkonos.sh deployment/packaging/package-relaxkonos.sh` 成功。
- `package-relaxkonos.sh 0.0.0-dev linux-x64 Debug … user-server` 成功生成 self-contained Linux 包；JSON manifest 的 604 项文件清单及每项 SHA-256 已验证，payload 只有 Server、Guardian 与 `deployment/user`，没有 Helper、sudoers 或 system-service 安装引擎。
- 对生成的 bundle 执行了用户态本地安装与卸载冒烟测试：XDG 覆盖目录为 `0700`，`install-state.json` 为 `0600`，且均由当前 UID 所有；卸载后四个 RelaxKonOS XDG 子目录均已移除。

## 暂未执行 / 跳过的测试

| 测试 | 原因 | 后续条件 |
| --- | --- | --- |
| Client 项目构建 | 此环境中 `dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore` 未输出诊断即失败；该项目依赖桌面 SDK/workload。 | 在具备 Avalonia/桌面 workload 的 CI 或开发机运行。 |
| User Mode 启动/升级/回滚 | 已验证打包与安装布局；尚未在非 root PAM 环境启动真实 Server，也未覆盖 `/ready` 回滚。 | 在 Linux CI 的无 sudo 测试账号运行完整生命周期。 |
| 本机 loopback/Unix socket HTTP 冒烟测试 | 当前受限容器拒绝进程打开/连接本地 socket（`Operation not permitted`）；因此无法在此处探测新增的 `/ready` 或 control socket。 | 在允许本地 TCP 与 Unix socket 的 Linux CI 运行。 |
| PAM `login` 登录 | 需要非 root 测试账号及宿主现有 PAM 策略；不能在当前共享构建环境安全模拟。 | Ubuntu、Rocky/Alma、Slurm/等价容器各执行一次。 |
| SSH tunnel、ACL、GPU/Slurm、无 user systemd 生命周期 | 依赖真实远程/调度环境与第二 UID。 | 按 Goal 第 9 节验收清单进行环境验证。 |
| System Mode 回归 | 需要 root、systemd、Helper 与 PAM 专用服务。 | 独立的 Ubuntu systemd CI/测试机。 |

## 尚待扩展

- launcher 的私有 control socket、原子升级和 `/ready` 回滚已经实现；仍需要在允许本地 socket 的非 root 集成环境验证整个故障恢复路径。
- 桌面客户端的完整编译仍依赖具备 Avalonia workload 的环境；新增的 Settings UI 已完成 JSON/静态校验。
