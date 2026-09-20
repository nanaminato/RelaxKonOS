# RelaxKonOS.PrivilegedHelper

这是受限的本地特权边界，不是网络服务，也不是 Guardian Agent。Linux 通过标准输入/输出使用短生命周期 root 工作进程。Windows 的 LocalSystem 服务和开发者控制台宿主使用同一套经过认证的命名管道协议及封闭操作分派器。它绝不接受任意命令或可执行文件。

## 开发

按常规方式构建 Helper：

```bash
dotnet build RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj
```

若要进行真实的 Server → sudo → Helper 集成测试，请将构建输出安装到 root 拥有的开发目录，并创建狭窄的 sudoers 规则：

```bash
sudo deployment/linux/install-relaxkonos-privileged-helper-development.sh "$USER"
```

该脚本会把完整的 Debug 输出复制到 `/usr/local/lib/relaxkonos/privileged-helper-development/RelaxKonOS.PrivilegedHelper`，再只允许开发账户以 root 身份运行该精确的 apphost。选择 Server 的 `http-linux-privileged` 配置，它将 `PrivilegedHelper__HelperPath` 设为该副本、`PrivilegedHelper__SudoPath` 设为 `/usr/bin/sudo`。每次重新构建 Helper 后都要重新运行脚本。

Server 本身仍是非特权进程：sudo 会针对每个结构化请求启动一个 Helper 进程，且 Helper 只允许封闭操作集。绝不可让 sudoers 规则指向开发账户可写的 `bin/Debug` 可执行文件；这会赋予账户等同 root 的控制权。

开发安装默认仅允许 `/etc/relaxkonos` 和 `/var/lib/relaxkonos`。若需调试受保护文件目录，使用
`--file-access whitelist --file-roots deployment/linux/privileged-helper-roots.example`，并从示例中只保留测试所需的绝对目录。
`--file-access full` 会允许 `/` 下所有文件，仅限隔离的可信测试机；不要用它将 `/etc/ssh` 私钥暴露给文件浏览器。

## Windows 开发

日常开发时，直接在 IDE 中使用 `--console` 运行 Helper，不要安装 Windows 服务。在部署目录外创建仅用于开发的配置，使用新的随机 Base64 密钥（至少 32 字节），并且只允许一次性文件根目录：

```json
{
  "pipeName": "relaxkonos-privileged-helper-dev",
  "sharedSecret": "replace-with-a-random-base64-secret-of-at-least-32-bytes",
  "fileAllowedRoots": ["C:\\RelaxKonOS-dev"],
  "allowedServiceIds": ["RelaxKonOSServer-dev"],
  "allowConsoleDebug": true,
  "developerUserSids": ["S-1-5-21-1111111111-2222222222-3333333333-1005"]
}
```

`developerUserSids` 列出除"启动 Helper 的账户"之外还允许连接管道的身份，写 `whoami /user` 得到的 SID 或 `计算机名\账户名` 都可以，无法解析时 Helper 直接启动失败。它是可选项：不写时行为与以前一致；但只要 Server 与提权后的 Helper 不是同一账户，就必须把 Server 的账户写进来。

在 IDE 或终端中启动：

```powershell
dotnet run --project RelaxKonOS.PrivilegedHelper -- --console --config C:\RelaxKonOS-dev\privileged-helper.debug.json
```

为调试 Server 配置相同的管道名和密钥：

```text
PrivilegedHelper__PipeName=relaxkonos-privileged-helper-dev
PrivilegedHelper__SharedSecret=<相同的 Base64 密钥>
```

控制台宿主把管道访问权限授予"启动它的账户"，加上 `developerUserSids` 列出的身份（以及 SYSTEM 和 Administrators），使由该账户启动的 Server 可走生产 IPC 路径。Helper 需要提权，因此它常常以另一个账户运行（例如 Helper 以 Administrator 启动、Server 以普通开发账户运行）；此时必须在 `developerUserSids` 里点名 Server 的账户，否则该连接会在任何认证发生之前被内核拒绝（EPERM），而客户端只会看到"特权助手不可用"，看起来像密钥错误。启动日志会打印实际生效的客户端 SID 列表——连不上时先把 `whoami /user` 与那一行对比。仅在测试确实需要管理员权限的操作时，以提升权限运行 IDE。配置要求 `allowConsoleDebug: true`；生产 `helper.json` 不使用此架构，因而不会意外启用控制台模式（反过来，服务模式也会拒绝含 `developerUserSids` 的部署配置，避免它被静默忽略）。发布前应通过 LocalSystem 服务测试一次，以覆盖 Session 0、用户配置文件、DPAPI、网络凭据和映射驱动器差异。

## Linux 发布安装

先发布项目（Helper 需要与可执行文件并列的 `.runtimeconfig.json`、`.deps.json` 及所有托管程序集）：

```bash
dotnet publish RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj -c Release -r linux-x64 --self-contained false
```

将发布的 apphost 作为 [`install-relaxkonos-services.sh`](../deployment/linux/install-relaxkonos-services.sh) 的第四个参数传入。安装程序会将完整发布目录复制到 root 拥有的位置，并为 Server 服务账户创建狭窄的 sudoers 规则。

## 设置系统时区操作（2026-09-08，实现待平台验收）

新增封闭 `HostTimeRead` / `HostTimeApply`。写入仅接受远程系统列举的 `TimeZoneId` 与 `ExpectedRevision`，禁止混入文件、服务等字段；Helper 再读取当前时区并比较内容摘要，写后读回。Windows 使用系统目录下的 `tzutil.exe`，Linux 使用固定 `/usr/bin/timedatectl`（要求 systemd-timedated 可用）。不存在该后端则明确失败，不以写 JSON 替代 OS 写入。

Server 使用现有 `HostTimeChange` 短期授权，精确目标 `host/time`。预览、幂等与操作状态存于 Server 独立加密 SQLite 日志；Helper 不处理 HTTP 幂等。结果丢失后不能生成新请求盲目重试。

Linux Helper 启动及 Helper 管理子进程现在显式清空继承环境，设置安装控制的固定 PATH；特权程序使用绝对路径。Linux 文件/服务白名单只读 `/etc/relaxkonos/privileged-helper-roots` 与 `/etc/relaxkonos/privileged-services`，不再接受环境变量覆盖。安装必须提供可信运行时，不依赖调用用户的 DOTNET_ROOT/PATH。Windows 服务启动前的运行时环境隔离仍需安装链路审计与实机验证；不能把子进程清理等同于全部启动隔离已验收。

真实 Windows/Ubuntu 写入、策略锁定、外部时区编辑与回滚尚未在指定测试主机验证。详见 SettingsSystem.Goal 执行记录。

### 设置系统主机名操作（已接入，尚未实机验收）

封闭操作 `HostIdentityRead` / `HostIdentityApply` 只接受 `hostName` 与 `ExpectedRevision`，不混合文件、服务、时区或环境字段；其他操作携带 `hostName` 一律拒绝。名称规则与 Server 共用并在 Helper 再校验一次：单一 RFC 952/1123 标签、无控制字符、首尾必须是字母或数字、不得全为数字，长度不超过平台上限（Windows 15，Linux 63）。

Windows 只读取固定注册表位置 `...\Control\ComputerName\ActiveComputerName` 与 `...\ComputerName\ComputerName`（分别为生效与待生效名称），不提供任意注册表路径；写入使用固定 `SetComputerNameEx(ComputerNamePhysicalDnsHostname)` 系统 API，并在改动前后用命名互斥锁串行化。该 API 只暂存新名称，重启后才生效，因此读回确认的是“待生效名称”，Helper 与 Server 都不会把暂存报告为已生效。域加入或安全策略拒绝时返回确定性的 `ResourceNotAllowed`，而不是未知结果。

Linux 读取固定 `/etc/hostname`，写入使用固定 `/usr/bin/hostnamectl set-hostname`，两者缺一即明确返回不支持，不写 `.bashrc`/`.profile`，也不直接覆盖被托管文件。Linux 没有暂存语义，待生效名称与生效名称相同。

Server 使用新增 `HostIdentityChange` 短期授权，精确目标 `host/identity`；预览、幂等、操作与恢复记录存于 Server 独立加密 SQLite 日志，Helper 不处理 HTTP 幂等。

真实 Windows 注册表/重启后生效、域策略拒绝与 Linux `hostnamectl` 写后读回尚未在指定远程测试目标验证；当前证据只是受控 provider 行为测试与编译。若操作结果为未知或需要恢复，管理员必须在主机上手动设置主机名——这是本操作明确声明的手动恢复路径。

### 设置系统环境操作（已接入，尚未实机验收）

封闭操作 `HostEnvironmentRead` / `HostEnvironmentApply` 使用结构化 `environmentTarget` / `environmentChange`；不能混合通用文件、服务或时区字段。其他操作也拒绝环境载荷。Windows 实现固定机器环境键及 `HKEY_USERS/<SID>/Environment`，禁止使用 Helper 的 HKCU；SID 必须是可解析的规范账户 SID，配置单元未加载则返回 NotFound，不创建或挂载任意配置单元。Server 必须先从认证用户映射 SID，客户端不能选择任意 SID。

读回保留 REG_SZ / REG_EXPAND_SZ 原始值；写入前比较完整快照摘要，批量变更逐项写注册表并 Flush、读回及发送 Environment 变化通知。批量注册表写入不承诺事务，Server 必须在写前持久化恢复材料，并将中断/部分失败作为未知结果协调。广播只能通知可到达的会话，不会重写运行进程环境，也不保证其他登录会话或 Windows 服务立即生效。

Linux 仅支持固定 `host/environment/machine` 的 `/etc/environment` provider。Helper 先确认本机 PAM 配置存在未禁用、未重定向 `envfile` 的 `pam_env.so`，否则返回不支持；不会伪造 Linux `HostUser` 存储。读写使用受限无 shell 语法的保真解析、字节 revision、Helper 互斥、写前二次 revision 检查、同目录临时文件、落盘及原子替换、读回验证。保留原文件模式并拒绝链接/目录。其生效语义是“新的 PAM 登录会话”，绝不声称会更新运行中进程、Shell 配置或 systemd 服务。

原始变量仅存在于受认证本地 IPC 的 `hostEnvironment` 结果；禁止直接透传 HTTP、普通审计或诊断。审计只记录资源标识摘要。环境 HTTP/授权协调器已接入；Windows 实机读写/注册表 ACL/服务运行时隔离，以及 Linux 真实 PAM 登录、外部改写与回滚仍待指定远程测试目标验证。
