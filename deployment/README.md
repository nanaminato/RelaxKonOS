# RelaxKonOS 一键服务端安装

发布制品分为 `client`、`server` 与 `user-server` 三种包。`server` 包是 System Mode，包含已 `dotnet publish` 的 Server、Guardian Agent、权限助手和平台部署引擎；`user-server` 是无 sudo 的 Linux User Mode，包含 Server、同 UID Guardian 和用户 launcher，但不包含权限助手、sudoers 或系统服务安装器；`client` 包只包含桌面 Client。

引导安装器会先把三个组件的完整 publish 输出复制到持久安装目录（Windows 默认 `C:\Program Files\RelaxKonOS`，Linux 默认 `/opt/relaxkonos`）；服务绝不会指向临时下载目录或离线介质。Linux System Mode 把每个版本及其配套部署引擎发布到独立的 `versions/<版本>` 目录，完整复制成功后才停止服务并用 `current` 符号链接原子切换，因此升级不会把新版覆盖到在用的版本目录；失败回滚也始终使用目标版本自己的部署引擎。

## 发布包布局

```text
manifest.json
manifest.sha256
payload/windows/{server,guardian,privileged-helper}/...
payload/linux/{server,guardian,privileged-helper}/...
deployment/windows/Install-RelaxKonOSServices.ps1
deployment/linux/install-relaxkonos-services.sh
```

`manifest.json` 和下载描述文件使用 `schemaVersion: 1`，并明确标记 `packageKind`（`client`、`server` 或 `user-server`）；示例见 [release-manifest.example.json](./release-manifest.example.json)。清单列出每个包内文件的长度和 SHA-256；`manifest.sha256` 列出包内除 manifest 文件外的全部文件，Linux System Mode 安装器与 User Mode launcher 都会重算并精确比对该 inventory。线上安装由发布页同时提供 ZIP 的 SHA-256，安装器在解压前检查它。服务器中心按来源处理：官网包在服务器下载并核对官方 ZIP 摘要及逐文件清单；用户选择的本地或服务器 ZIP 不要求官方摘要、不计算逐文件摘要，仍检查包类型、RID、必要文件、版本和安全解压布局。当前发布包不要求签名密钥，也不生成签名伴随文件。

发布前可用仓库内的检查工具复核服务器 ZIP：

```powershell
dotnet run --project ./deployment/packaging/RelaxKonOS.ReleaseVerifier -- verify ./artifacts/RelaxKonOS-0.1.0-win-x64-server.zip server win-x64
```

服务器中心部署脚本随客户端内置：桌面使用嵌入资源，Android 使用 APK assets。无需放置外部 `launcher/`、设置发布目录环境变量或准备目标 RID 的 `release-verifier`。Windows 使用系统 PowerShell/.NET，Linux 使用 Bash 和 Python 3 完成严格 JSON 解析及安全 ZIP 解压。

Linux 系统模式在未上传新发布包的维护操作中，从 `/opt/relaxkonos/current/deployment/bootstrap/` 读取当前版本的安装和卸载引擎。桌面端必须同时核对操作终态回执及随后读取的宿主状态：失败回执显示实际失败原因；卸载只有在操作成功且状态确认 `installed=false` 时显示成功，保留数据不等于仍然安装。

普通 SSH 账户通过 sudo 维护系统安装时，已安装引擎的文件和可执行权限检查也必须使用同一次经验证的 sudo 身份。部署目录仅 root 可遍历时，不能用普通 SSH 用户的 `test -x` 判断引擎缺失；无 sudo 授权或脚本实际缺失时仍须拒绝操作。

SSH 私有解压目录使用 `0700/0600` 权限；发布到系统模式安装目录后，必须让服务账户可遍历程序目录并读取运行库和程序集，同时保持程序文件仅 root 可写。程序的 `server/data` 必须链接到持久化的受管服务器数据目录。修复部署脚本后必须重新制作 Server ZIP，因为安装实际执行的是 ZIP 内的版本化引擎，仅更新桌面客户端不会更新旧 ZIP 中的安装器。安装器等待 HTTP/HTTPS 健康端点最多 60 秒，不能用 systemd 的 active 状态替代健康检查成功。

安装来源使用当前请求契约：`officialStable` 不上传 ZIP，由服务器读取官网描述符、下载并自动校验；`localBundle` 上传用户选择的 ZIP，`stagedPackageName` 为必填；`remoteBundle` 通过 `remotePackagePath` 引用服务器绝对路径，直接读取，不下载回客户端或重新上传。用户文件无需 `packageDigest`。所有来源仍拒绝路径穿越、重复 ZIP 路径、符号链接、不匹配的架构或包类型，并只将包解压到本次操作的私有目录。Linux 引擎对用户包使用 `--skip-file-checks`，仅省略摘要比对；官网包保留摘要检查。

打包脚本仍导出 `artifacts/launcher/` 中的两种脚本，供维护者手动使用，但客户端运行不依赖这个目录。仓库的 `RelaxKonOS.ReleaseVerifier` 仍可用于发布前检查，不再发布或上传它作为安装依赖。

操作记录和独占锁保存在暂存目录之外，因而不同客户端和断线后的新暂存目录仍会读取同一回执：Windows 已提升管理员操作为 `%ProgramData%\RelaxKonOS-Deployment`（未提升账号的只读探测使用 `%LOCALAPPDATA%\RelaxKonOS-Deployment`），Linux System Mode 为 `/var/lib/relaxkonos-deployment`，Linux User Mode 为 `${XDG_STATE_HOME:-$HOME/.local/state}/relaxkonos-deployment`。卸载数据时也保留该操作日志，以便查询卸载回执。

维护者用下列命令制作一个自包含的单平台发布包（会同时生成 ZIP、`.sha256`、清单与下载描述符）：

```powershell
./deployment/packaging/New-RelaxKonOSRelease.ps1 -Version 0.1.0 -Runtime win-x64
```

Linux System Mode 则运行：

```bash
./deployment/packaging/package-relaxkonos.sh 0.1.0 linux-x64 Release
```

Linux 打包宿主还需提供 `zip`、`sha256sum` 与 `stat`。

客户端的便携 ZIP 与 Windows MSIX 打包和升级流程见 [ClientDistribution.md](./ClientDistribution.md)。Linux 客户端通过便携 ZIP 分发；不提供 Debian/Ubuntu APT 仓库或 `.deb` 包。

两者都会分别产出 Client 与 Server ZIP、`.sha256` 和同名 `.json` 下载描述文件。用户态 Linux 包可单独生成：

```bash
./deployment/packaging/package-relaxkonos.sh 0.1.0 linux-x64 Release ./artifacts user-server
```

System Mode 安装时请选择 `*-server.zip` 或对应的 Server 发布目录；User Mode 请选择 `*-user-server.zip` 解压后的发布目录。

## 官方在线来源

安装器默认从 `https://downloads.relaxkon.com/relaxkonos/stable/latest/{rid}.json` 读取当前稳定版，其中 `{rid}` 是 `win-x64`、`win-arm64`、`linux-x64` 或 `linux-arm64`。该描述文件包含 ZIP 的 HTTPS 地址和 SHA-256；将通过验证的版本描述文件同步为 `latest/{rid}.json`，即可完成稳定版切换，无需修改安装器。Linux User Mode 的服务器中心安装使用独立的 `latest/user-server/{rid}.json`，其 `packageKind` 必须为 `user-server`；发布时需同时部署这份描述符。

## 反向代理与分块上传

大文件上传走**分块会话**（`POST`/`PATCH`/`GET`/`DELETE /api/v1.0/files/uploads...`），设计与实现规格见 [RelaxKonOS.FileUpload.Design.md](../docs/architecture/RelaxKonOS.FileUpload.Design.md)。每个请求只承载一个分片，所以对中间层反而友好；但**必须显式放宽下面这几个值**，否则代理的默认上限会以新的形式替代服务端原本的上限：

```nginx
location /api/v1.0/files/uploads/ {
    client_max_body_size 12m;        # ≥ 服务端下发的 chunkSize（普通 8 MiB / 提权 6 MiB，提权路径还会被 base64 放大 4/3）+ 信封余量
    client_body_timeout 120s;        # 单分片内两次写入之间的间隔，不是整个文件的耗时
    proxy_request_buffering off;     # 关键：不要先把整个分片缓冲下来再转发
    proxy_buffering off;
    proxy_read_timeout 120s;         # 与客户端的 60 秒分片停滞看门狗同一量级
    proxy_send_timeout 120s;
}
```

- 只对 `files/uploads` 前缀放宽，其余 API 保持既有严格值。
- 仓库自带的 Nginx 管理器对 `DisableBuffering` 路由已输出 `proxy_request_buffering off`（`RelaxKonOS.Server/WebServer/NginxWebServerManager.cs`），所以"关闭缓冲"是既有能力，不是为上传新加的概念。
- 用 IIS/ARR、Caddy、Traefik 或云负载均衡时按同一组语义配置。**任何把单请求上限设得低于一个分片（8 MiB）的中间层都必须检查**：它的失败模式不是"传不了大文件"，而是"每个分片都被拒，上传永远停在第一片"。
- 单发快路径（`POST /api/v1.0/files/upload`）在服务端显式声明了 16 MiB 上限，客户端只在 ≤ 4 MiB 时才走它，因此上面的 `12m` 不影响小文件。
- **User Mode 不需要这组配置**：Server 只绑定 `127.0.0.1`，控制套接字不经代理；远程连接走 SSH 本地转发时转发的是原始 TCP，也不存在代理缓冲。

## Windows

管理员 PowerShell 中运行：

```powershell
& .\deployment\bootstrap\Install-RelaxKonOS.ps1 -BundlePath 'D:\RelaxKonOS-release'
```

直接运行安装器即可获取官方稳定版。也可以传入 `-ReleaseUri` 与必须的 `-ReleaseSha256` 安装指定 ZIP，或用 `-BundlePath` 进行离线安装。`-NonInteractive` 会同样使用官方稳定版；默认仅监听 `127.0.0.1:5000`、只允许权限助手访问 RelaxKonOS 数据目录。

安装器会自动准备安全审计：生成安装实例编号和审计完整性密钥、创建受保护的审计数据库与运行日志目录，并在修复或升级时保留它们。无需输入或保存任何审计密钥；只有需要接入企业日志平台或改变默认保留策略时才需要高级部署配置。

离线介质可直接是发布目录或 ZIP 文件，例如：`Install-RelaxKonOS.ps1 -BundlePath E:\media\RelaxKonOS-0.1.0-win-x64.zip`。安装前会校验 Windows 架构与包内 `runtime` 是否相符。

## Linux

System Mode（需要 root）使用显式模式：

服务器中心可由普通 SSH 管理账户显式选择 Linux 系统模式：安装前验证 sudo 密码和权限，部署引擎通过 sudo 执行，系统状态文件通过 sudo 核验。向导中的 sudo 密码留空时使用 SSH 登录密码；密码只通过 SSH 标准输入传递，不写入命令、请求或日志。无需 root SSH 登录或配置免密 sudo。暂存与操作日志保留在原 SSH 账户下，安装器不会改变这些目录的所有者。

桌面操作记录选中后显示操作 ID、阶段、问题码和完整摘要；“从宿主刷新所选操作”同时通过固定的 Linux `--diagnostics OPERATION_ID` 或 Windows `-DiagnosticsOperationId` 读取至多 64 KiB 部署日志。日志仅在当前界面显示，不写入本地索引，并遮盖密码、secret、token 和 authorization 字段。安装向导打开时，工作区隐藏重复进度条，由向导显示“安装中”和一个进度条。

Linux 安装结束（成功或失败）会清理当前操作的解压目录、本次上传的 `server.zip`、官方包下载文件和证书暂存，保留 SSH 用户的回执与日志；服务器来源的原始 ZIP 不删除。解压前按 ZIP 未压缩大小加 64 MiB 余量检查暂存分区容量；用户配额仍以实际写入返回的 `EDQUOT` 为准，并报告 `disk_quota_exceeded`，分区耗尽报告 `disk_space_insufficient`。旧版客户端遗留的 `/tmp/relaxkonos-deploy.*` 目录需要检查后清理，不能把配额失败归类为包清单损坏。

```bash
sudo ./deployment/bootstrap/install-relaxkonos.sh --mode system --bundle /mnt/RelaxKonOS-release
```

直接运行安装器即可获取官方稳定版。也可传入 `--release-uri URL --release-sha256 SHA256` 安装指定 ZIP，或用 `--bundle` 进行离线安装。Linux 权限助手不是常驻服务：Server 账户只能通过固定的 sudo 规则执行 root-owned Helper；Server 和 Guardian 则是 systemd 服务。

System Mode 安装会自动创建 `/var/log/relaxkonos/runtime` 和审计数据库，并在 root-only systemd 配置中生成、保存审计完整性密钥。管理员不需要手工设置 `InstanceId`、路径或 HMAC 密钥；重装和升级会保留已有值。

离线介质可直接是发布目录或 ZIP，例如：`sudo ./install-relaxkonos.sh --bundle /media/usb/RelaxKonOS-0.1.0-linux-x64.zip`。安装器会验证包的架构、systemd、`sudo`/`visudo`/`openssl`，并仅默认接受 Debian 12、Ubuntu 22.04/24.04/26.04；其他系统必须明确传入 `--allow-unsupported-system`。

局域网模式仅将 Server 绑定到 `0.0.0.0`，不会自动打开防火墙。公网部署请选择反向代理模式（默认本机监听），并由反向代理终结 HTTPS。

Docker 管理默认关闭，因为 Docker socket 等同高权限主机控制。只有需要 Docker Manager 时，才在 System Mode 命令末尾明确追加 `--docker-access`；安装器会授权 Server 服务账户并重启 Server。

### Linux User Mode（无 sudo）

先解压 `*-user-server.zip`，然后以目标 Linux 账号运行：

```bash
./deployment/user/install-relaxkonos.sh --mode user --bundle /path/to/RelaxKonOS-0.1.0-linux-x64-user-server
"${XDG_DATA_HOME:-$HOME/.local/share}/relaxkonos/bin/relaxkon" start
"${XDG_DATA_HOME:-$HOME/.local/share}/relaxkonos/bin/relaxkon" status
```

User Mode 仅写入该账号的 XDG 数据、配置、状态和缓存目录；它只绑定 `127.0.0.1`，不创建 systemd system unit、不修改 PAM、sudoers、防火墙或 `/etc`。远程连接请使用 SSH 本地转发。`relaxkon upgrade`、`stop` 和 `uninstall` 使用相同的用户态目录；没有 user systemd 或 linger 也可以使用这些命令。

User Mode 也会在该账号私有的 XDG 配置目录自动生成并保存审计实例编号和密钥；用户无需执行额外步骤。
# Recover a Linux system installation

If the server and Guardian are running but `/var/lib/relaxkonos/install-state.json`
is missing, run host preflight in the desktop Server Center, enter the sudo password
when required, and choose **Recover installation**. This uses the fixed SSH repair
operation in Linux System Mode and does not require another release ZIP.

Recovery checks root ownership and write permissions of the deployed version and
configuration, service executable paths and accounts, the managed database and data
link, effective listening address, TLS configuration, file access policies, and an
HTTP 200 response from the local health endpoint. Only after all checks pass does it
atomically publish a schema 2 installation record with a new installation identity.
Existing records are never overwritten. Data, services, and certificates are preserved.
The installed certificate is recorded as custom so later repair preserves its identity;
no previous version is inferred from unrelated directories.

If verification fails, inspect the repair operation's diagnostic details. A stopped
or inconsistent installation must be repaired before its managed state can be recovered.

## 桌面 HTTPS 证书信任

桌面登录在发送凭据前探测登录端点。遇到有效但未受系统信任的自签名证书时，显示服务器地址、主题、签发者、有效期和 SHA-256 指纹，由用户选择信任或取消。确认记录保存于本机应用数据目录的 `RelaxKonOS/servercenter/tls-certificate-pins.json`，仅适用于相同服务器地址、端口和证书指纹，不修改操作系统信任库。登录、API、上传和 SignalR HTTP/WebSocket 连接共用该记录。证书变化时停止连接并展示新旧指纹重新确认；过期、主机名不匹配或其他证书链错误仍拒绝连接。拒绝 HTTPS 证书后不会自动降级到 HTTP。
