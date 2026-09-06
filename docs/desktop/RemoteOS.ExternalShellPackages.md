# RemoteOS 外部 Shell 包

外部 Shell 只在 RemoteOS 的主窗口内运行；它不会、也不能替换 Windows Explorer、Finder 或宿主 OS 的桌面。

外部 Shell 与其他第三方应用一样封装为 `.roapp`，通过“应用安装程序”安装。个性化页面只负责选择已安装且兼容的桌面，不再接受本地目录。归档内结构为：

```text
manifest.json
lib/net10.0/<publisher>.Shell.dll
lib/net10.0/Localization/*.json
assets/...
```

入口实现 `RemoteOS.Shell.IDesktopShellFactory`，并且只能引用 `RemoteOS.Shell`。不要引用 `Client`、服务容器、认证会话或远程文件 API。Shell 使用 `ShellPresentationContext.Actions` 与只读状态投影请求操作，并在 `InitializeAsync` 中登记完整的 `ShellSurfaces`。普通窗口工作区通过 `UpdateWorkArea` 上报；全屏 host 必须覆盖 Shell 根。

桌面包与其他外置应用统一使用根目录 `manifest.json`。包必须自行携带本地化文件，并通过 `ShellPresentationContext.Localization.LanguageChanged` 监听核心工作区语言变化；`Localization.Get` 只用于宿主拥有的术语，不应代替包内语言资源。

`manifest.json` 必须声明 `permissionModelVersion: 2` 和 `packageType: "desktopShell"`。应用安装程序负责安全解压、版本化安装和软件包目录登记；`ShellCatalog` 只从统一软件包目录发现桌面扩展，不再复制用户选择的任意文件夹。

`examples/Windows11DesktopShell` 是可构建的 Windows 11 风格外置桌面示例。它使用 AXAML、视图模型和包内 JSON 语言文件，演示壁纸、桌面快捷方式、居中任务栏、开始菜单、快速设置，以及窗口、全屏窗口和 Shell 覆盖层的正确注册方式。使用 `RemoteOS.DevCli pack` 生成 `.roapp` 后，通过 RemoteOS 应用安装程序安装。清单发现阶段不会执行程序集；只有用户在个性化页面选择该 Shell 时才通过可收集的 `AssemblyLoadContext` 加载。

如果包缺失、不兼容、禁用、初始化超时或抛异常，当前桌面保持可用；不能激活时回退 `remoteos.windows-like`，而 Workspace 中的跨设备选择意图不被覆盖。
