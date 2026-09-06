# RemoteOS 外部 Shell 包

外部 Shell 只在 RemoteOS 的主窗口内运行；它不会、也不能替换 Windows Explorer、Finder 或宿主 OS 的桌面。

包目录由用户在个性化设置页明确选择：

```text
shell.json
lib/net10.0/<publisher>.Shell.dll
assets/...
```

入口实现 `RemoteOS.Shell.IDesktopShellFactory`，并且只能引用 `RemoteOS.Shell`。不要引用 `Client`、服务容器、认证会话或远程文件 API。Shell 使用 `ShellPresentationContext.Actions` 与只读状态投影请求操作，并在 `InitializeAsync` 中登记完整的 `ShellSurfaces`。普通窗口工作区通过 `UpdateWorkArea` 上报；全屏 host 必须覆盖 Shell 根。

`examples/NeonDesktopShell` 是可构建的最小示例。将其输出 DLL 放到 manifest 所示路径后即可形成开发包。未签名开发包要求用户显式开启 Developer Mode；发行包须提供并通过 `shell.json.sha256` 的 entry assembly 哈希验证。清单发现和校验从不执行程序集，程序集只在用户选择该 Shell 时通过可收集的 `AssemblyLoadContext` 加载。

如果包缺失、不兼容、禁用、初始化超时或抛异常，当前桌面保持可用；不能激活时回退 `remoteos.default`，而 Workspace 中的跨设备选择意图不被覆盖。
