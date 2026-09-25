# RelaxKonOS 一键服务端安装

发布制品分为 `client`、`server` 与 `user-server` 三种包。`server` 包是 System Mode，包含已 `dotnet publish` 的 Server、Guardian Agent、权限助手和平台部署引擎；`user-server` 是无 sudo 的 Linux User Mode，包含 Server、同 UID Guardian 和用户 launcher，但不包含权限助手、sudoers 或系统服务安装器；`client` 包只包含桌面 Client。

引导安装器会先把三个组件的完整 publish 输出复制到持久安装目录（Windows 默认 `C:\Program Files\RelaxKonOS`，Linux 默认 `/opt/relaxkonos`）；服务绝不会指向临时下载目录或离线介质。

## 发布包布局

```text
manifest.json
payload/windows/{server,guardian,privileged-helper}/...
payload/linux/{server,guardian,privileged-helper}/...
deployment/windows/Install-RelaxKonOSServices.ps1
deployment/linux/install-relaxkonos-services.sh
```

`manifest.json` 和下载描述文件使用 `schemaVersion: 1`，并明确标记 `packageKind`（`client` 或 `server`）；示例见 [release-manifest.example.json](./release-manifest.example.json)。线上安装必须由发布页同时提供 ZIP 的 SHA-256，安装器会在解压前验证它。正式发行应在此基础上对 ZIP 使用代码签名或签名的发布清单。

维护者用下列命令制作一个自包含的单平台发布包（会同时生成 ZIP 与同名 `.sha256` 文件）：

```powershell
./deployment/packaging/New-RelaxKonOSRelease.ps1 -Version 0.1.0 -Runtime win-x64
```

Linux System Mode 则运行：

```bash
./deployment/packaging/package-relaxkonos.sh 0.1.0 linux-x64 Release
```

客户端的便携 ZIP 与 Windows MSIX 打包和升级流程见 [ClientDistribution.md](./ClientDistribution.md)。Linux 客户端通过便携 ZIP 分发；不提供 Debian/Ubuntu APT 仓库或 `.deb` 包。

两者都会分别产出 Client 与 Server ZIP、`.sha256` 与同名 `.json` 下载描述文件。用户态 Linux 包可单独生成：

```bash
./deployment/packaging/package-relaxkonos.sh 0.1.0 linux-x64 Release ./artifacts user-server
```

System Mode 安装时请选择 `*-server.zip` 或对应的 Server 发布目录；User Mode 请选择 `*-user-server.zip` 解压后的发布目录。

## 官方在线来源

安装器默认从 `https://downloads.relaxkon.com/relaxkonos/stable/latest/{rid}.json` 读取当前稳定版，其中 `{rid}` 是 `win-x64`、`win-arm64`、`linux-x64` 或 `linux-arm64`。该描述文件包含 ZIP 的 HTTPS 地址和 SHA-256；将通过验证的版本描述文件同步为 `latest/{rid}.json`，即可完成稳定版切换，无需修改安装器。

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

离线介质可直接是发布目录或 ZIP 文件，例如：`Install-RelaxKonOS.ps1 -BundlePath E:\media\RelaxKonOS-0.1.0-win-x64.zip`。安装前会校验 Windows 架构与包内 `runtime` 是否相符。

## Linux

System Mode（需要 root）使用显式模式：

```bash
sudo ./deployment/bootstrap/install-relaxkonos.sh --mode system --bundle /mnt/RelaxKonOS-release
```

直接运行安装器即可获取官方稳定版。也可传入 `--release-uri URL --release-sha256 SHA256` 安装指定 ZIP，或用 `--bundle` 进行离线安装。Linux 权限助手不是常驻服务：Server 账户只能通过固定的 sudo 规则执行 root-owned Helper；Server 和 Guardian 则是 systemd 服务。

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
