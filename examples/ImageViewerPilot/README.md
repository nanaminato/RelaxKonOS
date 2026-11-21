# Image Viewer 官方包迁移预研

该项目使用第三方示例 ID 和 SDK 外部入口，可以通过 `deploy ./examples/ImageViewerPilot` 或常规 `.roapp` 安装。它复用[官方独立图片查看器](../../docs/applications/RelaxKonOS.ImageViewer.md)的窗口、缩放、快捷键及语言资源。启动、热更新和 `.roapp` 流程见[开发者模式](../../docs/development/RelaxKonOS.DeveloperMode.md)。

`watch --direct` 监听试点项目目录。修改 `Applications/RelaxKonOS.App.ImageViewer/` 后，重新执行 `deploy ./examples/ImageViewerPilot` 会编译两个项目并重新加载试点。

## 当前身份和试点范围

`Bootstrapper` 注册宿主内置应用的具体类型。`BuiltInApplicationRegistry` 解析这些 DI 实例，以读取清单并核对 `BuiltinKey` 和 `AppId`。VSD 用宿主工厂已知的键发现内置应用，`ApplicationManager.RegisterBuiltIn` 记录其来源。内置入口负责在 Explorer Server 文件源和 SSH 文件源之间选择，并向独立程序集传入文件读取与显示语言服务。

宿主内置 Image Viewer 继续持有官方 ID `relaxkonos.imageviewer`。试点包使用 `com.relaxkonos.example.image-viewer`，通过 `IExternalAppContext.ServerFiles` 读取获得授权的文件，保留 32 MiB 读取上限。两个入口声明同一组图片扩展名。开发者安装拒绝 `relaxkonos.*`。

## 迁移建议

1. 为 SDK 补齐可测试的 SSH 文件访问能力，使官方包接替内置入口时可以沿用现有两个文件源。
2. 复用现有签名组件目录、制品摘要和冻结安装计划。为官方应用增加产品内置的发布者公钥及 AppId 授权表。安装时验证签名、制品摘要和发布者对目标 ID 的授权，并在客户端保存验证结果作为身份来源。用户自行添加的目录来源继续分发外部应用。
3. 官方包验证、重新启动恢复、发布者密钥轮换和在线回退通过后，再把 `relaxkonos.imageviewer` 的工厂描述符切换为官方包描述符。保留同一个 AppId、文件关联和应用数据归属。官方发布者身份与特权审批分别管理，按应用所用能力评审权限。
