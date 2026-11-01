# RelaxKonOS 组件版本发现与制品选择设计

> 状态：设计提案，2026-11-01。
>
> 本文补充 [声明式组件目录与安装提供者设计](RelaxKonOS.ComponentCatalog.Design.md)，解决不同组件版本发现方式不同的问题。核心结论是：**版本发现、制品选择、安装执行必须分层**；`InstallerProvider` 不负责从上游发现版本。

## 1. 为什么需要独立 Release Provider

不同组件的版本来源并不一致：

- Debian/Ubuntu 的 Git、Nginx 等可以从 APT 仓库取得候选版本；
- Git for Windows、Mihomo、FRP 等可以从固定 GitHub Releases 仓库取得版本和 assets；
- Nginx Windows 当前需要解析 nginx.org 的官方版本列表；
- RelaxKonOS 自有 `.roapp` / `.rkmodule` 可以来自本地 Catalog 或未来的签名 Package Store；
- 某些组件可能只有静态固定版本或离线制品。

因此不能让 `download`、`archive` 等安装 provider 同时承担“有哪些版本”的发现逻辑，否则 provider 会重新变成按具体组件堆叠特殊分支的中心。

目标流水线调整为：

```text
Catalog / Component Manifest
        ↓
Compatibility Resolver
        ↓
Release Provider
“有哪些可用版本？”
        ↓
Artifact Selector
“这个版本中哪个制品适合当前目标？”
        ↓
InstallPlan
        ↓
Policy / Trust / Approval
        ↓
Installer Provider
“已冻结的这个制品怎样安装？”
        ↓
Domain Installer / TaskOrchestration / Privileged Helper
```

## 2. Release Provider

建议定义：

```csharp
public interface IComponentReleaseProvider
{
    string Type { get; }

    Task<IReadOnlyList<ComponentRelease>> GetReleasesAsync(
        ComponentManifest component,
        InstallTarget target,
        ComponentHostEnvironment environment,
        CancellationToken cancellationToken = default);
}
```

统一输出可以包含：

```csharp
public sealed record ComponentRelease(
    string Version,
    string ReleaseId,
    string Channel,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ComponentArtifact> Artifacts);
```

组件中心只消费统一的 `ComponentRelease[]`，不需要知道版本是来自 APT metadata、GitHub API、HTML 页面还是本地签名目录。

### 2.1 优先建设通用 Provider

不要默认“每个软件一个 Provider”。优先复用通用版本来源：

| Release Provider | 用途 |
| --- | --- |
| `apt` | Debian/Ubuntu 仓库候选与精确包版本 |
| `github-releases` | 固定 GitHub repository 的 Releases API |
| `static` | manifest/Catalog 中固定的版本与制品 |
| `catalog` | RelaxKonOS 本地或签名 Package Catalog |
| `http-manifest` | 明确定义 schema 的结构化版本清单；后续按需求增加 |

只有上游无法安全映射到通用 provider 时才增加专用实现，例如当前 Nginx Windows 的官方页面解析可以是 `nginx-official`。

这样新增普通 GitHub 项目通常只增加 manifest；新增一种真正不同的版本发现协议时才增加 C# Provider。

## 3. Artifact Selector

“发现 Release”和“从 Release 中选择适合当前 Host 的文件”也是两个问题。

GitHub Release 可能同时包含：

- Windows x64 / arm64；
- Linux x64 / arm64；
- installer / portable；
- zip / tar.gz / exe / msi；
- checksum / signature；
- debug 或其他不应安装的资产。

因此 Release Provider 不应直接决定最终安装文件。建议增加：

```csharp
public interface IComponentArtifactSelector
{
    string Type { get; }

    ComponentArtifactSelection Select(
        ComponentRelease release,
        InstallTarget target,
        ComponentHostEnvironment environment);
}
```

第一阶段可以有：

- `package`：APT 等系统包，不存在普通下载 asset；
- `pattern`：在受信任 provider 返回的 assets 中按声明式规则选择；
- `catalog-artifact`：Catalog 已给出精确 artifact identity；
- 少量专用 selector：只有资产命名或平台规则无法安全通用化时使用。

Selector 只从 Release Provider 已接受的候选制品中选择，不允许借 selector 引入任意新 URL。

## 4. Manifest 示例

### 4.1 Git on Linux

APT 同时提供版本候选和包身份，但版本发现与安装职责仍保持逻辑分离：

```json
{
  "os": "linux",
  "architectures": ["x64", "arm64"],
  "releases": {
    "type": "apt"
  },
  "artifact": {
    "selector": "package"
  },
  "installer": {
    "type": "apt",
    "packages": ["git"]
  }
}
```

`AptReleaseProvider` 查询目标 Host 当前受信任仓库中的候选版本；`AptInstallerProvider` 只负责把已冻结的精确包版本和依赖事务交给现有 package ownership / Helper 路径。

### 4.2 Git for Windows

```json
{
  "os": "windows",
  "architectures": ["x64", "arm64"],
  "releases": {
    "type": "github-releases",
    "repository": "git-for-windows/git",
    "channel": "stable"
  },
  "artifact": {
    "selector": "git-windows-installer"
  },
  "installer": {
    "type": "native-installer",
    "format": "exe"
  }
}
```

流程为：

```text
GitHubReleaseProvider
  -> ComponentRelease[]
  -> GitWindowsInstallerSelector
  -> exact exe artifact
  -> InstallPlan
  -> NativeInstallerProvider
```

如果 Git for Windows 的资产命名能够稳定、安全地由通用声明式 selector 表达，则应优先使用 `pattern`，不保留专用 selector。

### 4.3 Mihomo / FRP

这类组件通常可以共享 `github-releases`：

```json
{
  "releases": {
    "type": "github-releases",
    "repository": "MetaCubeX/mihomo",
    "channel": "stable"
  },
  "artifact": {
    "selector": "pattern",
    "artifactRule": "mihomo-windows-{arch}"
  },
  "installer": {
    "type": "archive"
  }
}
```

这里的 pattern 是**结构化、受限的资产匹配规则**，不是任意 regex/script 执行环境。具体允许的占位符和最大复杂度必须由 schema 定义。

### 4.4 Nginx Windows

如果 nginx.org 的官方页面仍是当前产品认可的版本发现方式，可以声明：

```json
{
  "os": "windows",
  "architectures": ["x64"],
  "releases": {
    "type": "nginx-official",
    "channel": "stable"
  },
  "artifact": {
    "selector": "nginx-windows-zip"
  },
  "installer": {
    "type": "archive"
  }
}
```

`NginxOfficialReleaseProvider` 可以复用当前已有 nginx.org 解析实现。特殊代码留在 provider 内，而不是留在 Component Center UI 或组件 ID switch 中。

## 5. Provider Registry

manifest 只能引用 Host 已注册、受信任的 Provider：

```csharp
public sealed class ComponentReleaseProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IComponentReleaseProvider> _providers;

    public IComponentReleaseProvider Resolve(string type)
        => _providers.TryGetValue(type, out var provider)
            ? provider
            : throw new ComponentProviderUnavailableException(type);
}
```

Artifact Selector 和 Installer Provider 使用同样的 allow-list/registry 模型。

这意味着：

- 新增一个使用现有 GitHub Releases 规则的软件：通常只增加 manifest；
- 新增一个新的受支持包管理器：增加一个通用 Release/Installer Provider；
- 新增一个具有特殊官方版本页的软件：必要时增加一个专用 Release Provider；
- 不允许 manifest 下载或动态加载未知 C# provider 代码。

第三方 provider/plugin 若未来需要支持，必须进入独立的代码信任、签名、隔离和权限设计，不属于普通 Catalog manifest 的能力。

## 6. 不允许把 Parser 变成脚本运行时

普通 manifest 不允许：

```json
{
  "releases": {
    "url": "https://example.com/downloads",
    "parser": "javascript ...",
    "regex": ".*"
  }
}
```

也不允许通过 manifest 提供 shell、PowerShell、JavaScript、C# expression 或其他可执行解析逻辑。

原因：如果 Catalog 可以携带任意版本解析代码，那么声明式 Catalog 实际上会退化成远程代码执行/plugin 系统，并绕过 Provider Registry、Package Store 信任和 Helper 的类型化边界。

对于通用 HTTP 元数据，只接受 RelaxKonOS 已定义的有限 schema；对于 HTML 等特殊上游格式，由 RelaxKonOS 内置 provider 解析。

## 7. Release Provider 与 Source/Trust 的边界

`Release Provider` 回答“怎样从一个已允许的上游获得版本元数据”，但它不能自行扩大信任范围。

例如 `github-releases` 必须使用 manifest/受保护配置中已经批准的固定 repository；不能跟随 release 文本中的任意仓库或下载地址。`nginx-official` 只能访问其实现允许的官方站点和路径。APT 只能使用 Host 当前已批准的仓库集合。

Release Provider 输出的 artifact 仍要经过：

```text
source identity
origin/repository policy
platform/architecture match
digest/signature when available
redirect policy
size limit
revocation / anti-rollback when applicable
```

版本“能被发现”不等于版本“被允许安装”。

## 8. InstallPlan 的新增冻结字段

在原 Component Catalog 设计基础上，InstallPlan 至少还应冻结：

```text
releaseProviderType
releaseId
version
channel
artifactSelectorType
artifactId / package identity
artifact source identity
artifact URL/reference after policy resolution
artifact digest/signature when available
platform/architecture
installerProviderType
```

执行阶段不得重新查询 `latest`，也不得重新运行版本发现后静默选择另一个 asset。上游 metadata 或候选包变化导致冻结身份失效时，按现有领域规则重新规划或拒绝执行。

## 9. 与现有实现的迁移关系

现有代码已经存在多种版本发现方式，不应重写后再接入：

- Windows Nginx 当前官方版本列表解析 -> `NginxOfficialReleaseProvider`；
- Mihomo / FRP 当前固定 GitHub Releases 发现 -> 提取为通用 `GitHubReleaseProvider`；
- Linux APT candidate/version resolution -> `AptReleaseProvider`；
- Package Store / `.roapp` / `.rkmodule` 目录 -> `CatalogReleaseProvider`。

迁移顺序应优先**包裹和抽取现有成熟实现**，保持现有信任、缓存、分页、代理、超时、平台映射和领域测试，不为了接口统一重新实现网络解析。

## 10. 更新后的 Phase 1

声明式 Component Catalog 的 Phase 1 应补充：

1. 定义 `IComponentReleaseProvider`、`ComponentRelease`、`ComponentArtifact`；
2. 定义受限 `IComponentArtifactSelector`；
3. 实现 Provider Registry；
4. 从现有实现提取 `apt`、`github-releases`、`nginx-official`、`catalog/static`；
5. Installer Provider 改为消费已经冻结的 release/artifact，不负责发现 latest；
6. 将 Git/Nginx/Mihomo/FRP 作为首批迁移样例，验证“通用 provider + 少量特殊 provider”模型；
7. 测试 provider 不可用、上游返回异常、资产歧义、平台不匹配、仓库/origin 越界、计划后版本变化和执行阶段重新解析 latest 等拒绝路径。

最终希望达到：**新增普通组件通常只需要组合 Manifest 中已有的 Release Provider + Artifact Selector + Installer Provider；只有新增一种真正不同的上游协议或领域语义时才需要增加代码。**