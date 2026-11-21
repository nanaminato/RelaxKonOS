# Image Viewer 官方包迁移预研

该项目以 SDK 外部应用入口构建，可以使用 `deploy ./examples/ImageViewerPilot` 或常规 `.roapp` 安装。它复用现有 Image Viewer 的解码器源码，支持通过 Explorer 的打开方式进入预览，并使用 `IExternalAppContext.ServerFiles` 读取获得授权的文件。包仅依赖 SDK 和 Avalonia。启动、热更新和 `.roapp` 流程见[开发者模式](../../docs/development/RelaxKonOS.DeveloperMode.md)。

## 当前身份和试点范围

`Bootstrapper` 注册宿主内置应用的具体类型。`BuiltInApplicationRegistry` 立即解析这些 DI 实例，以读取清单并核对 `BuiltinKey` 和 `AppId`。VSD 用宿主工厂已知的键发现 BuiltIn 应用，`ApplicationManager.RegisterBuiltIn` 再记录其来源。内置 Image Viewer 还直接依赖宿主的 Explorer、SSH 会话、本地化和窗口管理服务。

宿主内置 Image Viewer 继续持有官方 ID `relaxkonos.imageviewer`。试点包使用 `com.relaxkonos.example.image-viewer`，按外部应用处理文件关联和权限。开发者安装拒绝 `relaxkonos.*`。试点实现 SDK 激活和权限受控的图片读取。SSH 文件源切换、完整工具栏和快捷键由内置版本承担。

## 迁移建议

1. 将 `ImageViewerDecoder`、视图和纯显示逻辑移入独立应用程序集。现有内置入口在迁移期间复用这些组件。为 SDK 补齐可测试的文件选择和 SSH 文件访问能力后，再迁移原来的文件关联及交互。
2. 复用现有签名组件目录、制品摘要和冻结安装计划。为官方应用增加产品内置的发布者公钥及 AppId 授权表。安装时验证签名、制品摘要和发布者对目标 ID 的授权，并在客户端保存验证结果作为身份来源。用户自行添加的目录来源继续分发外部应用。
3. 官方包验证、重新启动恢复、发布者密钥轮换和在线回退通过后，再把 `relaxkonos.imageviewer` 的工厂描述符切换为官方包描述符。保留同一个 AppId、文件关联和应用数据归属。官方发布者身份与特权审批分别管理，按应用所用能力评审权限。
