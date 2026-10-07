# 开发调试指南

## 依赖版本与构建

当前使用 .NET 10 SDK，Avalonia 核心包为 12.1.3，独立发行的 DataGrid 保持 12.1.2，Microsoft.OpenApi 为 2.12.2。NuGet 版本集中在根目录 `Directory.Packages.props`，子项目不重复定义版本。

执行 `dotnet list RelaxKonOS.sln package --outdated` 检查更新，修改兼容版本后运行 `dotnet restore RelaxKonOS.sln` 和 `dotnet build RelaxKonOS.sln -c Release -m:1`。完整重建可追加 `-t:Rebuild`。

ASP.NET Core OpenAPI 10.0.x 要求 Microsoft.OpenApi 小于 3；ImageSharp 暂留 3.1.12，跨主版本更新必须核对 API 与第三方许可。官网前端采用 Angular 22.2.1 与 TypeScript 6.0.3，具体环境、锁文件维护和调试步骤见[官网开发教程](https://relaxkon.com/docs/zh-CN/latest/getting-started/development)。

常规开发调试时**不要**运行部署脚本，也**不需要**注册 Windows 服务。直接在 Rider 中同时启动 Agent 和 Server 即可。需要受保护文件、Windows 受管 Nginx/FRP 或真实 Linux UFW 操作时，按对应章节配置特权 Helper。

> 日常调试时的「有效用户执行」（文件浏览器、终端、Git、媒体、上传）**不需要安装任何服务**：
> 开发 profile 已把 `PrivilegedHelper:UserExecutionBackend` 设为 `local-identity`，以你自己的账户在
> Server 进程内执行。完整的执行后端语义、硬守卫与限制见
> [RelaxKonOS.LocalDebugging.md](RelaxKonOS.LocalDebugging.md)；下文是特权（提权）通道的调试方式。

---

## 常规桌面系统（Windows 10/11）

### Windows 10/11：先选择互斥的开发模型

Windows 10/11 的 Docker Desktop 是交互用户拥有的开发运行时，而不是可由 RelaxKonOS 任意账户共享的
系统 Docker 服务。`local-identity` 也只允许 Server 以自己的 SID 执行普通文件、终端和 Git 操作。
因此不要把“Docker Desktop 所有者”、“Server 进程账户”和“登录的宿主账户”拆成不同用户，再试图用
`developerUserSids` 或管理员 Helper 把它们重新拼起来。

| 模型 | Docker Desktop 所有者 | Server / 登录账户 | 支持范围 |
| --- | --- | --- | --- |
| **桌面 Docker 开发** | 同一交互用户 `U` | 同一用户 `U` | Docker、普通文件、终端、Git 与可选的管理员 Helper 调试 |
| **跨身份 Helper 测试** | 不使用 Docker Desktop | 可验证的本地 `testuser` | 仅命名管道 ACL、普通文件与 Helper 协议排障 |
| **多用户 Windows 主机** | 不使用 Docker Desktop | 服务账户 + LocalSystem Helper | 仅 Windows Server 隔离验收；当前不作为开发桌面的已支持路径 |

Docker Desktop 的容器和镜像不能在 WSL 2 后端跨 Windows 用户共享，且其非特权命名管道默认只向启动
Docker Desktop 的用户、Administrators 和 LocalSystem 开放。详见 Docker 的
[Windows 权限要求](https://docs.docker.com/desktop/setup/install/windows-permission-requirements/) 和
[安装说明](https://docs.docker.com/desktop/setup/install/windows-install/)。

### 1. 桌面 Docker 开发：一个交互操作员

选择实际启动 Docker Desktop 的 Windows 用户 `U`。以 `U` 启动 Docker Desktop、RelaxKonOS Server 和
RelaxKonOS Client，并以 `U` 的宿主账户登录。开发 profile 使用
`PrivilegedHelper:UserExecutionBackend=local-identity`，因此这个 SID 相等关系是承重安全边界，不是可选
优化。以 `betha` 启动的 Docker Desktop 必须由 `betha` 运行的 Server 管理；不要改为用 `testuser` 的
`runas` Server 管理它。

当前 Server 的 Windows 密码登录使用 `LogonUser`。Windows Hello PIN 不是账户密码；Microsoft Account
需要使用真实密码和可解析的规范账户名。无密码地初始化用于分发配对码的本机时，从 `U` 的 PowerShell 或 IDE
使用普通 `http` profile 启动 Server，并让同一台机器上的 Client 连接 `http://localhost:5090`：

```powershell
dotnet run --project RelaxKonOS.Server --launch-profile http
```

在登录窗口展开“使用已配对设备密钥”，选择“设置或恢复此 Windows 设备”。该 bootstrap 仅接受本机 loopback
的 Negotiate 请求，要求 Windows 10/11 管理员账户的 SID 与 Server 进程 SID 相同；它会登记 owner-device
私钥并完成登录。后续在相同的 `http://localhost:5090` 使用“使用设备密钥登录”，并可在账户安全设置中创建
一次性配对码。`0.0.0.0` 仅是 Server 的监听地址，不能作为 Client 连接地址。局域网设备必须使用实际的 LAN
地址或 DNS 名称；真实跨设备部署应使用 HTTPS。

### 2. 配置并启动特权 Helper（需要受保护操作时）

本地开发的完整模型是：**普通权限启动 `http` Server，以管理员身份启动一次控制台 Helper，之后所有日常授权留在客户端**。Helper 持续监听本机命名管道，文件提权和 Windows 受管 Nginx/FRP 操作不再次弹宿主 UAC，也不需要用户回到 Windows 点击确认。无需安装服务；只有重启管理员控制台 Helper 时重新使用提升的终端。

`http` profile 与 Helper 必须使用相同的管道名和 sharedSecret，Server 登录账户必须与运行 Server 的 SID 相同。Helper 不同账户启动时，把 Server SID 写入 `developerUserSids`。管理员终端启动不代表可以省略 `--console --config` 或放宽文件根策略。Nginx 与 FRP 的软件包入口另由 `runtimeArchiveRoots` 管理；不需要为了安装软件把普通文件白名单改成整盘。

不要为日常断点调试安装 `RelaxKonOSPrivilegedHelper` 服务。创建开发专用配置（不可放在
`ProgramData\RelaxKonOS\privileged-helper`，且仅允许测试目录）。例如
`C:\RelaxKonOS-dev\privileged-helper.debug.json`：

```json
{
  "pipeName": "relaxkonos-privileged-helper-dev",
  "sharedSecret": "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
  "fileAllowedRoots": ["C:\\RelaxKonOS-dev"],
  "allowedServiceIds": ["RelaxKonOSServer-dev"],
  "allowConsoleDebug": true,
  "runtimeArchiveRoots": [
    "E:\\riderprojects\\RelaxKon\\RelaxKonOS\\RelaxKonOS.Server\\data\\runtimes\\frp",
    "E:\\riderprojects\\RelaxKon\\RelaxKonOS\\RelaxKonOS.Server\\data\\webserver-packages"
  ],
  "developerUserSids": [
    "S-1-5-21-2333115902-1181188794-1498531570-1005",
    "S-1-5-21-518898542-3752080965-3168045265-1005"]
}
```

示例密钥仅用于展示；请替换为新的、至少 32 字节的随机 Base64 密钥。`developerUserSids` 填入
`whoami /user` 输出的 SID（也接受 `计算机名\账户名`），它列出的身份与"启动 Helper 的账户"一样
可以连接管道。桌面 Docker 开发时，列表必须包含运行 Server 的操作员 `U`（若 Helper 正是由 `U` 提升
启动，该 SID 已自动获准）。
Helper 必须提权运行，因而常常与 Server 不是同一账户——**只要两者不同就必须显式列出 Server
账户**，否则该连接会在认证之前被内核拒绝（EPERM），客户端只会显示"特权助手不可用"，与密钥错误、
配置缺失无法区分。该项可省略（行为与以前一致）；条目无法解析时 Helper 直接启动失败，不会静默丢弃。
启动时会打印实际生效的客户端 SID 列表，排障时先与 `whoami /user` 对照。

然后从以管理员身份运行的 PowerShell 或 IDE 启动：

```powershell
dotnet run --project RelaxKonOS.PrivilegedHelper -- --console --config C:\RelaxKonOS-dev\privileged-helper.debug.json
```

将 `runtimeArchiveRoots` 改为实际 Server 内容目录下的两个包暂存目录；它们不能与 Helper 的受管程序目录重叠。未指定时 Nginx 使用 `%ProgramData%\RelaxKonOS\webserver\nginx`，FRP 的受保护副本使用 `%ProgramData%\RelaxKonOS\privileged-runtimes`。

定制 Nginx 目录时，Server 的 `NginxManaged:InstallationRoot` 和 Helper 的 `nginxRoot` 必须相同且为明确绝对路径。Helper 会保护这些目录及 Nginx 的父目录，Server 只能通过专用运行时接口修改；这些路径应专用，不能选成源码、用户工作目录、Server 数据或包暂存目录。

配置必须显式包含 `allowConsoleDebug: true`，并配置与 Server 完全相同的
`pipeName`、随机 Base64 `sharedSecret`、`fileAllowedRoots` 与 `allowedServiceIds`。Server 启动
配置中分别设置 `PrivilegedHelper__PipeName` 和 `PrivilegedHelper__SharedSecret`。这样 Server
仍通过正式的命名管道、HMAC、重放保护和固定请求协议调用 Helper，断点则直接命中同一进程中的
执行器。Helper 控制台模式必须具有管理员权限；普通终端启动会明确报错并以退出码 77 退出，
不会自动触发 UAC 提权。Server 和客户端仍可使用普通权限运行。只有管道创建成功后才会输出
`is listening`；配置或管道创建失败会报错退出。

### 3. 跨身份 Helper 测试（不含 Docker）

只有需要验证“提升的 Helper 与不同 Server SID 的管道 ACL”时才创建 `testuser`。此流程**不能**验证或
管理由 `betha`（或其他用户）启动的 Docker Desktop；不要把它与上一节混用。

```powershell
# 以管理员身份运行；仅创建一次。
New-LocalUser -Name "testuser" -Password (ConvertTo-SecureString "Test@123" -AsPlainText -Force)
(Get-LocalUser -Name "testuser").SID.Value
```

将该 SID 写入 `privileged-helper.debug.json` 的 `developerUserSids`，再从普通 PowerShell 以同一用户启动
Server。`http` profile 显式选择 `local-identity`：

```powershell
$machine = $env:COMPUTERNAME
runas /user:"$machine\testuser" 'cmd /c "cd /d D:\RelaxKon\RelaxKonOS && dotnet run --project RelaxKonOS.Server --launch-profile http"'
```

在客户端以 `testuser` 登录。`testuser` 需要对工作树拥有写入权限，因为 `dotnet run` 会更新项目的 `bin`
和 `obj` 目录。不要直接运行 `RelaxKonOS.Server.exe`，因为它不会读取 `launchSettings.json` 中的开发 profile
和命名管道配置。测试结束后可用提升 PowerShell 删除该账户：

```powershell
Remove-LocalUser -Name "testuser"
```

`--console` 不能读取并启用生产 `helper.json`：它使用独立配置结构，并要求显式开发开关。发布前
仍必须在隔离 Windows VM 以 LocalSystem 服务模式至少验证一次，以覆盖 Session 0、HKCU、用户
profile、DPAPI、网络凭据、映射盘和环境变量差异。

Windows 有效用户文件执行另用 `<pipeName>-user` 管道。代码只接受本机账户，并由 LocalSystem Helper
通过一次性 S4U token impersonate；管理员控制台模式不是 LocalSystem，不能执行该路径。Helper 侧的
`enableWindowsUserExecution` 默认为 `false`；Server 侧则由 `PrivilegedHelper:UserExecutionBackend`
（`helper` / `local-identity` / `disabled`）选择执行后端，默认 `helper`，安装器在省略
`-EnableWindowsUserExecution` 时写入 `disabled`。只有在隔离 Windows Server 中准备两个普通本地用户并执行
SID/NTFS ACL、并发、token 释放与服务重启矩阵时，才可临时把 Helper 侧开关打开并把 Server 侧改为
`helper`；Windows 10/11 Docker Desktop 不属于该验收路径。`local-identity` 只是开发机后端（Production
下启动期拒绝），域账户、Git 与 Terminal 仍不在此次测试范围内。

## Linux 特权 Helper 调试

Linux Helper 是按请求启动的 root 进程，不是常驻服务：`RelaxKonOS.Server` 保持以普通用户运行，
仅可通过 `sudo -n` 启动一条 sudoers 规则中**精确指定**的 `RelaxKonOS.PrivilegedHelper`。因此
Server 不会继承 root 身份，UFW、受保护文件和受限服务操作才会在 Helper 内以 root 执行。

要调试真实 Server → sudo → Helper 路径，先构建 Helper，然后由管理员安装其 root-owned 开发副本：

```bash
dotnet build RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj

sudo deployment/linux/install-relaxkonos-privileged-helper-development.sh "$USER" \
  --file-access whitelist \
  --file-roots deployment/linux/privileged-helper-roots.example \
  --administrator-file-access whitelist \
  --administrator-file-roots deployment/linux/privileged-helper-administrators.example
```

该脚本复制完整 Debug 输出（包括 PDB）到
`/usr/local/lib/relaxkonos/privileged-helper-development/`，使其归 `root:root` 且开发账户不可写；
再原子安装 Helper 所需的受管 `/etc/pam.d/relaxkonos`（仅 `common-auth` 与
`common-account`）并创建只允许当前 IDE 用户启动该 apphost 的无密码 sudoers 规则。若同名 PAM
文件不是 RelaxKonOS 受管文件，脚本会拒绝覆盖。它不会创建或启动 systemd 服务，也不会启动 Server、Guardian
或 Client。每次改动 Helper 后，重新执行构建和该脚本以部署新副本。

该脚本默认安装三份独立的 `restricted` 文件策略（手动 grant、管理员、root；root 额外允许 `/root`）。如需调试由
Helper 访问的受保护文件，请使用单独的无敏感数据夹具，并通过白名单显式授权：

```bash
sudo deployment/linux/install-relaxkonos-privileged-helper-development.sh "$USER" \
  --file-access whitelist \
  --file-roots deployment/linux/privileged-helper-roots.example
```

可复制示例文件后只保留所需的绝对目录。管理员和 root 范围分别使用
`--administrator-file-access`/`--administrator-file-roots` 与 `--root-file-access`/`--root-file-roots`。
任何来源的 `full` 都会授权 `/` 下所有路径；不得用它读取或测试导出 `/etc/ssh` 的主机私钥。完整说明见
[`RelaxKonOS.PrivilegedOperations.Operations.md`](../platform/RelaxKonOS.PrivilegedOperations.Operations.md#文件访问配置)。

然后选择 Server 的 `http-linux-privileged` 启动配置。该配置的
`PrivilegedHelper__HelperPath` 指向上述 root-owned 副本；`PrivilegedHelper__SudoPath` 仍必须为
`/usr/bin/sudo`。普通 `http` 配置不包含此路径，因此适合 UI/API 调试；一旦发起真实 UFW 修改，
它会稳定返回 `firewall.privileged_proxy_required`。

### `http-linux-privileged` 登录返回 503

`http-linux-privileged` 的系统账户登录也走同一条 `Server → sudo → Helper → PAM` 链路。仅在 Rider
中切换到该启动配置不会安装或更新 Helper。因此若客户端登录提示
`authentication-unavailable`（HTTP 503），首先重新构建并重新执行上面的开发安装命令；这会将当前
Debug Helper 快照安装到它配置的路径，并同时恢复受管 PAM 文件和精确的 sudoers 规则。

每次修改 `RelaxKonOS.PrivilegedHelper` 后都必须重复这两个步骤；仅重新启动 Rider 或 Server 不会更新
root-owned 副本。若重新安装后仍失败，查看 Server 控制台中同一次请求的
`Privileged Helper operation completed`：`HelperUnavailable` 表示 Helper 路径或 sudo 授权未就绪，
`InternalError` 则表示 Helper 已运行但专用 PAM service 或其依赖不可用。不要将 System Mode 改为
`in-process` 作为绕过方式。

未显式配置 `NginxManaged:InstallationRoot` 时，Development 下 Linux 使用
`$HOME/.local/share/RelaxKonOS/debug/webserver/nginx`，Windows 使用 `%ProgramData%\RelaxKonOS\webserver\nginx`。
Linux 目录只保存 RelaxKonOS 的受管标记；系统包 Nginx 仍由 `nginx.service` 使用
`/etc/nginx/nginx.conf`。Windows 目录包含由提升 Helper 保护和执行的受管程序及配置。

若只需给 Helper 的 Dispatcher 设断点，可直接以 root 执行构建产物并传入一条结构化请求：

```bash
printf '%s' '{"operation":"FirewallUfwStatus","operationId":"11111111-1111-1111-1111-111111111111"}' \
  | sudo ./RelaxKonOS.PrivilegedHelper/bin/Debug/net10.0/RelaxKonOS.PrivilegedHelper
```

不要使用 `sudo dotnet run`，否则构建输出可能被 root 占有。也不要把 sudoers 规则直接指向开发账户
可写的 `bin/Debug` apphost；那等价于授予该账户 root 能力。

### Linux 当前账号 PAM 登录（不构建 Helper）

`http-linux-user` 启动配置设置 `Identity__LinuxPamTransport=in-process` 与
`Identity__LinuxPamService=login`，使以当前开发用户运行的 Server 直接调用宿主已有的 `login` PAM service。
它与无 sudo User Mode 使用相同的认证 transport，不需要构建或安装 PrivilegedHelper。

这不是由 `Development` 环境隐式放开的回退：`in-process` 与 `helper` 是明确配置。systemd System Mode 服务
应继续使用默认 `helper` transport 和专用 `relaxkonos` PAM service；User Mode 才设置 `in-process`，且绝不
把该设置与特权 Helper 或 sudoers 授权混为一体。

### Linux 系统账户认证手工验证

安装或重新安装后，确认专用 PAM service 而不是 `login` stack 在工作：

```bash
sudo pamtester relaxkonos nanami authenticate
sudo systemctl show relaxkonos-server.service -p User -p Group
sudo -u nobody sudo -n -l /usr/local/lib/relaxkonos/privileged-helper/RelaxKonOS.PrivilegedHelper
```

第一条应提示 `nanami` 的系统密码并成功；第二条应继续显示 `relaxkonos-server`，而不是 root。第三条会因
没有 sudoers 授权而失败，验证未授权本地账户不能调用 Helper。随后从 Client 分别验证正确密码、错误密码、
未知用户和 alias 登录；错误密码应是 `invalid-credential`，Helper/Server 日志不得出现密码。若要检查 PAM
文件，只读取其受管内容：`sudo sed -n '1,20p' /etc/pam.d/relaxkonos`。

## 进程守护
### 1. 配置 Agent 环境变量
新建 RelaxKonOS.Guardian.Agent 的 .NET Project 启动配置，在“环境变量”中逐项加入：
```bash
RELAXKONOS_GUARDIAN_PIPE=relaxkonos-guardian-dev
RELAXKONOS_GUARDIAN_SHARED_SECRET=dev-guardian-secret-local-only
RELAXKONOS_GUARDIAN_DATA_DIR=E:\riderprojects\RelaxKonOS\.codex-scratch\guardian-dev
```
注意：RELAXKONOS_GUARDIAN_DATA_DIR 需要替换为计算机上实际存在的目录。
### 2. 配置 Server 环境变量
在 RelaxKonOS.Server 的启动配置中加入同一对 Pipe/密钥：
```bash
GuardianAgent__PipeName=relaxkonos-guardian-dev
GuardianAgent__SharedSecret=dev-guardian-secret-local-only
```
注意在 Rider 中每个环境变量单独添加。
### 3.启动顺序：
按以下顺序启动：
1. RelaxKonOS.Guardian.Agent
2. RelaxKonOS.Server
3. RelaxKonOS.Client（连接到调试 Server）

### 4. 验证守护程序状态
   重启 Server 后，守护程序状态显示说明：

| 状态 | 含义 |
|------|------|
| 可用 | ✅ Agent 运行正常 |
| guardian.agent_unavailable | Agent 未运行，但密钥配置正确 |
| guardian.agent_not_configured | Server 的两个环境变量未生效（检查配置） |

### 5. 测试工作负载
   使用以下不会长期占用业务端口的 workload 做首次验证：

| 字段     | 值 |
|--------|-----|
| 工作负载名称 | Development ping |
| 可执行文件  | C:\Windows\System32\PING.EXE |
| 工作目录   | C:\Windows\System32 |
| 参数     | 127.0.0.1<br>-t |

保存后应出现在左侧列表。点击启动，再点击"查看日志"可看到输出；停止或删除可验证完整生命周期。

**注意**：调试时不要勾选"宿主机重启后自动启动"。

### 6. 测试 .NET/Java 应用
   .NET 应用配置：

| 字段 | 值 |
|------|-----|
| 可执行文件 | C:\Program Files\dotnet\dotnet.exe |
| 工作目录 | 应用发布目录（绝对路径） |
| 参数 | MyApp.dll |

Java 应用配置：

| 字段 | 值 |
|------|-----|
| 可执行文件 | ...\bin\java.exe |
| 工作目录 | 应用目录（绝对路径） |
| 参数 | -jar<br>app.jar |

通用要求：

- 工作目录必须是存在的绝对路径
- 可执行文件可填写存在的绝对路径，或填写 Guardian Agent PATH 中的程序名（例如 dotnet）
- 保存时会解析并持久化为绝对路径
- 实际可访问性由目标 RunAs 账户的 OS 权限决定
