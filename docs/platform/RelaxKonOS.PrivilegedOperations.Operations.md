# RelaxKonOS Privileged Helper 运维指南

本指南适用于以最小权限账户运行的 `RelaxKonOS.Server`。Server 不应以 root、
LocalSystem 或 Administrator 身份运行；所有成功的宿主特权操作必须有 Helper transport
审计记录。

## Linux

使用签名发布包中的 `deployment/linux/install-relaxkonos-services.sh` 安装。安装程序会：

- 创建 `relaxkonos-server` 系统账户；
- 将 Helper 发布目录、sudoers 与策略文件设为 root 所有且 Server 用户不可写；
- 将手动 grant、管理员自动路由、root 会话的文件根分别写入
  `/etc/relaxkonos/privileged-helper-roots`、`privileged-helper-roots-administrator`、
  `privileged-helper-roots-root`；服务 ID 写入 `/etc/relaxkonos/privileged-services`；
- 仅允许 Server 用户以 `sudo -n` 调用 Helper apphost 的三个精确入口：无参数特权协议、`--user-execution` 和 `--user-terminal`。

### Docker 访问（显式选择）

Docker Unix socket 的控制权近似 root 权限，因此部署默认**不会**把 Server 服务账户加入
`docker` 组。若确定要让 Docker Manager 管理本机 Engine，必须在 System Mode 安装时显式传入
`--docker-access`：

```bash
sudo deployment/bootstrap/install-relaxkonos.sh --mode system --bundle /path/to/release --docker-access
```

安装器会将这项选择写入 root-only 策略文件；若 Docker 已安装，会将 `relaxkonos-server` 加入
`docker` 组并重启 Server。若 Docker 由 RelaxKonOS 之后安装，Helper 在安装后执行同一固定授权，
并将安装任务标为“需要重启”；重启 `relaxkonos-server` 后再刷新 Docker 状态。未选择该选项时，
Docker 安装会在修改主机前拒绝执行。

安装后检查：

```text
systemctl status relaxkonos-server relaxkonos-guardian
sudo -u relaxkonos-server sudo -n /usr/local/lib/relaxkonos/privileged-helper/<apphost>
```

第二个命令没有 JSON 请求时必须失败；它只能证明 sudoers 指向固定 apphost，不能用于
执行命令。sudoers 中的 `""` 明确表示无参数，另外两条规则只匹配固定的 user-execution 入口。
不要增加通配符、shell、额外参数或可由 Server 选择的命令路径。

### 文件访问配置

安装器对三类授权来源默认都使用 `restricted`。手动 grant 与管理员来源默认仅允许：

```text
/etc/relaxkonos
/var/lib/relaxkonos
```

root 会话范围在此基础上增加 `/root`。实际第二个根以安装时的 `--data-root` 为准。
Linux System Mode 文件路由规则见 [宿主管理员身份与执行路由 Goal](./RelaxKonOS.HostPrivilegeRouting.Goal.md)。

可在常规安装参数之后明确选择下列模式：

```bash
# 默认；适合生产环境。
sudo deployment/linux/install-relaxkonos-services.sh ... --file-access restricted

# 从审查过的白名单文件安装策略。
sudo deployment/linux/install-relaxkonos-services.sh ... \
  --file-access whitelist \
  --file-roots deployment/linux/privileged-helper-roots.example

# 仅将 root 会话范围设为整机；不会扩大标准用户临时 grant。
sudo deployment/linux/install-relaxkonos-services.sh ... --root-file-access full

# 管理员会话使用独立白名单。
sudo deployment/linux/install-relaxkonos-services.sh ... \
  --administrator-file-access whitelist \
  --administrator-file-roots /path/to/admin-roots
```

`whitelist` 文件每行一个绝对目录；空行和以 `#` 开头的注释会被忽略。可从
[`privileged-helper-roots.example`](../../deployment/linux/privileged-helper-roots.example)
复制后删除不需要的条目。一个根目录会授权其所有子路径的读取、写入、删除、移动、复制、上传和创建目录。
因此不要将 `/`、`/etc`、`/home` 或 `/tmp` 写入生产白名单；尤其不要把 `/etc/ssh` 加入通用文件操作，
否则有权限使用文件功能的用户可读取 SSH 主机私钥。

三个来源各有 `--file-access`、`--administrator-file-access`、`--root-file-access` 参数，均可取
`restricted|whitelist|full`，白名单文件分别用 `--file-roots`、`--administrator-file-roots`、`--root-file-roots` 指定。
只扩大一个来源不会自动扩大另两个来源。`full` 将 `/` 写入对应策略；尤其 `--root-file-access full`
意味着信任 Server 进程提交的 root 会话来源标签。Helper 目前不能独立验证会话证明，已被攻陷的
Server 可伪造来源并获得该策略范围内的封闭文件能力。只有接受这项部署风险时才可启用整机范围，
并继续保持 Server 服务账户没有通用 sudo 或 shell 授权。

开发安装脚本也支持相同参数。例如，调试受保护文件流程时可以使用独立夹具：

```bash
sudo deployment/linux/install-relaxkonos-privileged-helper-development.sh "$USER" \
  --file-access whitelist \
  --file-roots deployment/linux/privileged-helper-roots.example
```

该开发脚本会写入同三份系统策略文件，也支持上述管理员和 root 的独立参数；不要在同时运行生产
Server 的主机上将任何来源切换为 `full`。

若增加受保护文件根或可控制服务，修改前必须进行安全审查；策略文件必须保持
`root:root`、`0600`。重装服务会根据所选模式重建文件策略。

## Windows Server

使用提升的会话运行 `deployment/windows/Install-RelaxKonOSServices.ps1`。它会安装：

- `RelaxKonOSPrivilegedHelper`：LocalSystem Windows Service；
- `RelaxKonOSServer`：LocalService，启用 service SID；
- `RelaxKonOSGuardian`：安装器声明的 Guardian 服务。

Helper 仅监听本机命名管道。管道 ACL 仅包含 LocalSystem、Administrators 和 Server
service SID；每条消息还必须通过安装时生成的共享密钥 HMAC 验证。`helper.json` 只能由
LocalSystem 与 Administrators 读取；Server 仅能读取自身 `appsettings.host.json` 中的密钥。
同一 operation ID 在十分钟内只能处理一次；客户端必须为新的操作生成新 ID，重复 ID 会被
Helper 以冲突结果拒绝而不会再次执行。
安装脚本还会把 Helper apphost 的 SHA-256 写入该受保护配置；Helper 启动时必须匹配该
清单，完整性不匹配时不会监听管道。升级或修复 Helper 必须重新运行安装脚本，不能直接
替换可执行文件。

安装后检查服务状态和 Event Viewer 中 `RelaxKonOSPrivilegedHelper` 的事件。若 Helper
缺失、密钥不匹配或协议版本不匹配，Server 必须返回 Helper 不可用，不能回退为启动提升的
可执行文件。

### Windows 文件访问配置

`Install-RelaxKonOSServices.ps1` 默认使用 `-FileAccess restricted`，仅允许
`%ProgramData%\RelaxKonOS`。可在提升的 PowerShell 会话中选择：

```powershell
# 默认的最小权限策略。
.\deployment\windows\Install-RelaxKonOSServices.ps1 -FileAccess restricted

# 从 JSON 白名单文件加载受管目录。
.\deployment\windows\Install-RelaxKonOSServices.ps1 `
  -FileAccess whitelist `
  -FileRootsFile .\deployment\windows\privileged-helper-roots.example.json

# 授权安装时所有已就绪的本地卷（C:\、D:\ 等）；不包含 UNC 网络共享。
.\deployment\windows\Install-RelaxKonOSServices.ps1 -FileAccess full
```

白名单文件必须是 JSON 字符串数组，且每项为绝对 Windows 或 UNC 路径；可从
[`privileged-helper-roots.example.json`](../../deployment/windows/privileged-helper-roots.example.json)
复制并仅保留所需目录。白名单根及其所有后代可用于读取、写入、删除、移动、复制、上传和创建目录。
不要添加 `C:\`、`C:\Windows`、用户配置文件根目录或存放私钥的目录，除非所有有文件功能权限的
RelaxKonOS 用户都可信。`full` 不是单次管理员提升，而是放宽整个文件接口；仅应在隔离测试环境使用。

日常开发可改用 `RelaxKonOS.PrivilegedHelper.exe --console --config <debug-config>`。这不是降低
生产权限模型的替代品：控制台配置必须单独创建并显式启用，管道只允许"启动 Helper 的账户"以及在
`developerUserSids` 中显式列出的身份；其协议、HMAC、
重放保护和操作分发器与服务模式相同。生产安装目录中的 `helper.json` 不能用作控制台配置，且部署
配置不得设置任何控制台调试开关。控制台模式必须从管理员终端启动；未提权时以退出码 77
拒绝启动，且不会输出监听成功提示。管道创建失败也会直接导致启动失败。

## 共享密钥轮换与升级

在维护窗口重新运行相同版本的签名安装脚本。脚本生成新的随机密钥、更新受保护配置并按
Helper、Guardian、Server 顺序重启服务。不得手工把密钥复制到用户配置、日志、数据库或
HTTP 请求中。

升级前记录当前 Helper 和 Server 发布版本；升级失败时恢复匹配的一对发布目录与受保护
配置，然后先启动 Helper，确认健康后再启动 Server。

## 故障排查

- `privileged-helper-unavailable`：检查 Helper 服务/系统单元、发布目录所有者、pipe ACL、
  sudoers 和密钥配置。
- `elevation-required`：客户端需要为当前 JWT、相同 capability 和精确资源重新进行宿主管理员认证。
- `access-denied` / `resource-not-allowed`：不要放宽 sudoers 或管道 ACL；检查 Helper 策略根、
  allowlisted 服务 ID 和受管实例 ID。
- `manual_host_action_required`：该能力尚未有安全的封闭模型，必须在宿主操作系统中按官方
  文档执行，不得把命令放入 Server 配置。

审计日志只包含 operation ID、operation、资源哈希、结果和问题码；若发现密码、JWT、
共享密钥、文件内容或完整命令行，应视为安全缺陷并立即轮换相关密钥。
