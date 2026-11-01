# RelaxKonOS 声明式组件目录与安装提供者设计

> 状态：设计提案，2026-11-01。
>
> 目标：将组件中心从“编译期写死组件 + 按组件编写安装入口”逐步升级为声明式组件部署入口，使新增宿主组件、官方 `.roapp` 或后续可信第三方组件不必修改组件中心自身代码。同时保持现有领域安装器、TaskOrchestration、Helper 权限边界和安装台账为权威实现。

## 1. 核心决策

组件中心只理解通用的组件描述、平台目标和安装机制，不理解 Git、Docker、Nginx 等具体产品的特殊安装步骤。

目标流程：

```text
Catalog
  -> Component Manifest
  -> Compatibility Resolver
  -> Install Plan
  -> Policy / Trust
  -> Installer Provider
  -> existing domain installer / TaskOrchestration / Privileged Helper
  -> Installed State
```

关键原则：

1. **具体软件是数据，安装机制是代码。** 新组件优先通过 manifest 声明，而不是新增 `if (componentId == ...)`。
2. **Catalog 来源与 Catalog 格式分离。** v1 首先支持本地目录/相对文件；HTTP(S) 是后续可选来源，不是运行组件中心所必需的网站服务。
3. **相对引用是第一等能力。** 同一套目录可直接放在安装包、本地磁盘、离线介质，未来也可以原样发布到 HTTPS 静态站点。
4. **平台支持由 manifest 显式声明。** Linux 可选择 APT/system package provider，Windows 可选择官方下载安装器/归档 provider；组件中心不根据组件名称猜测平台。
5. **`.roapp` 是一种 provider/包类型，不另建一套发现机制。** 桌面应用和宿主组件可以共享 Catalog/Source 层，但安装事务仍由各自安装器负责。
6. **manifest 不获得任意命令执行能力。** 默认不接受 `shell`、`powershell`、`curl | sh` 等任意脚本。高权限操作必须映射到已有的类型化 Helper/领域操作。
7. **先计划，后执行。** provider 必须能生成可审查的 InstallPlan；UI 展示来源、版本、平台、权限、下载和变更后再批准执行。
8. **已安装事实独立于当前 Catalog。** Catalog 被删除、离线或版本变化后，仍必须能够识别已安装组件，并按已保存的安装事实执行允许的修复/卸载/恢复。

## 2. Catalog 不要求网站

第一阶段推荐随 RelaxKonOS 安装包携带本地目录：

```text
ComponentCatalog/
  catalog.json
  manifests/
    git.json
    nginx.json
    docker.json
  packages/
    example.roapp
```

`catalog.json` 只负责索引：

```json
{
  "schemaVersion": 1,
  "components": [
    {
      "id": "relaxkon.git",
      "manifest": "./manifests/git.json"
    },
    {
      "id": "relaxkon.example",
      "manifest": "./manifests/example.json"
    }
  ]
}
```

这里的 `manifest` 是**相对于当前 Catalog 所在位置的引用**，不是 HTTP URL。

例如本地 Catalog 位于：

```text
/opt/relaxkonos/catalog/catalog.json
```

则 `./manifests/git.json` 解析为：

```text
/opt/relaxkonos/catalog/manifests/git.json
```

未来如果同一目录原样发布为：

```text
https://packages.relaxkon.com/catalog.json
```

相同引用自然解析为：

```text
https://packages.relaxkon.com/manifests/git.json
```

因此前期不需要为组件中心维护 `packages.relaxkon.com`；后期增加 HTTP(S) 只是新增 Source Adapter，不改变 manifest 或安装器模型。

## 3. Source 与 Catalog 分层

建议抽象：

```csharp
public interface IComponentCatalogSource
{
    string SourceId { get; }

    Task<ComponentCatalog> GetCatalogAsync(
        CancellationToken cancellationToken = default);

    Task<Stream> OpenAsync(
        string reference,
        CancellationToken cancellationToken = default);
}
```

首版：

- `LocalCatalogSource`：安装包、本地目录、管理员明确选择的离线目录。

后续可选：

- `HttpCatalogSource`：HTTPS 静态目录或服务。
- 官方/社区签名 Source：复用 `RelaxKonOS.PackageStore.Design.md` 的发布者、公钥、撤销、反回退和缓存规则。

Source 负责**取得字节和解析引用**；Catalog/Manifest 负责**描述组件**；Installer Provider 负责**把已批准计划交给现有安装后端**。三者不能互相越权。

### 3.1 引用解析规则

引用分两类：

- 相对引用：相对于声明它的 Catalog 或 Manifest 位置解析。
- 绝对引用：只有对应 Source/Policy 明确允许时才接受。

本地 Source 必须：

- 规范化路径；
- 拒绝逃出 Catalog 根目录的 `..`；
- 拒绝符号链接/reparse 等导致的根目录逃逸；
- 不把 manifest 中的任意本机绝对路径交给 Helper。

HTTP Source 必须沿用 Package Store 的 HTTPS、origin、重定向、大小限制、签名和撤销策略，不能因为 URI 抽象而降低现有安全边界。

## 4. Component Manifest

建议的通用结构：

```json
{
  "schemaVersion": 1,
  "id": "relaxkon.git",
  "name": "Git",
  "summary": "Distributed version control system",
  "category": "development",
  "targets": [
    {
      "os": "linux",
      "architectures": ["x64", "arm64"],
      "installer": {
        "type": "apt",
        "packages": ["git"]
      }
    },
    {
      "os": "windows",
      "architectures": ["x64", "arm64"],
      "installer": {
        "type": "download",
        "source": "official",
        "artifact": "git-installer",
        "format": "exe"
      }
    }
  ]
}
```

manifest 声明的是“RelaxKonOS 支持怎样安装这个组件”，不等于上游软件所有理论支持平台。

对于本地 `.roapp`：

```json
{
  "schemaVersion": 1,
  "id": "relaxkon.example",
  "name": "Example",
  "targets": [
    {
      "os": ["windows", "linux", "macos"],
      "installer": {
        "type": "roapp",
        "artifact": "../packages/example.roapp",
        "sha256": "..."
      }
    }
  ]
}
```

相对 artifact 引用遵循与 manifest 相同的基址规则。

## 5. Compatibility Resolver

Resolver 根据目标 Host 的实际环境选择 target，而不是 UI 自己判断：

```text
OS
architecture
Linux distribution/version
package manager capability
service host capability
required RelaxKonOS/Host API
provider availability
policy/trust
```

输出必须区分：

- Supported：存在可执行 target；
- UnsupportedPlatform：manifest 明确没有当前平台；
- ProviderUnavailable：平台支持，但当前 Host 没有对应 provider/capability；
- PolicyBlocked：来源或权限策略阻止；
- Unknown：环境查询失败，不能降级成“未安装”。

这与当前 sing-box “只声明 Linux、组件中心通用过滤”的方向一致，并将其推广为所有组件的统一规则。

## 6. Installer Provider

provider 是安装机制适配器，不是组件适配器：

```csharp
public interface IInstallerProvider
{
    string Type { get; }

    bool CanHandle(
        InstallTarget target,
        ComponentHostEnvironment environment);

    Task<InstallPlan> PlanAsync(
        ComponentManifest component,
        InstallTarget target,
        CancellationToken cancellationToken = default);

    Task<InstallResult> InstallAsync(
        InstallPlan plan,
        CancellationToken cancellationToken = default);
}
```

第一阶段建议只实现/抽象现有能力：

| Provider | 用途 |
| --- | --- |
| `roapp` | RelaxKonOS 桌面应用包；交给现有 AppPackageInstallerService/安装器 |
| `apt` | Debian/Ubuntu 系统包；交给现有 package ownership + Helper/TaskOrchestration 路径 |
| `download` | Windows 等平台的官方下载安装器；必须由可信来源适配器解析制品 |
| `archive` | 已有受管归档运行时；交给 ManagedRuntimes 领域安装器 |

后续再按实际需求增加 `winget`、`brew`、`dnf`、`flatpak` 等。不要为了“看起来通用”在 v1 同时实现所有包管理器。

### 6.1 不允许 manifest 自带任意脚本

错误方向：

```json
{
  "installer": {
    "type": "shell",
    "command": "curl https://example.com/install.sh | sudo sh"
  }
}
```

正确方向是结构化操作：

```json
{
  "installer": {
    "type": "apt",
    "packages": ["git"]
  }
}
```

最终由受信任 provider 转换成现有类型化安装计划；manifest 不能决定 Helper 执行什么任意命令。

## 7. InstallPlan 是安全与 UX 边界

Catalog/Manifest 不能直接触发安装。Resolver + Provider 先生成冻结计划：

```text
componentId
componentVersion / releaseId
sourceId
selected target
provider type
platform/architecture
artifact/package identity
expected digest where available
packages/dependencies
privileged operations
services/files affected
estimated download/disk impact where available
restart impact
```

计划随后进入现有的 Policy/Trust、审批、TaskOrchestration 和领域安装事务。

Component Center 展示计划，而不是展示 manifest 中的一段“将执行命令”。执行前如果仓库候选、制品摘要、Host 身份或依赖影响变化，必须重新规划或按现有领域规则拒绝。

## 8. Installed State 与 Catalog 解耦

安装事实不能依赖 Catalog 当前仍然存在。可以沿用现有领域台账；若需要统一索引，建议只保存领域权威状态的快照/引用，例如：

```json
{
  "componentId": "relaxkon.git",
  "version": "2.51.0",
  "provider": "apt",
  "sourceId": "ubuntu",
  "installedAt": "2026-11-01T12:00:00Z",
  "installationIdentity": {
    "packages": ["git"]
  }
}
```

统一索引不是第二套安装权威。APT 的真实 dpkg 身份、受管归档 revision、`.roapp` 安装状态等仍由已有领域实现验证。

这样即使：

- Catalog 文件被删除；
- 网络来源离线；
- 新 Catalog 不再列出旧版本；
- 第三方来源被禁用；

组件中心仍能正确显示已安装事实，并按策略提供卸载、恢复或受限修复。

## 9. 与现有 Package Store / Managed Runtimes 的关系

本设计不替换现有文档，而是补上“组件声明与安装机制选择”这一层：

- `RelaxKonOS.ComponentCenter.Design.md`：拥有统一 UI、组件/工作负载和生命周期入口；
- `RelaxKonOS.ManagedRuntimes.Design.md`：继续拥有受管运行时的版本、所有权、事务和领域适配；
- `RelaxKonOS.PackageStore.Design.md`：继续拥有在线官方/社区来源、签名目录、下载、撤销和反回退；
- 本文：拥有声明式 Component Manifest、Local Catalog、引用解析、Compatibility Resolver 与 Installer Provider 边界。

因此 Package Store 的 HTTP(S) 来源是**可选的远程 Source 实现**，不是 Component Catalog 的前置条件。

## 10. 分阶段实施

### Phase 1：本地声明式目录

- 定义 `ComponentCatalog` / `ComponentManifest` schema；
- 实现 `LocalCatalogSource` 和安全相对引用解析；
- 将现有编译期组件逐步迁移为本地 manifest；
- 实现 `CompatibilityResolver`；
- 抽象 `roapp`、`apt`、`download`、`archive` provider，优先复用现有领域安装器；
- Component Center 从解析后的 Catalog 生成组件列表，不再要求每个新增组件修改 UI/目录源码。

### Phase 2：可选 HTTP(S) Source

- 增加 `HttpCatalogSource`；
- 复用 Package Store 的官方签名、公钥、目录 sequence、有效期、撤销、反回退、缓存和下载限制；
- 允许官方新增组件/`.roapp` 后由目录刷新发现，而无需升级 RelaxKonOS；
- 本地 Catalog 继续作为离线/安装介质/恢复来源，不废弃。

### Phase 3：可信第三方来源

- 用户显式添加来源和固定发布者/公钥；
- publisher/trust UI；
- 权限与 provider allow-list；
- 第三方默认只能使用声明式 provider；
- 任意脚本型 provider 若未来确有需求，必须作为单独的高风险能力设计，不属于默认 manifest 契约。

## 11. 迁移要求

迁移期间不要新建第二套组件中心：

1. 现有硬编码目录逐项映射为 manifest；
2. provider 适配现有领域 API，而不是复制 Nginx/Mihomo/APT 安装逻辑；
3. 一个组件迁移完成后删除对应的目录硬编码/名称特例；
4. 测试同时覆盖 manifest 解析、路径逃逸、平台选择、provider 缺失、来源不可用、计划变化和现有领域回归；
5. HTTP Source 未实现前，任何功能不得因为没有网站而不可用。

最终目标不是“用 JSON 重写现有 switch”，而是让新增普通组件主要成为**增加 manifest + 必要的通用 provider 能力**，只有真正具有新领域语义的软件才需要新增领域代码。