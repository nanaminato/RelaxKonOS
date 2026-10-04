# 桌面端文档

桌面客户端采用 Avalonia 本地渲染，桌面内的应用窗口由 WindowManager 管理。此目录负责桌面体验；Android 文档入口见 [移动端简介](../mobile/README.md)。

## 按任务阅读

| 任务 | 入口 |
|---|---|
| 修改内置应用界面与查看逐项分析 | [BuiltInApps UI](RelaxKonOS.BuiltInApps.UI.md) |
| 理解桌面、宿主窗口、全屏与模态交互 | [Desktop](RelaxKonOS.Desktop.md) |
| 调整浅色/深色、调色板和强调色 | [Theming](RelaxKonOS.Theming.md) |
| 调整窗口、菜单、圆角、尺寸和动效 | [SystemStyle](RelaxKonOS.SystemStyle.md) |
| 修改启动器、任务栏、Dock 或切换 Shell | [ShellLauncher](RelaxKonOS.ShellLauncher.Goal.md) |
| 开发外置桌面包 | [ExternalShellPackages](RelaxKonOS.ExternalShellPackages.md) |
| 修改设置与偏好同步 | [Settings](RelaxKonOS.Settings.md) |
| 添加或修改界面文字 | [Localization](RelaxKonOS.Localization.md) |
| 修改关于页面 | [AboutPage](RelaxKonOS.AboutPage.md) |

## 修改与验证

优先复用动态语义资源。配色、系统风格与 Shell 布局分别由 `DesktopExperience.Appearance`、`SystemStyleId` 和 `ShellSelection` 表达。窗口生命周期和模态链归 WindowManager；Shell 提供布局和承载面。

在仓库根目录构建桌面端：

```sh
dotnet build Client/RelaxKonOS.Client.Desktop/RelaxKonOS.Client.Desktop.csproj -p:UsedAvaloniaProducts=
```

`UsedAvaloniaProducts` 在本地验证中置空，用于跳过构建遥测。交互变更应运行对应的客户端测试，并在目标平台检查主题、缩放、键盘操作与长文本。任务栏预览的验证入口和人工验收边界见 [Desktop](RelaxKonOS.Desktop.md)。

`*.Goal.md` 与 `*.Plan.md` 保存规划和执行记录；阅读时先核对文首状态和日期。实现契约优先查看上述专题文档及其引用的当前代码，历史统计不作为当前缺陷清单。
