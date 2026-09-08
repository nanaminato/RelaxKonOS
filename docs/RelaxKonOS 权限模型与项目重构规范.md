# RelaxKonOS 权限模型与项目重构规范

> 本文档用于指导 RelaxKonOS 权限体系设计与现有项目重构。
> 目标读者包括开发者、架构维护者以及 Codex 等代码代理。
>
> 本文档中的规范性关键词：
>
> - **MUST**：必须满足。
> - **SHOULD**：原则上应满足，除非存在明确理由。
> - **MAY**：可选实现。
>
> 本文档优先描述架构边界、稳定接口和迁移规则，而非限定具体实现细节。

---

# 1. 背景

RelaxKonOS 是一个服务器管理平台，同时包含：

- 文件管理器
- 终端
- Git 客户端
- Docker 管理
- Nginx 管理
- 证书管理
- 数据库管理
- 设置
- 第三方扩展应用

RelaxKonOS 同时支持两类应用：

1. **内置应用**
   - 随 RelaxKonOS 发布。
   - 由 RelaxKonOS 官方维护。
   - 通常默认获得完成其职责所需的权限。
   - 可直接运行于 RelaxKonOS 进程或可信运行环境。

2. **外置应用**
   - 同样运行于 RelaxKonOS 应用体系内。
   - 不代表独立桌面程序。
   - 默认不拥有敏感系统能力。
   - 必须通过 RelaxKonOS 提供的 API / Broker / IPC 访问系统资源。
   - 权限由用户、管理员或系统策略授予。

本设计的核心目标是：

> **内置与外置应用使用同一种能力模型，仅在信任级别、默认授权和调用通道上存在区别。**

不得形成两套互相独立的应用开发模型。

---

# 2. 核心设计原则

RelaxKonOS 权限体系必须遵守以下原则。

## 2.1 Core 有能力，App 有权限

RelaxKonOS Core 是系统能力的真正拥有者。

应用本身只能请求能力。

```text
RelaxKonOS Core
    │
    ├─ File System
    ├─ Process
    ├─ Credential
    ├─ Network
    ├─ System
    └─ Permission Engine

App
    │
    └─ Capability Request
```

即：

```text
Core owns capabilities.
Apps receive permissions.
```

---

## 2.2 内置应用不等于拥有全部权限

禁止采用：

```csharp
if (app.IsBuiltIn)
{
    return Allow;
}
```

`BuiltIn` 只应作为权限策略输入之一。

正确模型：

```text
应用声明需求
      ↓
识别应用身份
      ↓
确定 TrustLevel
      ↓
加载系统 Policy
      ↓
合并用户授权
      ↓
计算最终权限
```

因此：

```text
BuiltIn
≠
AllowEverything
```

---

## 2.3 所有 App 均属于权限模型

以下应用即使由 RelaxKonOS 官方开发，也必须拥有独立身份：

```text
relaxkonos.files
relaxkonos.git
relaxkonos.terminal
relaxkonos.docker
relaxkonos.nginx
relaxkonos.certificates
```

它们必须：

- 声明 Capability。
- 拥有 AppIdentity。
- 经过授权决策。
- 使用统一系统服务接口。

但内置应用可以通过系统 Policy 获得默认授权，因此用户通常不会看到授权弹窗。

---

## 2.4 权限检查与授权交互必须分离

以下两个概念不能混淆：

```text
Permission Check
```

和：

```text
User Prompt
```

例如：

```text
relaxkonos.files
filesystem.read
```

每次访问仍可经过权限判断。

但由于已经具有 `SystemDefault` Grant：

```text
Check → Allow
```

不需要显示任何 UI。

因此：

> **权限系统可以始终存在，而授权提示只在必要时出现。**

---

# 3. 系统信任边界

RelaxKonOS 建议分为三层。

```text
┌─────────────────────────────────────────┐
│              RelaxKonOS Core              │
│                                         │
│ Permission / AppRuntime / Broker        │
│ FileSystem / Process / Network          │
│ Credential / System Services            │
└───────────────────┬─────────────────────┘
                    │
           Capability Boundary
                    │
       ┌────────────┴────────────┐
       │                         │
┌───────────────┐         ┌───────────────┐
│ Built-in Apps │         │ External Apps │
│               │         │               │
│ Files         │         │ Git Plugin    │
│ Git           │         │ DB Manager    │
│ Docker        │         │ Other Apps    │
└───────────────┘         └───────────────┘
```

## 3.1 RelaxKonOS Core

以下组件属于 Trusted Computing Base：

```text
RelaxKonOS.Core
RelaxKonOS.Security
RelaxKonOS.Permission
RelaxKonOS.AppRuntime
RelaxKonOS.System
RelaxKonOS.IPC
```

Core 本身不进入普通 App Permission 模型。

否则会出现无限递归：

```text
PermissionManager
需要 permission.check？
        ↓
谁检查 permission.check？
```

因此：

> **Permission Engine 是信任根之一。**

---

# 4. App Identity

任何 App 必须拥有稳定身份。

建议定义：

```csharp
public sealed record AppIdentity(
    string AppId,
    string PublisherId,
    AppTrustLevel TrustLevel);
```

其中：

```csharp
public enum AppTrustLevel
{
    BuiltIn,
    Trusted,
    ThirdParty
}
```

未来 MAY 增加：

```text
System
EnterpriseManaged
Untrusted
Development
```

---

# 5. BuiltIn 身份来源

应用不得通过 Manifest 自己声明：

```json
{
  "builtIn": true
}
```

第三方应用不能自行提升信任等级。

`TrustLevel` 必须由 RelaxKonOS Runtime 根据以下信息确定：

```text
App ID
+
Package Signature
+
Publisher
+
Install Source
+
Built-in Registry
```

例如：

```text
relaxkonos.files
+
RelaxKonOS 官方签名
+
系统安装目录
+
BuiltInApps Registry
        ↓
BuiltIn
```

第三方伪造：

```text
id = relaxkonos.files
```

但签名不匹配时：

```text
Reject
```

或者：

```text
ThirdParty
```

绝不能获得 BuiltIn 身份。

---

# 6. Manifest

Manifest 的职责是：

> **声明应用希望使用哪些能力。**

Manifest 不负责授权。

例如：

```json
{
  "id": "relaxkonos.files",
  "name": "Files",
  "publisher": "relaxkonos",
  "version": "1.0.0",

  "capabilities": [
    {
      "name": "filesystem.read",
      "scope": "*"
    },
    {
      "name": "filesystem.write",
      "scope": "*"
    },
    {
      "name": "filesystem.delete",
      "scope": "*"
    },
    {
      "name": "clipboard.write"
    }
  ]
}
```

含义是：

```text
Files 希望拥有这些能力
```

而不是：

```text
Files 已经拥有这些能力
```

---

# 7. Capability 模型

第一版 SHOULD 保持较粗粒度。

推荐：

```text
filesystem.read
filesystem.write
filesystem.delete

process.execute

network
network.listen

credential.read
credential.write

clipboard.read
clipboard.write

notification

terminal

git.repository.read
git.repository.write
git.network

docker.read
docker.manage

system.info
system.service.read
system.service.manage
```

不建议一开始拆成：

```text
filesystem.file.open
filesystem.file.read
filesystem.file.seek
filesystem.file.close
filesystem.directory.enumerate
...
```

过细的 Capability 会导致：

- Manifest 复杂。
- Policy 复杂。
- UI 难理解。
- 权限组合爆炸。
- 测试成本增加。

---

# 8. Permission Scope

Capability 表示：

> 可以做什么。

Scope 表示：

> 可以对什么做。

例如：

```text
Capability:
filesystem.read

Scope:
/home/user/projects/*
```

和：

```text
filesystem.read
scope = *
```

虽然 Capability 相同，实际权限完全不同。

建议：

```csharp
public sealed record PermissionScope(
    string Type,
    string Value);
```

例如：

```text
Type: Path
Value: /home/user/projects/relaxkonos
```

未来可扩展：

```text
Path
Host
Port
Repository
CredentialNamespace
DockerResource
Service
```

---

# 9. Grant

实际授权 SHOULD 使用统一数据结构表示。

例如：

```csharp
public sealed record PermissionGrant(
    string AppId,
    string Capability,
    PermissionScope Scope,
    GrantSource Source,
    DateTimeOffset? ExpiresAt = null);
```

`GrantSource`：

```csharp
public enum GrantSource
{
    SystemDefault,
    User,
    Administrator,
    Policy,
    Temporary
}
```

例如内置 Files：

```text
App:
relaxkonos.files

Capability:
filesystem.read

Scope:
*

Source:
SystemDefault
```

第三方 Git：

```text
App:
com.example.git

Capability:
filesystem.read

Scope:
/home/user/projects/demo

Source:
User
```

---

# 10. 系统 Policy

Manifest 和 Policy MUST 分离。

Manifest：

```text
应用想要什么
```

Policy：

```text
RelaxKonOS 愿意默认给什么
```

例如：

```json
{
  "relaxkonos.files": {
    "filesystem.read": "allow",
    "filesystem.write": "allow",
    "filesystem.delete": "allow",
    "clipboard.write": "allow",

    "credential.read": "deny",
    "process.execute": "deny",
    "network": "deny",

    "system.elevated": "prompt"
  }
}
```

这个文件属于 RelaxKonOS Core，而不是 Files App。

---

# 11. 默认策略

建议至少支持：

```csharp
public enum DefaultPermissionPolicy
{
    Allow,
    Prompt,
    Deny
}
```

语义：

### Allow

系统自动创建授权。

例如：

```text
relaxkonos.files
filesystem.read
→ Allow
```

---

### Prompt

应用允许请求，但首次使用需要用户批准。

例如：

```text
relaxkonos.git
credential.read
→ Prompt
```

---

### Deny

系统不允许该 App 使用此能力。

例如：

```text
relaxkonos.files
credential.read
→ Deny
```

---

# 12. BuiltIn 的正确语义

BuiltIn 不是权限。

它只是一个输入：

```text
AppIdentity
        │
        ├─ AppId
        ├─ Publisher
        └─ TrustLevel
                ↓
            Policy Engine
```

例如：

```text
BuiltIn Files
        ↓
读取 BuiltIn Policy
        ↓
filesystem.read = Allow
        ↓
自动创建 Grant
```

第三方文件管理器：

```text
ThirdParty
        ↓
filesystem.read
        ↓
Prompt
        ↓
用户授权目录
```

因此：

> **Builtin 决定默认授权策略，而不是绕过授权。**

---

# 13. 文件浏览器示例

官方文件浏览器：

```text
relaxkonos.files
```

Manifest：

```json
{
  "id": "relaxkonos.files",
  "capabilities": [
    "filesystem.read",
    "filesystem.write",
    "filesystem.delete",
    "clipboard.read",
    "clipboard.write"
  ]
}
```

系统 Policy：

```text
filesystem.read      Allow
filesystem.write     Allow
filesystem.delete    Allow

clipboard.read       Allow
clipboard.write      Allow

network              Deny
credential.read      Deny
credential.write     Deny
process.execute      Deny
```

因此 Files 可以正常完成：

```text
浏览
复制
移动
重命名
删除
创建
```

但无法因为自身存在漏洞就：

```text
读取 Git Token
执行 shell
启动 PowerShell
监听端口
把文件上传到互联网
```

---

# 14. App Permission 与 OS Permission

必须严格区分：

```text
App Capability
```

和：

```text
Operating System Privilege
```

例如：

```text
relaxkonos.files
filesystem.write = Allow
```

仅表示：

> RelaxKonOS 允许 Files 请求文件写入。

最终操作仍需符合：

```text
Linux UID/GID
Linux DAC
ACL
SELinux / AppArmor

Windows Access Token
Windows ACL
UAC
```

完整流程：

```text
App Permission
      ↓
RelaxKonOS 允许请求
      ↓
OS Permission
      ↓
实际操作成功或失败
```

因此：

```text
filesystem.write
≠
root
```

也不等于 Windows Administrator。

---

# 15. Elevated Operation

不建议将普通文件权限与系统提权混合。

应单独设计：

```text
system.elevated
```

或者：

```text
filesystem.elevated
process.elevated
```

例如：

```text
Files 删除用户目录文件
        ↓
普通 filesystem.delete
```

而：

```text
Files 修改 /etc/nginx/nginx.conf
        ↓
OS Permission 不允许
        ↓
请求 Elevated Operation
        ↓
sudo / polkit / UAC
```

系统提权 MUST 是明确的高风险边界。

---

# 16. Permission Evaluation

建议形成统一授权入口：

```csharp
public interface IPermissionService
{
    ValueTask<PermissionDecision> EvaluateAsync(
        AppIdentity app,
        CapabilityRequest request,
        CancellationToken cancellationToken = default);
}
```

请求结构：

```csharp
public sealed record CapabilityRequest(
    string Capability,
    PermissionScope Scope);
```

返回：

```csharp
public enum PermissionDecision
{
    Allow,
    Prompt,
    Deny
}
```

---

# 17. 权限计算顺序

建议：

```text
Capability Request
        ↓
Manifest 是否声明？
        │
        ├─ NO → Deny
        │
        ▼
是否存在 Explicit Deny？
        │
        ├─ YES → Deny
        │
        ▼
是否存在有效 Grant？
        │
        ├─ YES
        │    ↓
        │  Scope 是否匹配？
        │    ├─ YES → Allow
        │    └─ NO
        │
        ▼
是否存在 System Default？
        │
        ├─ Allow → Grant / Allow
        ├─ Prompt → Prompt
        └─ Deny → Deny
```

MUST NOT 因为 BuiltIn 而跳过以上流程。

---

# 18. Explicit Deny

建议支持显式拒绝。

例如用户：

```text
禁止某个 App 使用网络
```

即使它之前获得：

```text
network
```

也应该能够覆盖普通 Grant。

优先级可定义：

```text
Administrator Deny
      >
User Explicit Deny
      >
Grant
      >
System Default
```

具体顺序可根据 RelaxKonOS 管理需求调整，但 MUST 固定并经过测试。

---

# 19. Capability API

应用 SHOULD 不直接操作敏感系统资源。

不推荐：

```csharp
File.ReadAllText(path);
Process.Start(...);
new TcpClient(...);
```

推荐：

```csharp
IFileSystemApi
IProcessApi
INetworkApi
ICredentialApi
ISystemApi
IGitApi
IDockerApi
```

例如：

```csharp
public interface IFileSystemApi
{
    Task<byte[]> ReadFileAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task WriteFileAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string path,
        CancellationToken cancellationToken = default);
}
```

授权发生在 API 边界。

---

# 20. 内置与外置应用统一接口

内置应用：

```text
App
 ↓
IFileSystemApi
 ↓
In-process implementation
 ↓
Authorization
 ↓
Backend
```

外置应用：

```text
App
 ↓
IFileSystemApi
 ↓
RPC implementation
 ↓
IPC
 ↓
Authorization
 ↓
Backend
```

例如：

```csharp
public interface IFileSystemApi
{
    Task<FileInfoDto> GetInfoAsync(string path);
}
```

内置实现：

```text
LocalFileSystemApi
```

外置实现：

```text
RpcFileSystemApi
```

两者 API Contract MUST 尽量一致。

---

# 21. 统一模型的重要原则

禁止形成：

```text
BuiltIn App
→ Internal Services

External App
→ Completely Different APIs
```

否则长期会出现：

```text
Internal API
≠
External API
```

最终形成两套生态。

正确模型是：

```text
                  Capability API
                        │
              ┌─────────┴─────────┐
              │                   │
        Built-in Transport    External Transport
              │                   │
          In Process              IPC
              │                   │
              └─────────┬─────────┘
                        ↓
                  Authorization
                        ↓
                     Backend
```

---

# 22. Git 应用示例

Git App 建议声明：

```json
{
  "id": "relaxkonos.git",

  "capabilities": [
    "git.repository.read",
    "git.repository.write",
    "git.network",
    "credential.read"
  ]
}
```

不要默认给予：

```text
process.execute
```

如果 RelaxKonOS 提供：

```text
IGitApi
```

则 Git App 不需要执行任意系统命令。

结构：

```text
Git App
   │
   │ IGitApi
   ▼
RelaxKonOS Git Backend
   │
   ▼
GitCommandRunner
   │
   ▼
git
```

这样比：

```text
Git App
 ↓
process.execute
 ↓
任意命令
```

安全得多。

---

# 23. 避免过度授予 `process.execute`

`process.execute` 是高风险能力。

如果业务允许，应优先暴露领域 API：

```text
IGitApi
IDockerApi
INginxApi
ISystemServiceApi
```

而不是允许：

```text
git app → process.execute
```

因为任意进程执行往往可以间接突破大量 Capability 边界。

因此：

> **Domain API 优先于 Arbitrary Process API。**

---

# 24. Credential 权限

凭据 MUST 单独管理。

不得因为：

```text
filesystem.read
```

就允许应用直接扫描 Credential Store。

建议：

```text
credential.read
credential.write
```

并提供 namespace Scope：

```text
credential.read

scope:
git/*
```

或者：

```text
credential.read

scope:
github.com
```

Git App 不应该读取：

```text
database/*
relaxkonos/*
system/*
```

---

# 25. Network 权限

建议初始支持：

```text
network
network.listen
```

后续可增加 Scope：

```text
network

scope:
github.com:443
```

或：

```text
network.listen

scope:
127.0.0.1:*
```

应用主动联网和监听端口应视为不同能力。

---

# 26. Permission Store

运行时授权不能仅存在 Manifest 中。

建议建立：

```text
PermissionStore
```

持久化：

```text
AppId
Capability
Scope
GrantSource
CreatedAt
ExpiresAt
```

例如：

```json
{
  "appId": "com.example.git",
  "capability": "filesystem.read",
  "scope": {
    "type": "path",
    "value": "/home/user/projects/demo"
  },
  "source": "User"
}
```

---

# 27. Manifest 更新与权限扩张

系统 MUST 防止 App 更新自动扩权。

例如 Files 1.0：

```text
filesystem.read
filesystem.write
```

Files 2.0 新增：

```text
network
credential.read
```

不能因为：

```text
BuiltIn
```

就直接赋予新权限。

必须重新经过 Policy：

```text
Manifest 新增 capability
        ↓
System Policy
        ↓
没有 DefaultAllow
        ↓
不自动授权
```

这能够防止：

```text
软件升级
=
隐式权限升级
```

---

# 28. 建议项目结构

建议重构为：

```text
src/

RelaxKonOS.Core/
    Abstractions/
    Runtime/

RelaxKonOS.Security/
    Identity/
    Permissions/
    Policies/
    Grants/
    Scopes/

RelaxKonOS.AppModel/
    Manifest/
    Contracts/
    Runtime/
    SDK/

RelaxKonOS.System/
    FileSystem/
    Process/
    Network/
    Credential/
    Services/

RelaxKonOS.IPC/
    Protocol/
    Transport/
    Serialization/

RelaxKonOS.AppHost/
    BuiltIn/
    External/

Apps/

    RelaxKonOS.Files/
        Application/
        Views/
        ViewModels/

    RelaxKonOS.Git/
        Application/
        Views/
        ViewModels/

    RelaxKonOS.Docker/

    RelaxKonOS.Nginx/
```

---

# 29. 推荐核心接口

建议至少存在以下接口：

```csharp
public interface IPermissionService
{
    ValueTask<PermissionDecision> EvaluateAsync(
        AppIdentity app,
        CapabilityRequest request,
        CancellationToken cancellationToken = default);
}
```

```csharp
public interface IPermissionStore
{
    Task<IReadOnlyList<PermissionGrant>> GetGrantsAsync(
        string appId,
        CancellationToken cancellationToken = default);

    Task AddGrantAsync(
        PermissionGrant grant,
        CancellationToken cancellationToken = default);

    Task RemoveGrantAsync(
        PermissionGrant grant,
        CancellationToken cancellationToken = default);
}
```

```csharp
public interface IAppIdentityProvider
{
    AppIdentity GetCurrentApp();
}
```

```csharp
public interface IAppManifestProvider
{
    AppManifest GetManifest(string appId);
}
```

```csharp
public interface IAppPolicyProvider
{
    PermissionPolicy GetPolicy(
        AppIdentity app,
        string capability);
}
```

---

# 30. 请求上下文

建议应用系统 API 调用携带：

```csharp
public sealed record AppExecutionContext(
    AppIdentity Identity,
    Guid SessionId);
```

调用链不得仅相信客户端传入：

```text
AppId
```

外部应用不能发送：

```json
{
  "appId": "relaxkonos.files"
}
```

然后冒充官方应用。

AppIdentity SHOULD 由：

```text
Authenticated IPC Session
```

或：

```text
In-process AppHost
```

绑定。

---

# 31. IPC 安全边界

外置 App IPC MUST 满足：

- App 身份在连接建立时确定。
- 后续请求不得任意修改 AppId。
- 服务端执行权限判断。
- 客户端 Permission Check 只用于 UX，不具备安全意义。
- 所有敏感参数在服务端重新验证。
- 文件路径不得仅由客户端规范化。
- 不信任客户端提交的 Scope。
- 不信任客户端声称的 TrustLevel。

即：

> **客户端永远不是权限最终裁决者。**

---

# 32. TOCTOU 与路径问题

文件系统权限实现需要注意：

```text
Path Traversal
Symlink
Junction
Relative Path
Case Sensitivity
Mount Point
```

例如：

```text
允许：
/home/user/project
```

攻击者可能构建：

```text
/home/user/project/link
    ↓
/etc
```

因此 Scope 判断 SHOULD 尽量基于规范化后的真实资源。

不能简单：

```csharp
path.StartsWith(scopePath)
```

作为最终安全判定。

---

# 33. UI 行为

内置应用默认授权时：

```text
不显示权限弹窗
```

但设置界面 MAY 显示：

```text
Files

已允许：
✓ 读取文件
✓ 修改文件
✓ 删除文件
✓ 剪贴板

未允许：
✗ 网络访问
✗ 凭据
✗ 运行程序
```

第三方应用首次申请时：

```text
Example Git 希望访问：

D:\Projects\RelaxKonOS

✓ 读取文件
✓ 修改文件

[允许一次]
[始终允许]
[拒绝]
```

---

# 34. Temporary Grant

建议支持临时授权：

```text
AllowOnce
AllowSession
AllowPermanent
```

映射：

```text
Temporary
User
```

例如：

```text
打开一个 Git 仓库
```

可以只授权：

```text
当前 Session
```

而不是永久开放整个文件系统。

---

# 35. 权限撤销

所有 User Grant SHOULD 可撤销。

例如：

```text
Settings
→ Apps
→ Example Git
→ Permissions
```

用户撤销：

```text
filesystem.read
/home/user/project
```

后续请求 MUST 立即受到影响。

---

# 36. Audit

建议预留审计结构：

```text
App
Capability
Scope
Decision
Time
Reason
```

例如：

```text
relaxkonos.files
network
*
DENY
Policy
```

第一版可不持久化所有成功请求。

但 SHOULD 至少记录：

- Permission Denied。
- 权限修改。
- Elevated Operation。
- Credential Access。
- Arbitrary Process Execution。

---

# 37. 性能

统一 Permission Model 不意味着每个调用都需要 IPC 或数据库查询。

内置 App 可以：

```text
in-process
```

权限信息可以：

```text
memory cache
```

例如：

```text
PermissionSnapshot
```

只有 Grant 修改时刷新。

因此：

```text
统一权限
≠
高性能损失
```

---

# 38. Codex 重构约束

以下内容可直接作为 Codex 项目修改规则。

## MUST

Codex 在修改 RelaxKonOS 时 MUST：

1. 不允许通过 `IsBuiltIn => AllowEverything` 实现授权。
2. 不允许 Manifest 自行声明最终 `TrustLevel`。
3. 不允许第三方 App 直接访问 RelaxKonOS Core 敏感服务。
4. 不允许客户端作为最终权限判断者。
5. 新增敏感系统能力时必须通过 Capability API。
6. App Capability 与 OS Privilege 必须分离。
7. Manifest 与 Permission Grant 必须分离。
8. 默认授权 Policy 必须属于 RelaxKonOS，而非 App。
9. 内置与外置 App 应尽量共享同一 Contract。
10. 外置 App 必须通过 IPC/Broker 进入服务端授权。
11. 权限评估逻辑必须集中管理。
12. 安全边界不得依赖 UI。
13. 新权限必须有明确 Capability 名称。
14. 新 Capability 必须明确默认 BuiltIn / ThirdParty Policy。
15. 不得为了方便为 App 默认授予 `process.execute`。

---

# 39. Codex SHOULD

Codex SHOULD：

1. 将现有系统直接调用逐步抽象为 API。
2. 优先从高风险能力开始重构。
3. 避免一次性大规模改写整个项目。
4. 保持现有功能兼容。
5. 使用依赖注入区分 Local / RPC 实现。
6. 为权限计算添加单元测试。
7. 为 Manifest 加 Schema 校验。
8. 为 IPC 请求绑定 AppExecutionContext。
9. 为 Scope 提供明确 Value Object。
10. 领域能力优先于通用 `process.execute`。

---

# 40. 重构阶段

不建议一次性完成全部权限系统。

推荐分阶段实施。

## Phase 1：建立基础抽象

新增：

```text
AppIdentity
AppTrustLevel
Capability
PermissionScope
PermissionGrant
GrantSource
PermissionDecision
```

新增：

```text
IPermissionService
```

此阶段尽量不改变业务行为。

---

## Phase 2：建立 Manifest

为所有现有官方应用增加：

```text
manifest
```

例如：

```text
relaxkonos.files
relaxkonos.git
relaxkonos.docker
relaxkonos.nginx
```

但仍可保持原有服务调用。

---

## Phase 3：建立 BuiltIn Policy

建立：

```text
BuiltInPolicyRegistry
```

明确每个内置 App 默认权限。

不得使用：

```text
BuiltIn → *
```

---

## Phase 4：文件系统 API

优先将：

```text
File.*
Directory.*
```

从 App 层迁移至：

```text
IFileSystemApi
```

Files App 作为第一批验证对象。

---

## Phase 5：进程执行

统一：

```text
Process.Start
```

进入：

```text
IProcessApi
```

扫描现有 App 是否存在任意命令执行。

能够使用：

```text
IGitApi
IDockerApi
```

替代的 SHOULD 替代。

---

## Phase 6：Credential

建立：

```text
ICredentialApi
```

禁止 App 直接访问宿主 Credential Storage。

---

## Phase 7：External App IPC

建立：

```text
RelaxKonOS.App.SDK
        ↓
RPC
        ↓
RelaxKonOS.AppHost
```

外部 App 的 API Contract 与内置 App 尽可能一致。

---

## Phase 8：用户授权 UI

加入：

```text
Prompt
Allow Once
Allow Session
Allow Always
Deny
```

以及：

```text
Settings → Apps → Permissions
```

---

# 41. 重构优先级

Codex SHOULD 按以下顺序处理系统能力：

```text
1. Credential
2. Process Execution
3. File Write/Delete
4. Network Listen
5. Network Access
6. File Read
7. Clipboard
8. Notification
```

原因是前几个能力更容易形成高权限突破。

---

# 42. 文件浏览器迁移示例

旧代码：

```csharp
var content = await File.ReadAllTextAsync(path);
```

第一阶段：

```csharp
await fileSystem.ReadTextAsync(path);
```

其中：

```text
IFileSystemApi
      ↓
PermissionService
      ↓
FileSystemBackend
```

不要在 Files ViewModel 中写：

```csharp
_permissionService.Check(...);
```

因为业务代码容易遗漏检查。

正确做法：

```text
安全检查位于能力提供方
```

而不是：

```text
安全检查依赖能力调用者自觉
```

---

# 43. Git 迁移示例

旧：

```csharp
Process.Start("git", ...);
```

推荐：

```csharp
await gitApi.FetchAsync(repository);
```

实现：

```text
Git App
 ↓
IGitApi
 ↓
Permission
 ↓
Git Backend
 ↓
git
```

如果某些高级功能必须暴露命令，则建立限定接口：

```csharp
ExecuteGitAsync(...)
```

并严格限制：

```text
Executable = git
```

而不是开放任意 `Process.Start()`。

---

# 44. 目录 Scope 初期策略

第一阶段不必实现复杂 sandbox。

可以先支持：

```text
*
SpecificPath
```

例如：

```text
filesystem.read: *
```

或：

```text
filesystem.read:
/home/user/project
```

后续再支持：

```text
Home
SelectedFolder
RepositoryRoot
ApplicationData
Temporary
```

---

# 45. Capability 命名规则

统一采用：

```text
domain.operation
```

例如：

```text
filesystem.read
filesystem.write

credential.read

network.listen

git.repository.read

docker.manage
```

避免：

```text
CanReadFiles
ReadPermission
AllowNetwork
GitFullAccess
```

Capability Name SHOULD：

- 稳定。
- 可序列化。
- 可用于 Manifest。
- 可用于 Policy。
- 可用于日志。
- 尽量避免绑定 UI 文案。

---

# 46. 不应做的设计

## 禁止

```text
BuiltIn = FullTrust
```

---

## 禁止

```text
App Manifest
→ 自己指定 Allow
```

---

## 禁止

```text
Third-party App
→ 直接拿 IServiceProvider
→ Resolve RelaxKonOS internal services
```

---

## 禁止

```text
External App
→ 客户端说“我有权限”
→ Backend 信任
```

---

## 禁止

```text
UI 中检查权限
Backend 不检查
```

---

## 不推荐

```text
所有功能都通过 process.execute 实现
```

---

# 47. 最终目标架构

RelaxKonOS 权限架构目标：

```text
┌───────────────────────────────────────────┐
│                    App                    │
│                                           │
│ Files / Git / Docker / Nginx / ThirdParty│
└─────────────────────┬─────────────────────┘
                      │
                App Contracts
                      │
          ┌───────────┴────────────┐
          │                        │
       In Process                 RPC
          │                        │
          └───────────┬────────────┘
                      │
               AppExecutionContext
                      │
                      ▼
              Capability Gateway
                      │
                      ▼
                Authorization
                      │
         ┌────────────┼─────────────┐
         │            │             │
     Manifest       Policy        Grants
         │            │             │
         └────────────┼─────────────┘
                      ▼
               PermissionDecision
                      │
                      ▼
              RelaxKonOS Backend
                      │
     ┌────────────────┼────────────────┐
     │                │                │
 FileSystem         Process         Credential
     │                │                │
 Network           Git/Docker        System
     │                │                │
     └────────────────┼────────────────┘
                      ▼
                      OS
```

---

# 48. 核心规则总结

RelaxKonOS 权限模型最终遵循以下公式：

```text
Requested Capability
        ∩
Manifest Declaration
        ∩
System Policy
        ∩
Effective Grants
        ∩
Scope
        ∩
OS Permissions
        =
Effective Access
```

而：

```text
TrustLevel
```

只影响：

```text
Policy
```

不直接等于最终访问权限。

因此：

```text
BuiltIn
      ↓
默认 Policy 更宽松
      ↓
自动获得职责所需权限
```

而不是：

```text
BuiltIn
      ↓
Bypass Security
```

---

# 49. 一句话架构原则

RelaxKonOS 权限系统应始终坚持：

> **Core 有能力，App 有权限；应用声明需求，系统决定授权；BuiltIn 是信任等级，不是万能通行证。**

这一原则 SHOULD 作为后续 RelaxKonOS App Runtime、插件体系、文件系统、Git、Docker、Nginx、证书管理以及第三方应用 API 设计的基础约束。
