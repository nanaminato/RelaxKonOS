# RelaxKonOS Privileged Helper 运维指南

本指南适用于以最小权限账户运行的 `RelaxKonOS.Server`。Server 不应以 root、
LocalSystem 或 Administrator 身份运行；所有成功的宿主特权操作必须有 Helper transport
审计记录。

## Linux

使用签名发布包中的 `deployment/linux/install-relaxkonos-services.sh` 安装。安装程序会：

- 创建 `relaxkonos-server` 系统账户；
- 将 Helper 发布目录、sudoers 与策略文件设为 root 所有且 Server 用户不可写；
- 将受管文件根和服务 ID 写入 `/etc/relaxkonos/privileged-helper-roots` 与
  `/etc/relaxkonos/privileged-services`；
- 仅允许 Server 用户以 `sudo -n` 调用无参数 Helper apphost。

安装后检查：

```text
systemctl status relaxkonos-server relaxkonos-guardian
sudo -u relaxkonos-server sudo -n /usr/local/lib/relaxkonos/privileged-helper/<apphost>
```

第二个命令没有 JSON 请求时必须失败；它只能证明 sudoers 指向固定 apphost，不能用于
执行命令。不要向 sudoers 增加通配符、shell 或自定义参数规则。

### 文件访问配置

安装器默认使用 `--file-access restricted`，仅允许：

```text
/etc/relaxkonos
/var/lib/relaxkonos
```

可在常规安装参数之后明确选择下列模式：

```bash
# 默认；适合生产环境。
sudo deployment/linux/install-relaxkonos-services.sh ... --file-access restricted

# 从审查过的白名单文件安装策略。
sudo deployment/linux/install-relaxkonos-services.sh ... \
  --file-access whitelist \
  --file-roots deployment/linux/privileged-helper-roots.example

# 允许所有绝对路径；仅限隔离的、所有 RelaxKonOS 使用者均可信的测试主机。
sudo deployment/linux/install-relaxkonos-services.sh ... --file-access full
```

`whitelist` 文件每行一个绝对目录；空行和以 `#` 开头的注释会被忽略。可从
[`privileged-helper-roots.example`](../../deployment/linux/privileged-helper-roots.example)
复制后删除不需要的条目。一个根目录会授权其所有子路径的读取、写入、删除、移动、复制、上传和创建目录。
因此不要将 `/`、`/etc`、`/home` 或 `/tmp` 写入生产白名单；尤其不要把 `/etc/ssh` 加入通用文件操作，
否则有权限使用文件功能的用户可读取 SSH 主机私钥。

`full` 模式会将 `/` 写入策略文件，等价于所有路径均可经 root Helper 处理。它不是对单个管理员的临时提升，
而是扩大整个 RelaxKonOS 文件接口的能力；生产环境应使用 `restricted` 或经过审查的 `whitelist`。

开发安装脚本也支持相同参数。例如，调试受保护文件流程时可以使用独立夹具：

```bash
sudo deployment/linux/install-relaxkonos-privileged-helper-development.sh "$USER" \
  --file-access whitelist \
  --file-roots deployment/linux/privileged-helper-roots.example
```

该开发脚本会写入同一份系统策略文件；不要在同时运行生产 Server 的主机上将它切换为 `full`。

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
生产权限模型的替代品：控制台配置必须单独创建并显式启用，管道仅允许当前开发用户；其协议、HMAC、
重放保护和操作分发器与服务模式相同。生产安装目录中的 `helper.json` 不能用作控制台配置，且部署
配置不得设置任何控制台调试开关。

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
