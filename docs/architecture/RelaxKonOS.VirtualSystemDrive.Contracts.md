# RelaxKonOS 虚拟系统盘契约（V1）

本文件冻结 [Virtual System Drive Goal](./RelaxKonOS.VirtualSystemDrive.Goal.md) 的 Goal 0 契约。它描述本机数据的组织方式，而非宿主操作系统的磁盘、权限边界或第三方代码隔离机制。

## 固定目录与路径规则

VSD 根目录只能由客户端 Host 计算：`{LocalApplicationData}/RelaxKonOS/SystemDrive`。所有持久化的相对路径统一使用 `/`，并在落盘前转换为平台分隔符。任何输入路径均不得为绝对路径，不得含 `..`、空段、符号链接/重解析点逃逸，且必须在完整路径规范化后仍位于预期根目录之内。

```text
SystemDrive/
  System/catalog.json
  System/associations.json
  System/automation-audit/
  Programs/BuiltIn/<app-id>/app.remoteos.json
  Programs/External/<app-id>/versions/<immutable-id>/app.remoteos.json
  Programs/External/<app-id>/current.json
  Shells/<shell-id>/shell.remoteos.json
  Users/<local-profile-id>/Desktop/*.remoteos-link.json
  Users/<local-profile-id>/Scripts/*.remoteos-script.json
  Users/<local-profile-id>/Documents/
  Users/<local-profile-id>/Downloads/
  Users/<local-profile-id>/AppData/<app-id>/
```

固定扩展名为 `app.remoteos.json`、`shell.remoteos.json`、`*.remoteos-link.json` 与 `*.remoteos-script.json`；所有 V1 JSON 文档均使用 `schemaVersion: 1`。脚本刻意只采用 JSON，避免在 V1 引入可执行 YAML 标签或自定义类型解析。

## 字段归属

| 数据 | 唯一权威来源 | 可写方 | 安全约束 |
| --- | --- | --- | --- |
| VSD 根与固定目录 | Host | Host | UI、包与脚本不能指定 Root。 |
| BuiltIn AppId、builtinKey、工厂、固定运行时元数据 | 编译进 Host 的 factory registry | Host | 磁盘 descriptor 只能被验证/恢复，不能赋予 BuiltIn 身份或选择 DLL/类型。 |
| 外置应用显示/激活/兼容性请求 | 已验证 `.roapp` manifest 与版本 descriptor | 受控安装器 | 描述文件不含 TrustLevel、权限授予、Elevation 或服务注入。 |
| 权限决策 | 现有 `IAppPermissionManager` | 用户授权 UI | descriptor、快捷方式和脚本不得改变 grant。 |
| 运行时可启动应用 | `ApplicationManager` | Catalog/安装器仅通过其 API | `catalog.json` 不是 launch registry。 |
| 窗口状态 | `WindowManager` | WindowManager | Shell 不直接写入窗口私有状态。 |
| 默认 Shell/图标布局 | Workspace 偏好；VSD 保存本机物化 | Settings/用户 | Device 值只作离线回退；不得覆盖已同步的 Workspace 选择。 |
| catalog 与关联缓存 | 经过验证的 descriptor/目录布局 | Host | 可删除并重建。 |
| 快捷方式与脚本 | 用户 VSD 目录 | 用户经 Host API | 只能引用允许的相对目标/受控 URI。 |

## DTO/schema 草案

`ApplicationDescriptor`：`schemaVersion`、`id`、`kind` (`builtin` 或 `package`)、显示元数据、`version`、图标、请求权限、文件/URI 声明、实例策略、平台、权限模型及 activation。`builtin` activation 仅允许 `builtinKey`；`package` activation 仅允许包根内的 `entryAssembly` 和 `entryType`。DTO 不是授权模型。

`ShellDescriptor`：`schemaVersion`、`id`、显示名、预览资源与受支持功能。Shell 只能由 Host 受控工厂创建；V1 不支持外置 Shell 插件。

`RemoteOsShortcut`：`schemaVersion`、稳定 `id`、显示名、`kind` (`application`、`remote-file`、`remote-folder`、`script`、`uri`)、受限 `target` 与可选图标。不得含绝对本地路径、命令行、程序集类型、权限 grant 或 HTTP 凭据。

所有解析失败使用稳定问题码，而不是异常文本：`vsd.path.invalid`、`vsd.path.escape`、`vsd.schema.unsupported`、`vsd.json.invalid`、`vsd.document.too-large`、`vsd.app-id.invalid`、`vsd.app-id.duplicate`、`vsd.builtin.mismatch`、`vsd.package.layout.invalid`、`vsd.shortcut.invalid` 与 `vsd.script.invalid`。

## 旧开发包迁移

首次启用 VSD 时，客户端只读取旧目录 `{LocalApplicationData}/RelaxKonOS/developer-apps` 及其 `catalog.json`。每条旧记录会被验证并复制/迁移至新的 `Programs/External/<app-id>/versions/<immutable-id>`；新 `current.json` 和可重建 catalog 由 Host 原子写入。成功迁移后旧目录保留，直到未来版本提供用户确认的清理流程。

不兼容、损坏或无法安全定位的记录绝不删除：保持原文件不动，记录稳定诊断并在应用安装 UI 中显示“需重新安装/修复”。迁移不复制或清除现有权限决策；更新后的包仍按既有权限模型重新进行用户授权。

## 安全与产品声明

VSD 不是真实 `C:`、宿主文件系统映射或沙箱。第三方 `.roapp` 仍不是恶意代码隔离、签名验证或可信发布者模型；它们只在现有 Host 权限门控下运行。内置 `remoteos`、`windows-like`、`macos-like` 与 `ubuntu-like` 仅是 RelaxKonOS 自己的视觉样式，不冒充原始操作系统，也不允许外置包替换 Shell 或截获全局输入。自动化调用必须携带 `UserShortcut` 或 `UserScriptLibrary` 来源并写入脱敏审计；未知/后台来源被拒绝。V1 不执行任意宿主命令、EXE、网络请求、反射代码、程序集片段或静默自动化。
