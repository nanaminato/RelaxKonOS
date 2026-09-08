# 开发调试指南

常规开发调试时**不要**运行部署脚本，也**不需要**注册 Windows 服务。直接在 Rider 中同时启动 Agent 和 Server 即可。仅当需要测试真实 Linux UFW 变更时，按下节完成一次特权 helper 配置。

---

## 常规桌面系统（Windows 10/11）

### 特权 Helper 日常调试

不要为日常断点调试安装 `RemoteOSPrivilegedHelper` 服务。创建开发专用配置（不可放在
`ProgramData\RelaxKonOS\privileged-helper`，且仅允许测试目录）。例如
`C:\RelaxKonOS-dev\privileged-helper.debug.json`：

```json
{
  "pipeName": "remoteos-privileged-helper-dev",
  "sharedSecret": "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
  "fileAllowedRoots": ["C:\\RelaxKonOS-dev"],
  "allowedServiceIds": ["RelaxKonOSServer-dev"],
  "allowConsoleDebug": true
}
```

示例密钥仅用于展示；请替换为新的、至少 32 字节的随机 Base64 密钥。然后直接从 IDE 启动：

```powershell
dotnet run --project RelaxKonOS.PrivilegedHelper -- --console --config C:\RelaxKonOS-dev\privileged-helper.debug.json
```

配置必须显式包含 `allowConsoleDebug: true`，并配置与 Server 完全相同的
`pipeName`、随机 Base64 `sharedSecret`、`fileAllowedRoots` 与 `allowedServiceIds`。Server 启动
配置中分别设置 `PrivilegedHelper__PipeName` 和 `PrivilegedHelper__SharedSecret`。这样 Server
仍通过正式的命名管道、HMAC、重放保护和固定请求协议调用 Helper，断点则直接命中同一进程中的
执行器。只有需要验证真实管理员行为时才以管理员身份启动 IDE。

`--console` 不能读取并启用生产 `helper.json`：它使用独立配置结构，并要求显式开发开关。发布前
仍必须在隔离 Windows VM 以 LocalSystem 服务模式至少验证一次，以覆盖 Session 0、HKCU、用户
profile、DPAPI、网络凭据、映射盘和环境变量差异。

### 1. 创建调试用户

以**管理员身份**打开 PowerShell，创建用于调试的本地用户：

```powershell
# 以管理员身份运行
New-LocalUser -Name "testuser" -Password (ConvertTo-SecureString "Test@123" -AsPlainText -Force)

# 验证用户是否创建成功
Get-LocalUser | Select-Object Name, Enabled
```
预期输出示例：
```bash
Name               Enabled
----               -------
Administrator      False
betha               True
DefaultAccount     False
Guest              False
testuser            True
WDAGUtilityAccount False
WsiAccount         False
```
### 2. 验证用户密码
创建完成后，可以用以下命令验证密码是否正确：
```powershell
# 使用 PrincipalContext 验证
$ctx = New-Object System.DirectoryServices.AccountManagement.PrincipalContext([System.DirectoryServices.AccountManagement.ContextType]::Machine)
$ctx.ValidateCredentials("testuser", "Test@123")
```
返回 True 表示验证成功。

注意：不要用当前已登录的账户测试 ValidateCredentials，Windows 安全机制会阻止已登录用户验证自己的密码。

### 3. 清理测试用户（可选）
调试完成后可删除测试用户：
```powershell
Remove-LocalUser -Name "testuser"
```

## Linux 特权 Helper 调试

Linux Helper 是按请求启动的 root 进程，不是常驻服务：`RelaxKonOS.Server` 保持以普通用户运行，
仅可通过 `sudo -n` 启动一条 sudoers 规则中**精确指定**的 `RelaxKonOS.PrivilegedHelper`。因此
Server 不会继承 root 身份，UFW、受保护文件和受限服务操作才会在 Helper 内以 root 执行。

要调试真实 Server → sudo → Helper 路径，先构建 Helper，然后由管理员安装其 root-owned 开发副本：

```bash
dotnet build RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj
sudo deployment/linux/install-remoteos-privileged-helper-development.sh "$USER"
```

该脚本复制完整 Debug 输出（包括 PDB）到
`/usr/local/lib/remoteos/privileged-helper-development/`，使其归 `root:root` 且开发账户不可写；
再创建只允许当前 IDE 用户启动该 apphost 的无密码 sudoers 规则。它不会创建或启动 systemd
服务，也不会启动 Server、Guardian 或 Client。每次改动 Helper 后，重新执行构建和该脚本以部署新副本。

该脚本默认安装 `restricted` 文件策略（`/etc/remoteos` 和 `/var/lib/remoteos`）。如需调试由
Helper 访问的受保护文件，请使用单独的无敏感数据夹具，并通过白名单显式授权：

```bash
sudo deployment/linux/install-remoteos-privileged-helper-development.sh "$USER" \
  --file-access whitelist \
  --file-roots deployment/linux/privileged-helper-roots.example
```

可复制示例文件后只保留所需的绝对目录。`--file-access full` 会授权 `/` 下所有路径，仅适合隔离、
所有使用者均可信的测试机；不得用它读取或测试导出 `/etc/ssh` 的主机私钥。完整说明见
[`RelaxKonOS.PrivilegedOperations.Operations.md`](../platform/RelaxKonOS.PrivilegedOperations.Operations.md#文件访问配置)。

然后选择 Server 的 `http-linux-privileged` 启动配置。该配置的
`PrivilegedHelper__HelperPath` 指向上述 root-owned 副本；`PrivilegedHelper__SudoPath` 仍必须为
`/usr/bin/sudo`。普通 `http` 配置不包含此路径，因此适合 UI/API 调试；一旦发起真实 UFW 修改，
它会稳定返回 `firewall.privileged_proxy_required`。

若只需给 Helper 的 Dispatcher 设断点，可直接以 root 执行构建产物并传入一条结构化请求：

```bash
printf '%s' '{"operation":"FirewallUfwStatus","operationId":"11111111-1111-1111-1111-111111111111"}' \
  | sudo ./RelaxKonOS.PrivilegedHelper/bin/Debug/net10.0/RelaxKonOS.PrivilegedHelper
```

不要使用 `sudo dotnet run`，否则构建输出可能被 root 占有。也不要把 sudoers 规则直接指向开发账户
可写的 `bin/Debug` apphost；那等价于授予该账户 root 能力。

## 进程守护
### 1. 配置 Agent 环境变量
新建 RelaxKonOS.Guardian.Agent 的 .NET Project 启动配置，在“环境变量”中逐项加入：
```bash
REMOTEOS_GUARDIAN_PIPE=remoteos-guardian-dev
REMOTEOS_GUARDIAN_SHARED_SECRET=dev-guardian-secret-local-only
REMOTEOS_GUARDIAN_DATA_DIR=E:\riderprojects\RelaxKonOS\.codex-scratch\guardian-dev
```
注意：REMOTEOS_GUARDIAN_DATA_DIR 需要替换为计算机上实际存在的目录。
### 2. 配置 Server 环境变量
在 RelaxKonOS.Server 的启动配置中加入同一对 Pipe/密钥：
```bash
GuardianAgent__PipeName=remoteos-guardian-dev
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
