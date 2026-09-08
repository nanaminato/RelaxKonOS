# 安装 Docker

本指南用于在运行 RelaxKonOS Server 的 Windows 或 Linux 主机上安装 Docker。请优先使用 Docker 官方安装程序或软件源，并按组织的安全策略配置镜像源和代理。

## 开始前

- 确认你拥有服务器管理员权限，且主机满足 Docker 的系统要求。
- 确认主机可以访问 Docker 软件包仓库或已配置企业镜像源。
- 安装新版本前，检查旧版 Docker 软件包或 Docker Desktop，避免重复安装。
- 生产服务器建议预留足够的磁盘空间给镜像、容器日志和卷。

## Windows

1. 在 Windows 10/11 或 Windows Server 上安装与环境匹配的 Docker Desktop 或 Docker Engine。
2. Docker Desktop 通常需要启用 WSL 2 或 Hyper-V；按安装程序提示完成重启和初始化。
3. 启动 Docker Desktop（或 Docker 服务），确认它显示为正在运行。

如果 RelaxKonOS Server 运行在 WSL、虚拟机或容器中，请确认 Docker socket/守护进程对该运行环境可用。

## Linux

1. 按当前发行版的官方 Docker Engine 说明添加软件源并安装 Docker Engine、CLI 和 Compose 插件。
2. 启动 Docker 服务，并设为开机启动，例如使用 systemd：

```bash
sudo systemctl enable --now docker
```

3. 如需让非 root 用户运行 Docker，将该用户加入 `docker` 用户组；重新登录后生效。请注意，这会授予接近 root 的主机权限。

## 验证安装

在 Windows 或 Linux 主机的终端中运行：

```bash
docker --version
docker info
```

命令应能显示客户端和服务端信息。若服务端连接失败，请先检查 Docker 服务或 Docker Desktop 是否已启动。

## 在 RelaxKonOS 中验证

打开 Docker Manager，等待状态卡片显示引擎可用。若仍不可用，请检查 Docker 服务日志，并确认运行 RelaxKonOS Server 的账户有权访问 Docker socket（Linux）或 Docker Desktop/Engine（Windows）。
