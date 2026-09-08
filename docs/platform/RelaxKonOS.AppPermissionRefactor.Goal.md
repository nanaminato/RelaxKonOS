# RelaxKonOS 应用权限模型与项目重构（Goal 执行版）

> 状态：**可实施**
>
> 建立日期：2026-09-05
>
> 前置规范：[权限模型与项目重构规范](../RelaxKonOS%20权限模型与项目重构规范.md)
>
> 关联但独立：[跨平台特权操作与 Helper（Goal 执行版）](./RelaxKonOS.PrivilegedOperations.Goal.md)

本文将权限模型规范转换为当前产品决策下可直接实施的 Goal。该决策已经确认：RelaxKonOS 支持第三方 App，但是否安装和使用由用户自行判断风险；推荐开源、可审查的包。首版**不**引入 App 进程隔离、OS 沙箱、包签名、发布者信任根或 app-bound Server 凭据。

这是一种“用户信任安装包”的扩展模型，而不是对恶意 App 的安全沙箱模型。本文的 App 权限用于能力声明、默认策略、用户可见性、正常 SDK 调用的授权门控与误操作防护；它不能阻止与 Shell 同进程执行的恶意 .NET 代码绕过 Client 侧检查、反射 Host 服务或直接使用操作系统 API。

本 Goal 是一次**破坏性权限模型升级**：不保留旧 `.roapp`、旧 manifest、旧 SDK 权限调用或旧本地授权记录的兼容性。升级到新模型后，旧包必须由其作者按新 SDK/manifest 重新打包，用户必须重新安装并重新授权。

## 1. 执行结论与风险接受

### 1.1 结论

**按此范围实施可行，复杂度为中等，且可以复用仓库已有的大部分 App 权限基础设施。**

不做隔离和签名后，不需要重构包运行时、建立跨平台沙箱或设计 Server Broker；改造重点收敛为：统一权限评估、Host-owned 默认策略、内置 App 清单补齐、权限 UI/存储迁移、SDK 能力门控和高风险能力的显式默认拒绝。

### 1.2 已接受的风险

- 第三方包通过 `DeveloperPackageManager` 在 Client/Shell 同一进程加载；`AssemblyLoadContext` 仅隔离加载/卸载，不隔离权限。
- 用户安装的恶意包理论上可绕过 Client 本地 Grant、调用 .NET 文件/网络/进程 API，或尝试取得已登录用户的 Client/Server 访问能力。
- 现有 Server capability endpoint 接受 Client 提交的 `AppId`，因此它不能作为 Server 端的不可伪造 App 身份或安全授权边界。
- `ThirdParty`、`Trusted`、`Development` 等 TrustLevel 在本 Goal 中只用于**默认策略和 UX 标识**，不应被描述为安全隔离等级。

这些风险必须在开发者模式、包安装页和第三方 App 文档中明确说明。用户的安装决定是该模型的信任根。

### 1.3 仍然不可降低的安全边界

本 Goal 不放宽以下边界：

1. RelaxKonOS 用户认证、Workspace/Server 资源授权和宿主 OS 权限继续由 Server/OS 最终裁决。
2. `RelaxKonOS.PrivilegedHelper` 及 Host Elevation 继续遵循 `RelaxKonOS.PrivilegedOperations.Goal.md`；App Capability 不等于 root、LocalSystem 或 Administrator。
3. SDK 不新增任意 `process.execute`、shell、PowerShell、任意服务控制、任意网络代理、原始 JWT 导出或特权 Helper transport。
4. 正常 SDK 调用仍必须在 Host 侧经过集中权限评估；Client 的 AppId 只能用于产品行为和审计标签，不能被 Server 当成不可伪造身份。

## 2. 当前可复用基线

| 现有基础 | 处理方式 |
| --- | --- |
| `AppPermissions` 的 `server.*` 目录 | 作为新模型的初始能力目录；不承诺旧包或旧 ID 的兼容性。 |
| `ApplicationManifest.RequestedPermissions` | 保持为“声明需求”，不是 Grant。 |
| `JsonAppPermissionManager` 和权限 UI | 迁为统一评估器的本地 Grant/Explicit Deny 存储适配层；旧授权文件不迁移。 |
| `ExternalAppContextFactory` / SDK | 继续作为外置包的推荐能力入口，逐步接入统一评估器。 |
| 内置 App 的 `remoteos.*` ID | 作为 BuiltIn Policy 的键；不因命名空间直接得到全权限。 |
| `IHostElevationSessionStore` / Privileged Helper | 保持独立；不合并进 App Permission Store。 |

当前 `AppCapabilityEndpoints` 是用户 JWT 下的过渡产品接口。新模型可以替换其 App capability 调用方式，不为旧包保留兼容分支；它也不得被扩展为“Server 可信地识别第三方 App”的机制或高风险能力入口。

## 3. 目标模型

### 3.1 三层授权保持分离

```text
App Capability
  App 是否被允许通过 Host SDK 请求某个领域功能

RelaxKonOS User Authorization
  当前登录用户是否被 Server 允许访问资源

Host Elevation
  已获准业务操作是否还需要 root / LocalSystem / Administrator
```

例如 `server.files.write = Granted` 只表示 Host SDK 可以发起文件写入请求；Server 和宿主 OS 仍会拒绝该用户无权访问的路径。它不授予管理员权限，也不会绕过 Helper 的 capability + target-scope 授权。

### 3.2 统一评估规则

实现单一的 `IPermissionEvaluator`，并固定以下优先级：

```text
未知 capability 或 manifest 未声明        → Deny
Administrator / User Explicit Deny        → Deny
有效 Temporary / User Grant 且 Scope 匹配 → Allow
BuiltIn / ThirdParty Default Policy        → Allow / Prompt / Deny
```

内置 App 的 `Allow` 是 `SystemDefault`，不是硬编码 bypass。第三方 App 默认 `Prompt` 或 `Deny`；只有用户明确批准才写入本地 Grant。首版不实现需要复杂资源解析的通用 Scope：无 scope 的 capability 和经领域验证的 `Path` scope 优先，`Service`、`Repository` 等只在对应 API 实现规范化检查后加入。

### 3.3 轻量身份模型

保留稳定 AppId，并新增可显示、可审计但非安全断言的来源信息：

```csharp
public sealed record AppIdentity(
    AppId AppId,
    AppTrustLevel TrustLevel,
    string InstallSource);
```

- `BuiltIn`：由 Client 注册的官方应用。
- `Development`：开发者模式安装的本地 `.roapp`。
- `ThirdParty`：未来的用户安装包类型；首版可与 `Development` 复用同一加载机制。
- `Trusted`：可选的用户手动标记，**不**代表签名验证或安全特权。

Manifest 不包含最终 `TrustLevel`；Host 根据注册/安装路径生成。没有签名时，Host 不能证明发布者身份，因此 UI 必须显示“来源未经验证”。

### 3.4 推荐调用链

```text
Built-in / third-party App
  → Host SDK capability facade
  → IPermissionEvaluator（本地产品授权）
  → Client adapter / RelaxKonOS API
  → Server user authorization
  → optional Host Elevation / Privileged Helper
  → OS
```

对内置 App，facade 可使用 in-process adapter；对第三方 App，仍使用现有 `IExternalAppContext`。两者共享 capability 名称、默认策略、Grant 语义和领域 contract，但本 Goal 不承诺其传输层是安全边界。

## 4. Goal 执行计划

每个 Goal 完成后保持 `dotnet build RelaxKonOS.sln -c Debug` 通过，并为新模型的评估规则与门控行为添加测试。不得将未完成的高风险能力以“临时直连”方式暴露给 App。

### Goal 0：决策落档与规范对齐

**工作**

- 在开发者模式、包安装与 SDK 文档中写明“用户自行评估第三方包风险；权限不是恶意代码沙箱”。
- 将主规范中依赖受认证 IPC、包签名或第三方安全隔离的条目标注为未来增强模式，避免与本 Goal 的产品决策相互矛盾。
- 为新 manifest 和 SDK 定义 `permissionModelVersion: 2`；旧包在安装/加载时明确拒绝，并提示重新打包。
- 清点当前 `server.*` 权限、内置 App manifest、`IExternalAppContext` 能力和 App capability endpoint；冻结新模型的 v2 权限目录。
- 为每个现有 capability 指定 BuiltIn / ThirdParty 默认值与是否允许 scope；默认拒绝凭据原文、任意进程执行、端口监听、任意服务管理与 Host Elevation。

**验收**

- 产品文档不再把 `AssemblyLoadContext`、本地 Grant、`Trusted` 标签或 `AppId` 声称为安全隔离。
- 旧包、旧 manifest 和旧授权记录有明确的拒绝/清理行为，没有迁移或兼容分支。
- 有一份权限矩阵可驱动后续 Policy Registry 和测试。

### Goal 1：统一权限领域模型与 Policy Registry

**工作**

- 在 `RelaxKonOS.Core.Applications` 新增 `AppIdentity`、`AppTrustLevel`、`PermissionScope`、`PermissionGrant`、`GrantSource`、`PermissionDecision` 与纯 `IPermissionEvaluator`。
- 新增 Host-owned `IAppPolicyProvider` / BuiltIn Policy Registry；Policy 不放在 manifest 中。
- 将 `JsonAppPermissionManager` 替换为 `IPermissionStore` 的 v2 本地实现，保留 Windows DPAPI；升级时删除或忽略旧 `Granted`/`Denied` 数据，不做迁移。
- 初版支持 `SystemDefault`、`User`、`Temporary` grant 与 `ExplicitDeny`；缓存只能在 grant/policy 变更后失效，不得把过期 temporary grant 当永久 grant。

**验收**

- Manifest 未声明、未知 ID、显式拒绝、临时授权过期、scope 不匹配及 BuiltIn 默认允许均有单元测试。
- `BuiltIn` 不存在 `AllowEverything` 分支；同一能力在内置与外置 App 均通过相同优先级计算。
- 未改变任何现有业务 endpoint 的用户/OS 授权行为。

### Goal 2：内置 App 清单与默认策略迁移

**工作**

- 为全部内置 App 补齐实际需要的 `RequestedPermissions`，并建立覆盖表：Explorer、Git、Docker、Web Server、Certificates、Firewall、Proxy、Terminal、Registry、Guardian、Tunnels 等。
- 为每个内置 App 编写最小 `SystemDefault` Policy；未声明或不需要的敏感能力默认 deny。
- 将 Shell 内置 App 的常用 capability 判断收敛到 facade/评估器；保留旧 Client 服务适配层，避免一次性移动所有 ViewModel。
- Settings 展示内置 App 的默认允许、用户拒绝和未声明项，但不向用户弹出内置 App 的首次授权窗口。

**验收**

- 内置 App 正常体验不回归；默认 grant 可在设置中可见。
- 用户 explicit deny 后，使用 facade 的内置 App 调用立即被拦截并显示可操作提示。
- 新增内置 App 权限必须同时提交 manifest 与 Policy 条目，否则测试失败。

### Goal 3：第三方包治理与 SDK 门控迁移

**工作**

- 将开发包/后续用户包登记为 `Development` 或 `ThirdParty`；只接受 `permissionModelVersion: 2` 的包，并展示包路径、版本、未经验证来源与其声明权限。
- 保持 `IExternalAppContext` 是第三方包的推荐入口；所有已暴露的 facade（文件、指标、桌面外观、媒体、设置）改用 `IPermissionEvaluator`，不再直接读取 `IsGranted`。
- 将首次提示由“逐权限启动时弹窗”调整为按 capability 首次使用请求；支持允许、拒绝、稍后和有限的 `AllowSession`。
- 包更新后不继承旧包的 Grant；用户重新安装或更新 v2 包后必须重新授权其全部 capability。

**验收**

- 未声明、未批准或被拒绝的权限不能通过正常 SDK facade 使用。
- 权限撤销立即阻止后续 facade 调用；媒体 lease 等短期资源不得被继续自动续期。
- UI 明确提醒：该门控不防御恶意/已受损 App，安装前应审查来源和代码。

### Goal 4：Scope、审计与高风险领域 API

**工作**

- 先为文件读写实现安全的 Path scope 规范化与边界测试；不依赖简单字符串前缀判断。
- 为 Git repository、Docker、Web Server、Certificate 等按领域增加精确 resource scope；未完成验证的能力保持无外置 App API。
- 记录权限修改、explicit deny、临时 grant、拒绝结果与经 SDK 发起的 Host Elevation 请求；日志不记录 token、密码、文件内容或私钥。
- 显式定义第三方 App 不可通过 SDK 发起的能力：凭据原文、任意命令执行、任意网络/监听、任意服务控制和 Host Elevation。

**验收**

- 每个新增能力均具备 manifest、Policy、grant/revoke、参数验证、审计与测试。
- Path scope 覆盖路径穿越、符号链接/重解析点、大小写和挂载边界测试。
- Host Elevation 仍只能由受控业务 endpoint + 用户管理员认证触发，不因 App Grant 自动通过。

### Goal 5：回归、文档与未来增强接口

**工作**

- 覆盖 Windows/Linux 的 v2 manifest、Policy、Grant、撤销、升级和 SDK 门控测试；不编写旧 manifest、旧授权文件或旧包兼容性测试。
- 完善包安装风险提示、开源包审查建议、开发者模式说明和故障排查文档。
- 预留未来安全增强的替换点：`IAppPermissionStore`、`IAppPolicyProvider`、`IExternalAppContext` facade 与 Server capability adapter；不得把当前本地 AppId 绑定逻辑固化为不可替换协议。

**验收**

- 文档、UI 与代码对“权限治理”及“非沙箱”表述一致。
- 未来若选择包签名、独立进程或 Broker，可替换 transport/identity adapter，而无需重写 Policy、Grant 和领域 contract。

## 5. 初始策略建议

| 能力类别 | BuiltIn 默认 | ThirdParty / Development 默认 | 首版 scope |
| --- | --- | --- | --- |
| 桌面外观、应用私有设置、系统语言 | Allow | Prompt | 无 |
| 服务器指标 | Allow（仅需要者） | Prompt | 无 |
| 服务器文件读写 | Allow（Explorer 等需要者） | Prompt | `Path`，先只读后写入 |
| Git repository | Allow（Git） | Deny，待领域 API | `Repository` |
| Docker、Web Server、证书、Firewall、Proxy | Allow（对应内置 App） | Deny，待逐项领域 API | 精确资源 ID |
| 凭据原文、任意进程/网络/服务控制、Host Elevation | Deny 或受专用内置流程控制 | Deny | 不开放 |

内置 App 的 Allow 也必须只覆盖其 manifest 声明的 capability。用户显式拒绝可覆盖默认 Allow；对于维持 RelaxKonOS 基础功能所必需的 App，UI 可以提示影响，但不得静默忽略拒绝。

## 6. 实施约束

- 不新建第二套 `filesystem.*` 权限目录；新模型直接定义并使用 v2 `server.*` capability 目录，不提供旧 ID alias 或迁移层。
- 不把 `AppPermission` 与 `HostElevationCapability` 合并，也不将 Grant 传给 Privileged Helper。
- 不将用户密码、JWT、Host Elevation session、共享密钥或完整文件内容写入权限存储、包元数据或审计日志。
- 不为方便第三方包访问而向 `IExternalAppContext` 暴露 `IServiceProvider`、通用 HTTP client 或任意本地文件/进程 API。
- 在没有进程隔离的前提下，所有 UI 文字和开发者文档必须避免“安全隔离”“受信任包”“防止恶意 App”等承诺。

## 7. 已确认的实施前提

1. 第三方 App 使用风险由用户自行判断；推荐开源且可审查的包。
2. 首版不实施进程隔离、OS 沙箱、包签名或发布者信任根。
3. 当前权限系统的定位是治理和正常调用门控，不是恶意代码防御。
4. 宿主 OS 特权操作仍按照独立的 Helper Goal 维持严格受限设计。

可以从 Goal 1 开始实现；Goal 0 的文档对齐工作应随第一个实现提交一并完成。
