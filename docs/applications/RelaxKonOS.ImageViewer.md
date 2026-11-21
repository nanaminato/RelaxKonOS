# 图片查看器

桌面图片查看器以独立 `.roapp` 提供，应用 ID 为 `relaxkonos.imageviewer`。项目包含自己的入口、窗口、图标和中英日资源，仅依赖桌面 SDK 和共享 UI。文件浏览器的压缩包预览与 Code Editor 的图片预览继续使用共享图片解码器。

## 安装与更新

发布桌面客户端时，MSBuild 通过 DevCli 将应用归档和 SHA-256 写入客户端发布目录的 `OfficialApps/`。

```powershell
dotnet publish Client/RelaxKonOS.Client.Desktop -c Release -o ./.codex-scratch/desktop-publish
```

在 **应用安装程序 → 官方应用** 中选择图片查看器，获取软件包并确认安装。有新版时可从同一入口获取更新，也可从该入口卸载。手动选择同一发行目录里的 `OfficialApps/relaxkonos.imageviewer.roapp` 仍可用于同版本修复。线上目录由桌面发行版预配置。见[官方桌面商店](../platform/RelaxKonOS.PackageStore.Design.md#轻量官方桌面商店)。

安装器在审查和提交时核对来源与摘要。候选版本通过客户端平台、程序集及入口校验后，用原有 VSD 指针完成更新。失败候选保留原指针和注册。图片查看器经外部应用 SDK 读取远程文件，首次使用需授予 `server.files.read`。普通工作区和 SSH 桌面分别使用各自的文件连接；授权仍受连接账号和服务端策略限制。

## Debug 开发

在隔离的 Debug profile 配对后运行 `relaxkonos-dev deploy ./Applications/RelaxKonOS.App.ImageViewer`。变更后可再次部署，或用 `watch --direct` 监听；用 `rollback relaxkonos.imageviewer` 恢复上一版本。桌面与 CLI 的配对方法见[开发者模式](../development/RelaxKonOS.DeveloperMode.md)。

## 验证记录

- Windows Release 发布生成了独立归档和摘要。正式包验收覆盖安装、测试版本更新、摘要变更与无效入口拒绝、路径与平台校验、重启发现和卸载。
- Windows Debug 桌面验收覆盖官方 ID 的 Direct Deploy、更新、候选加载失败时保留在用窗口、显式回滚和卸载。Release 桥接拒绝 Direct Deploy。
- 图片工作区检查覆盖权限提示、SVG、快捷键和实时语言切换。SSH 路径尚未在实际远端主机验收。

记事本也通过同一官方桌面商店发布。见[记事本](RelaxKonOS.Notepad.md)。
