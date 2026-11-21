# 记事本

桌面记事本以独立官方 `.roapp` 提供，应用 ID 为 `relaxkonos.notepad`。保留远程文件关联、无扩展名文件和 Explorer 对已识别文本的「打开方式」入口。窗口、图标、编辑逻辑和中英日资源随包发布。

## 安装与开发

发布桌面客户端会通过 DevCli 生成 `OfficialApps/relaxkonos.notepad.roapp` 和配套 SHA-256。在应用安装器选择同一发行目录中的本地包并确认，即可安装或更新。卸载入口为 **设置 → 应用 → 记事本 → 卸载**。

使用隔离的 Debug profile 配对后，可以运行 `relaxkonos-dev deploy ./Applications/RelaxKonOS.App.Notepad`。再次部署会更新当前版本，`rollback relaxkonos.notepad` 可恢复上一版本。工作区里已有草稿时，更新、回退或卸载会先询问是否放弃，保存进行中则保留当前窗口和版本。配对及监听方法见[开发者模式](../development/RelaxKonOS.DeveloperMode.md)。

## 远程文件与编码

**打开** 和 **另存为** 使用宿主的远程 Explorer 文件选择器。在普通 RelaxKonOS Server 工作区，打开需要 `server.files.read`，保存需要 `server.files.write`，另存为同时需要读取权限来浏览目录。文件服务继续使用当前登录的服务器会话及文件权限。

**设置 → 默认打开编码** 继续读写工作区的 `notepadDefaultEncoding`。单个文件可在状态栏选择使用其他编码重新打开或保存。新建、打开、重新打开及关闭会保护未保存的草稿，读取失败或服务器拒绝写入时保留编辑内容。

## 验证记录

- Windows Release 验收覆盖从发行目录安装、更新测试版本、拒绝损坏或不兼容的候选包并保留当前版本，以及重启发现和卸载。拒绝放弃草稿会阻止更新和卸载。
- Windows Debug 验收覆盖隔离 profile 中的 Direct Deploy、更新、无效候选保留当前窗口、回退和卸载，并核对正式 VSD 指针。
- 工作区验证使用真实客户端文件选择器、文件客户端和权限门控，文件及工作区偏好由测试夹具提供。覆盖远程打开、保存、另存为、编码切换和草稿保护。
