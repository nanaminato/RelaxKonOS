# RelaxKonOS 应用权限 v2

RelaxKonOS 的应用权限是对正常 Host SDK 调用的能力声明、默认策略和用户可见授权，**不是恶意 App 的安全沙箱**。开发包与未来的第三方包会在 Client/Shell 进程中加载；`AssemblyLoadContext` 只服务于加载和卸载，不能隔离 .NET 代码、操作系统 API 或反射访问。

安装决定由用户作出。请优先选择开源、可审查的软件包，并在安装前检查来源、版本、声明的权限和代码。当前没有包签名、发布者信任根、独立 App 进程或 OS 沙箱；`Trusted` 等来源标签也不表示加密验证或额外特权。

## v2 包要求

每个 `.roapp` 的 `manifest.json` 必须包含 `"permissionModelVersion": 2`。旧 manifest 和旧本地授权记录不会迁移：旧包被拒绝，更新或重新安装 v2 包后必须重新授权所有 capability。

`requestedPermissions` 只声明需求，不能自行授予能力。Host 按下列顺序评估：未知或未声明 capability 拒绝；显式拒绝优先；有效用户/临时授权且 scope 匹配时允许；其余按 Host-owned 默认策略处理。内置应用的默认允许也必须同时存在于其 manifest 与 Host policy 中，且用户显式拒绝会覆盖它。

第三方包应仅通过 `IExternalAppContext` 使用已暴露的 facade，在实际首次使用 capability 时调用自己的 `Permissions.RequestAsync`。不得依赖权限取得任意进程执行、网络监听、服务控制、凭据原文或 Host Elevation；这些能力不属于 v2 外置 App SDK。

Server 用户授权、宿主 OS 授权和 Host Elevation 仍是独立边界。App capability 允许某项 SDK 请求，并不绕过它们。
