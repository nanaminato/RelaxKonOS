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
  "allowConsoleDebug": true
}
```

在 IDE 或终端中启动：

```powershell
dotnet run --project RelaxKonOS.PrivilegedHelper -- --console --config C:\RelaxKonOS-dev\privileged-helper.debug.json
```

为调试 Server 配置相同的管道名和密钥：

```text
PrivilegedHelper__PipeName=relaxkonos-privileged-helper-dev
PrivilegedHelper__SharedSecret=<相同的 Base64 密钥>
```

控制台宿主只向交互式开发账户（以及 SYSTEM 和 Administrators）授予管道访问权限，使由该账户启动的 Server 可走生产 IPC 路径。仅在测试确实需要管理员权限的操作时，以提升权限运行 IDE。配置要求 `allowConsoleDebug: true`；生产 `helper.json` 不使用此架构，因而不会意外启用控制台模式。发布前应通过 LocalSystem 服务测试一次，以覆盖 Session 0、用户配置文件、DPAPI、网络凭据和映射驱动器差异。

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

### 设置系统环境操作（接入中，尚未实机验收）

封闭操作 `HostEnvironmentRead` / `HostEnvironmentApply` 使用结构化 `environmentTarget` / `environmentChange`；不能混合通用文件、服务或时区字段。其他操作也拒绝环境载荷。Windows 实现固定机器环境键及 `HKEY_USERS/<SID>/Environment`，禁止使用 Helper 的 HKCU；SID 必须是可解析的规范账户 SID，配置单元未加载则返回 NotFound，不创建或挂载任意配置单元。Server 必须先从认证用户映射 SID，客户端不能选择任意 SID。

读回保留 REG_SZ / REG_EXPAND_SZ 原始值；写入前比较完整快照摘要，批量变更逐项写注册表并 Flush、读回及发送 Environment 变化通知。批量注册表写入不承诺事务，Server 必须在写前持久化恢复材料，并将中断/部分失败作为未知结果协调。广播只能通知可到达的会话，不会重写运行进程环境，也不保证其他登录会话或 Windows 服务立即生效。

原始变量仅存在于受认证本地 IPC 的 `hostEnvironment` 结果；禁止直接透传 HTTP、普通审计或诊断。审计只记录资源标识摘要。环境 HTTP/授权协调器仍未开放，Windows 实机读写/注册表 ACL/服务运行时隔离均待指定远程测试目标验证。Linux 目前仅完成受限文档核心，封闭操作的 Linux provider 仍待接入，不能据此认定 Linux 不适用或阶段完成。
