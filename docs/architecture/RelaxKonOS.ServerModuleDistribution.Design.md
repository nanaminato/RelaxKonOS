# 服务端独立模块分发与 v1/v2 宿主设计

> 状态：设计，2026-10-31。v1 首个可运行切片已实现：离线/在线安装官方 `.rkmodule` v1、不可变版本目录、`installed.json` 安装事实、进程内独立加载器、重启核实、启动失败回退/隔离、组件中心安装/回退 UI，以及已接纳目录的模块搜索/下载；v2 ModuleHost、完整商店服务、提权 Broker 未实现。本文其余内容仍是目标设计，不能按已实现功能读取。
>
> 范围：服务端模块的独立打包、信任、安装、更新、回退、加载、卸载，以及 v1/v2 两种宿主模式。桌面 `.roapp` 的包格式仍由[开发者模式](../development/RelaxKonOS.DeveloperMode.md)拥有；商店来源、搜索和下载的统一契约由[包商店来源设计](../platform/RelaxKonOS.PackageStore.Design.md)拥有；第三方模块的提权调用由[模块提权 Broker 设计](RelaxKonOS.ServerModulePrivilegeBroker.Design.md)拥有。
>
> 本文不表示模块安装器、第三方模块、进程隔离、热卸载或在线商店已经实现。实现进度只按文末跟踪项和实际验收记录关闭。

## 1. 结论与边界

本设计采用以下产品决策：

1. **v1 只承载官方模块，在 Server 进程内运行。**
2. **v2 增加第三方 ModuleHost；官方模块继续在 Server 进程内运行，不移入 ModuleHost。**
3. **不交付“第三方模块在 Server 进程内运行”的 v1 变体。**
4. 目标包契约是 `schemaVersion=2`；v1 首个实现复用当前 `.rkmodule` v1 候选契约（`schemaVersion=1`），v2 阶段升级到唯一当前契约，不保留双解析。v1/v2 的差异是**宿主模式、信任策略和分发范围**，不是两套长期并存的解析器。
5. 安装、更新、回退、移除都通过“计划 → 确认 → 应用 → 重启 → 核实”的持久事务完成，首版不支持热加载或运行中卸载。
6. 模块文件放在持久数据根的不可变版本目录；Server 版本目录只放 Host 本身。
7. 官方模块必须通过官方签名目录验证；第三方模块由用户显式信任，默认标记为未验证或社区发布者，不因安装而获得官方身份。

不在本文范围内：

- 桌面 `.roapp` 的应用商店与安装实现；由[包商店来源设计](../platform/RelaxKonOS.PackageStore.Design.md)和桌面安装器文档分别拥有。
- Android 的模块/应用界面与平台边界；由 [Android 文档](../../Client/RelaxKonOS.Client.Android/docs/design/ApplicationPackages.Design.md)独立拥有。
- 受管软件（Nginx、Docker、Mihomo、sing-box 等）的安装、版本和回退；仍由[受管运行时设计](../platform/RelaxKonOS.ManagedRuntimes.Design.md)拥有。模块安装不得顺手安装或启动这些软件。

### 1.1 为什么不交付进程内第三方模块

把第三方模块放进 Server 进程，等于把 Server 的完整信任交给模块：它可以解析 Host 的 DI、读取配置和数据库连接、拿到 JWT/数据保护密钥、调用 `IPrivilegedOperationTransport`，甚至直接用 Server 的 OS 身份连接 Helper 的 Unix socket 或命名管道。此时“每次确认提权”只能约束诚实模块，恶意模块可以直接绕过。

因此本设计不把“第三方模块 + 进程内 + 确认框”包装成安全模型。需要第三方模块时，直接进入 v2 ModuleHost；v1 只用于官方签名模块，保持简单。

## 2. 当前基线

| 事实 | 当前状态 |
| --- | --- |
| 官方目录 | 编译期 9 个模块：Terminal、Firewall、SystemPerformance、Git、Tailscale、Docker、Certificate、WebServer、Proxy |
| 启停 | `Modules:<ConfigurationName>:Enabled`，重启生效；空配置默认全部启用 |
| 包校验 | `.rkmodule` v1 的清单、签名目录、归档、路径、摘要、RID、Host API 纯校验 |
| 维护命令 | `verify-package`、`catalog-status`、`accept-catalog`、`install-package`、`list-installed`、`remove-installed`；安装为离线管理员命令 |
| 管理 API | 只读清单、启停/策略计划、操作查询；在线包动作与版本目录 API 仍返回不支持 |
| 安装 | v1 离线/在线安装、不可变版本目录、`installed.json`、进程内独立加载器、重启核实和启动失败回退/隔离已实现；完整商店服务与第三方 ModuleHost 未实现 |
| 第三方 | 无包、无 ModuleHost、无权限模型 |

相关入口：

- [`ServerModules.Catalog.cs`](../../RelaxKonOS.Server/Startup/ServerModules.Catalog.cs)
- [`ModulePackageMaintenanceCommand.cs`](../../RelaxKonOS.Server/Startup/ModulePackageMaintenanceCommand.cs)
- [`RelaxKonOS.ServerModulePackage.Format.md`](RelaxKonOS.ServerModulePackage.Format.md)
- [`RelaxKonOS.ServerModuleManagement.Design.md`](RelaxKonOS.ServerModuleManagement.Design.md)

## 3. v1 与 v2

### 3.1 对照

| 维度 | v1：官方独立模块 | v2：第三方 ModuleHost |
| --- | --- | --- |
| 模块来源 | 官方签名目录 | 官方/社区/用户添加的商店来源，或本地包 |
| 信任 | 官方发布公钥 + 撤销 | 用户显式信任；签名可选但推荐 |
| 运行位置 | Server 进程内，独立 `AssemblyLoadContext` | 独立 `RelaxKonOS.ModuleHost` 进程 |
| 访问 Server 数据 | 可以（官方可信） | 不可以；只通过 ModuleHost IPC/broker |
| 访问 Helper | 走 Core 的现有通道 | 不可以；只能通过提权 Broker |
| 提权模型 | 现有 `IHostElevationSessionStore` | 每次确认默认；可按模块配置 |
| 失败影响 | 可能影响 Server 进程 | 只影响 ModuleHost；Server 保持运行 |
| 安装方式 | 计划 → 确认 → 重启 | 计划 → 信任 → 安装 → 重启 ModuleHost |
| 首版目标 | 证明安装/回退闭环 | 证明第三方边界与提权确认 |

v2 不把官方模块迁出 Server：官方模块继续走 v1 的进程内路径，v2 只是新增第三方 ModuleHost、商店信任和提权 Broker。

### 3.2 迁移关系

v1 是产品阶段，不是永久兼容层。v1 当前使用 `schemaVersion=1` 候选契约；v2 目标发布时：

- 官方模块升级到同一个 `schemaVersion=2` 契约，`runtime=in-process`；
- 第三方模块使用 `runtime=module-host`；
- Host 只保留一个当前解析器，不新增旧格式别名、双格式解析或回退路由；
- 如果 v1 期间还存在当前候选包 v1 的产物，按 `schemaVersion=2` 重新签发/重新验证，不做静默降级或“看起来还在运行”的假兼容。

## 4. 目标包契约（schemaVersion=2）

> v1 当前安装器读取 `schemaVersion=1`（见 §8.3）；本节是 v2 目标契约，尚未被 v1 安装器读取。

### 4.1 归档结构

```text
manifest.json
server/<rid>/<entry assembly>
server/<rid>/<private dependencies>
assets/...
```

约束沿用现有 `.rkmodule` 的安全规则，并补充 v2 字段：

- 根部只允许 `manifest.json`；其余文件必须由清单逐一声明。
- 拒绝目录条目、符号链接、设备、Windows reparse 标记、未声明文件、大小写冲突、绝对路径、`..`、空段、尾点和保留设备名。
- 只允许 ASCII 路径字符和 `/`；每个路径必须归属于该模块的不可变版本根。
- 读取清单和校验不得执行包代码。
- 包内不能携带可信公钥或自行宣称官方授权。

### 4.2 manifest 字段

```json
{
  "schemaVersion": 2,
  "packageKind": "server-module",
  "runtime": "in-process",
  "moduleId": "relaxkonos.example.hello",
  "version": "1.0.0",
  "publisher": {
    "id": "relaxkonos",
    "name": "RelaxKonOS"
  },
  "purpose": "official",
  "rid": "linux-x64",
  "hostApiMinimum": "1.0.0",
  "hostApiMaximumExclusive": "2.0.0",
  "entryAssembly": "server/linux-x64/RelaxKonOS.Example.Hello.dll",
  "entryType": "RelaxKonOS.Example.Hello.HelloModule",
  "buildId": "20261031.1",
  "sourceCommit": "0000000000000000000000000000000000000000",
  "dependencies": [],
  "capabilities": [],
  "privileges": [],
  "ui": {
    "name": "Hello",
    "summary": "最小官方模块示例",
    "description": "演示 v1 模块的注册、路由和安装。",
    "homepage": "https://relaxkon.com/",
    "license": "MIT"
  },
  "files": [
    {
      "path": "server/linux-x64/RelaxKonOS.Example.Hello.dll",
      "length": 12345,
      "sha256": "…"
    }
  ]
}
```

字段语义：

| 字段 | 语义 |
| --- | --- |
| `runtime` | `in-process` 仅允许 `purpose=official`；`module-host` 用于 v2 第三方 |
| `moduleId` | 稳定模块 ID；官方使用 `relaxkonos.*`，第三方不得使用该保留前缀 |
| `purpose` | 发布者的声明，不是授权；Host 信任策略决定实际身份 |
| `rid` | 单一 RID；必须与目标 Host 完全一致 |
| `hostApiMinimum` / `hostApiMaximumExclusive` | Host API 半开区间 |
| `entryAssembly` / `entryType` | 入口；必须位于版本根内 |
| `dependencies` | 模块 ID + 版本区间；首版只支持硬依赖 |
| `capabilities` | 可向客户端公布的 Host 能力；第三方不能新增 Host 未定义的稳定能力 |
| `privileges` | 申请的提权操作；安装时展示，运行时由 Broker 再校验 |
| `ui` | 商店/安装页展示元数据，不参与信任判断 |
| `files` | 每个文件的长度与 SHA-256；安装后再次核对 |

### 4.3 签名与目录

- 官方：`.rkmodule` 由官方签名目录（`SignedPackageCatalog`）绑定 releaseId、归档摘要、清单摘要和撤销集合。
- 第三方：可以使用发布者签名目录，也可以本地导入；本地导入必须记录用户信任的摘要。
- 签名证明来源/完整性，不证明代码安全。
- 目录、策略、历史、撤销和反回退规则沿用[现有包契约](RelaxKonOS.ServerModulePackage.Format.md)的签名目录模型，并沿用 `packageKind=server-module`。

## 5. 存储与状态

### 5.1 目录

```text
<data-root>/module-management/
  state.json                  # desired/effective/operations；模块管理唯一期望权威
  installed.json              # 安装事实：已安装版本、摘要、入口与能力
  package-catalogs.json       # 已接纳的签名目录与信任历史
  packages/
    <moduleId>/
      <version>/              # 不可变版本目录
  staging/
    <operationId>/            # 有界暂存，安装结束后清理
  quarantine/                 # 失败/待恢复的候选，不自动删除
```

模块目录必须在持久数据根，不能放在 `/opt/relaxkonos/versions/<server-version>/server` 内；否则 Server 升级会丢失模块。

系统模式建议由 Server 服务账户拥有 `module-management`，目录 `0700`、文件 `0600`；如果后续要求模块文件 root 所有，再通过受控 Helper 的文件操作迁移，不在 v1 首版引入新的提权面。v2 的 ModuleHost 只获得自己模块版本目录的只读遍历/读取权限，不能写模块文件，也不能读取其他模块目录。

### 5.2 installed.json 与 state.json

`installed.json` 只记录“磁盘上装了什么”，不表示启用或生效。当前 v1 记录形状：

```json
{
  "schemaVersion": 1,
  "modules": [
    {
      "moduleId": "relaxkonos.example.hello",
      "version": "1.0.0",
      "releaseId": "…",
      "sourceId": "official",
      "publisherId": "relaxkonos",
      "artifactSha256": "…",
      "manifestSha256": "…",
      "packagePath": "packages/relaxkonos.example.hello/1.0.0",
      "entryAssembly": "server/linux-x64/RelaxKonOS.Example.Hello.dll",
      "entryType": "RelaxKonOS.Example.Hello.HelloModule",
      "capabilities": [],
      "requires": [],
      "installedAt": "2026-10-31T08:00:00Z"
    }
  ]
}
```

`state.json` 继续拥有期望启用集合、驻留策略、冻结计划、操作回执和进程代次；它不复制 `installed.json` 的安装事实。两者分别由模块安装器和模块管理写者拥有，读取失败都按未知处理。

每个不可变版本目录还写入一份 `install.json`，保存该版本的 releaseId、artifact/manifest 摘要、入口、能力和依赖。`list-versions` 与 `rollback-installed` 只读这份元数据，不加载程序集；回退前重新核对文件哈希。

约束：

- `installed.json` 写入使用临时文件 + `Flush(true)` + 原子替换；单文档上限 4 MiB、最多 1024 个模块。
- `state.json` 的期望集合可以引用已安装模块；安装本身不自动把它加入启用集合。
- 操作记录包含 operationId、幂等键、操作者、目标、摘要、修订、阶段和终态。
- 历史上限和状态文件上限沿用现有模块管理设计；达到上限拒绝新操作，不静默裁剪。

## 6. 安装事务

### 6.1 状态机

```text
Prepared -> Verified -> AwaitingApproval -> Staged
         -> AwaitingRestart -> Verifying -> Succeeded
         -> Failed / RecoveryRequired
```

### 6.2 流程

1. **接收候选**：本地文件、已配置来源或上传；进入有界暂存，计算归档摘要。
2. **验证**：签名目录/信任策略、manifest、RID、Host API、依赖、能力、提权声明、路径、文件摘要。
3. **检查依赖**：`Requires` 必须有已安装且启用的模块；版本区间满足；反向依赖、循环和重复 ID 拒绝。
4. **生成计划**：模块、版本、依赖闭包、能力变化、提权项、重启影响、磁盘占用、是否信任发布者。
5. **用户确认**：安装/信任/提权预览；管理员权限；官方模块显示签名，第三方显示未验证/社区发布者。
6. **落盘**：解包到 `staging/<operationId>`，逐文件核对摘要，再原子移动到 `packages/<moduleId>/<version>`。
7. **提交期望状态**：更新 `state.json`，标记 `AwaitingRestart`。
8. **重启**：由部署宿主执行；Server 不假设自己能可靠重启。
9. **启动核实**：加载模块、映射路由、发布能力；`ApplicationStarted` 后核对 effective 集合和版本，写 `Succeeded`。
10. **失败处理**：记录阶段和问题码；候选启动失败时回退到 `lastKnownGood`，或进入恢复模式；不把“进程重启成功”当成“模块已生效”。

### 6.3 幂等与并发

- 每次请求必须带 `expectedRevision` 和 `idempotencyKey`。
- 相同键相同内容返回原回执；相同键不同内容冲突。
- 安装、更新、启停、回退共用服务器级锁和模块锁，固定加锁顺序。
- 外部修改 `state.json` 导致修订冲突，要求重新预览。
- 运行中请求不能被旧计划覆盖；旧计划过期或目标被取代时明确失败。

> v1 当前实现完成离线 `install-package` / `rollback-installed`、`installed.json` 安装事实、每版本 `install.json`、`package-operations.json` 操作回执和进程内加载。安装/回退不会自动修改 `state.json` 的期望启用集合；操作先记为 `AwaitingRestart`，`ApplicationStarted` 后按 `installed.json` 的精确版本/摘要核对为 `Succeeded` 或 `Failed`。仍不提供在线审批、自动启动失败回退或自动重启。

## 7. 启动、加载与回退

### 7.1 启动顺序

1. 读取 `state.json`、`installed.json` 和已接纳目录。
2. 验证已安装模块的文件、摘要、Host API、RID、依赖和信任状态；读不到按 `RecoveryRequired` 处理。
3. 计算本次启动的不可变模块快照；依赖拓扑排序。
4. 写入 `startup-attempt` 标记（进程代次、期望集合、修订）。
5. 按顺序注册服务，构建 Host，映射 Endpoint/Hub。
6. `ApplicationStarted` 后清除标记，核对 effective 集合并写回执。
7. 如果启动失败，记录阶段；下次启动看到未清除标记时增加失败计数。连续失败达到阈值后禁用新集合或进入恢复宿主，不自动反复重试。

### 7.2 v1 加载

- 每个模块使用独立 `AssemblyLoadContext`，但仍在 Server 进程内。
- 共享契约 `RelaxKonOS.ServerModule.Abstractions` 由 Host 提供，模块不得私有携带同名类型。
- 使用 `AssemblyDependencyResolver` 解析模块私有依赖和 native assets。
- 入口程序集和类型必须位于模块版本根内；只允许清单声明的路径。
- 捕获加载、DI 注册、路由映射异常，标记 `Faulted`，不发布该模块能力。
- 首版不支持运行中卸载；更新/移除通过重启生效。

### 7.3 v2 加载

- `RelaxKonOS.ModuleHost` 独立进程，使用专用 OS 账户/ACL，不加入 Helper socket 组，不读取 Server 数据目录。
- ModuleHost 从 `module-management/packages` 读取第三方模块，使用自己的 `AssemblyLoadContext` 和 DI 容器。
- 模块只能通过版本化 IPC 访问 Host 提供的窄能力：HTTP 路由注册、受限存储、日志、提权 Broker。
- Server 通过反向代理或 IPC 把已认证请求路由到 ModuleHost；具体传输在 v2 实施项中冻结。
- ModuleHost 崩溃/重启不影响 Server；Server 显示模块 `Faulted`，不伪造成功。

### 7.4 回退

- 每个模块保留上一个已验证版本，直到新版本通过启动核实。
- 回退只允许仍可信、RID/Host API 兼容、数据 schema 兼容的版本。
- 回退是显式计划，不需要用户重新下载；不能凭客户端缓存安装新代码。
- 卸载默认保留模块数据和配置；删除数据是独立计划。

> v1 首版保留旧版本目录，并实现启动失败恢复：有 `previousVersion` 的更新恢复旧版本；首次安装没有历史版本时隔离安装记录，不再加载其程序集。尚未实现自动重启或启动后的健康探测。

## 8. v1：如何添加一个官方模块

### 8.1 工程结构

```text
examples/ServerModules/HelloOfficial/
  HelloOfficial.csproj
  HelloModule.cs
  manifest.json
```

`HelloOfficial.csproj` 只引用 Host 提供的共享契约，不复制 Server 内部类型：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\Shared\RelaxKonOS.ServerModule.Abstractions\RelaxKonOS.ServerModule.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

> 共享抽象项目 `Shared/RelaxKonOS.ServerModule.Abstractions` 已存在；模块只引用它，不引用 `RelaxKonOS.Server`。

### 8.2 入口

```csharp
using RelaxKonOS.ServerModule.Abstractions;

namespace RelaxKonOS.Example.Hello;

public sealed class HelloModule : IInstalledServerModule
{
    public string Id => "relaxkonos.example.hello";
    public IReadOnlyList<string> Capabilities => [];
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(IServiceCollection services, InstalledModuleContext context)
    {
        services.AddSingleton<HelloService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, InstalledModuleContext context)
    {
        endpoints.MapGet("/api/v1.0/example/hello", (HelloService service) => service.GetAsync())
            .RequireAuthorization();
    }
}
```

v1 的 `InstalledModuleContext` 只暴露配置、环境、内容根、模块数据目录、平台和存储提供程序；不提供已构建的 `WebApplication`，模块不能重排 Core 中间件。

### 8.3 manifest（当前 v1 实现）

```json
{
  "schemaVersion": 1,
  "packageKind": "server-module",
  "moduleId": "relaxkonos.example.hello",
  "releaseId": "relaxkonos.example.hello-1.0.0-linux-x64",
  "version": "1.0.0",
  "publisherId": "relaxkonos",
  "purpose": "official",
  "rid": "linux-x64",
  "hostApiMinimum": "1.0.0",
  "hostApiMaximumExclusive": "2.0.0",
  "entryAssembly": "server/linux-x64/RelaxKonOS.Example.Hello.dll",
  "entryType": "RelaxKonOS.Example.Hello.HelloModule",
  "buildId": "20261031.1",
  "sourceCommit": "0000000000000000000000000000000000000000",
  "dependencies": [],
  "capabilities": [],
  "files": [
    {
      "path": "server/linux-x64/RelaxKonOS.Example.Hello.dll",
      "length": 12345,
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    }
  ]
}
```

> 这是 v1 安装器当前接受的 `.rkmodule` 契约；`schemaVersion=2`、`runtime`、`privileges` 和 `ui` 是 v2 目标字段，尚未被 v1 安装器读取。

> 示例中的长度和摘要仅用于展示字段形状；实际值必须由打包器逐文件生成，空 `files` 或占位摘要不能通过校验。

### 8.4 发布与安装

1. `dotnet publish` 到 `server/<rid>/`。
2. 生成 manifest 的 `files` 长度和 SHA-256。
3. 用官方签名工具生成 release 条目和签名目录。
4. 把 `.rkmodule` 和签名目录放到官方来源。
5. 停止 Server，以管理员/root 执行 `accept-catalog` 持久接纳目录。
6. 执行 `install-package`；它只验证/解包并写入 `installed.json`，不改变启用集合。
7. 通过现有模块管理计划启用模块，由部署宿主重启 Server。
8. 启动后核对 effective 版本；当前 v1 没有自动回退，失败时由管理员重新启用旧版本/恢复。

当前 v1 命令：

```text
RelaxKonOS.Server modules pack-package \
  --publish-dir <dotnet publish输出目录> --output-dir <打包输出目录> \
  --module-id <模块ID> --version <x.y.z> --release-id <发布ID> \
  --publisher-id <发布者ID> --purpose official --rid <RID> \
  --entry-assembly <publish内相对路径> --entry-type <入口类型> \
  --build-id <构建ID> --source-commit <40或64位小写十六进制> \
  --source-id <来源ID> --sequence <目录序号> --artifact-origin https://<origin>/ \
  --private-key <ECDSA P-256 PKCS#8 PEM> --key-id <公钥ID> --validity-seconds <1..2678400>

RelaxKonOS.Server modules list-installed --content-root <绝对服务目录>
RelaxKonOS.Server modules list-versions --content-root <绝对服务目录> --module-id <模块ID>
RelaxKonOS.Server modules list-package-operations --content-root <绝对服务目录>
RelaxKonOS.Server modules rollback-installed --content-root <绝对服务目录> --module-id <模块ID> --version <目标版本>
RelaxKonOS.Server modules install-package --content-root <绝对服务目录> --source-id <已配置来源> --archive <本地.rkmodule> --release-id <精确发布ID>
RelaxKonOS.Server modules remove-installed --content-root <绝对服务目录> --module-id <模块ID>
```

`list-versions` 只报告不可变版本目录，不声称哪一个是 active；`list-package-operations` 只报告 install/rollback 回执；`rollback-installed` 在 Host 停止、管理员/root 下重新核对目标版本文件哈希，然后把 `installed.json` 的当前版本切回该版本。两者都不修改期望启用集合，不自动重启。

`pack-package` 只读 publish 目录，生成 `.rkmodule`、`catalog.json`、`manifest.json` 和 `public-key.spki`；私钥只用于本次签名，不写入输出或日志。`artifact-origin` 必须是根路径 HTTPS 地址，生成的下载地址必须落在该 origin 下。

`remove-installed` 默认保留 `packages/<id>/<version>` 文件；模块仍在期望启用集合中时拒绝移除。

v1 不接受第三方包，即使 manifest 写了 `purpose=community` 也会被 Host 信任策略拒绝。

## 9. v2：如何添加一个第三方模块

### 9.1 工程结构

```text
examples/ServerModules/HelloModuleHost/
  HelloModuleHost.csproj
  HelloModule.cs
  manifest.json
```

只引用 ModuleHost SDK，不引用 `RelaxKonOS.Server`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\Shared\RelaxKonOS.ModuleHost.Sdk\RelaxKonOS.ModuleHost.Sdk.csproj" />
  </ItemGroup>
</Project>
```

> `RelaxKonOS.ModuleHost.Sdk` 是拟议项目名，实施时冻结；本文示例不是当前可编译工程。

### 9.2 入口

```csharp
using RelaxKonOS.ModuleHost.Sdk;

namespace Example.Hello;

public sealed class HelloModule : IModuleHostEntry
{
    public string Id => "com.example.hello";

    public ValueTask ConfigureAsync(IModuleContext context, CancellationToken ct)
    {
        context.Http.MapGet("/hello", async http =>
        {
            var status = await context.Privileges.GetStatusAsync(ct);
            await http.WriteJsonAsync(new { message = "hello", status });
        });

        return ValueTask.CompletedTask;
    }
}
```

v2 的 `IModuleContext` 只提供：

- `Http`：在 ModuleHost 的受控前缀下注册路由，不能接管 Server 全局中间件；
- `Storage`：模块私有数据目录；
- `Log`：结构化日志；
- `Privileges`：提权 Broker 的计划/查询接口；
- `Capabilities`：只读的 Host 能力描述。

没有 `IServiceProvider`、没有 `IPrivilegedOperationTransport`、没有 Server 配置文件路径。

### 9.3 manifest

```json
{
  "schemaVersion": 2,
  "packageKind": "server-module",
  "runtime": "module-host",
  "moduleId": "com.example.hello",
  "version": "0.1.0",
  "publisher": { "id": "com.example", "name": "Example" },
  "purpose": "community",
  "rid": "linux-x64",
  "hostApiMinimum": "2.0.0",
  "hostApiMaximumExclusive": "3.0.0",
  "entryAssembly": "server/linux-x64/Example.Hello.Module.dll",
  "entryType": "Example.Hello.HelloModule",
  "buildId": "…",
  "sourceCommit": "…",
  "dependencies": [],
  "capabilities": [],
  "privileges": [
    {
      "kind": "package.install",
      "target": "example-agent",
      "constraints": { "source": "host-apt", "version": "1.*" },
      "reason": "安装模块依赖的代理程序"
    }
  ],
  "ui": {
    "name": "Hello",
    "summary": "第三方 ModuleHost 示例",
    "description": "演示独立进程、商店安装和提权确认。",
    "homepage": "https://example.com/",
    "license": "MIT"
  },
  "files": [
    {
      "path": "server/linux-x64/Example.Hello.Module.dll",
      "length": 23456,
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    }
  ]
}
```

### 9.4 安装与运行

1. 用户在组件中心模块商店搜索/打开详情，查看发布者、签名、提权项和依赖。
2. 用户确认“信任此模块”并安装；未签名包显示“未验证”，默认不因安装而获得提权放行。
3. Server 校验包、解包到不可变版本目录，更新 `state.json`。
4. 重启后 Server 启动 `RelaxKonOS.ModuleHost`；ModuleHost 以专用账户加载模块。
5. 模块业务请求经 Server 认证/授权后路由到 ModuleHost。
6. 模块提权时调用 `context.Privileges`；Broker 生成计划，Core 弹确认，Core 调 Helper，ModuleHost 只拿结果。
7. ModuleHost 崩溃时 Server 标记模块 `Faulted`，其他模块和 Server 继续运行。

### 9.5 v2 不提供什么

- 不提供 Server 进程内加载第三方 DLL。
- 不提供任意 DI 注册、任意 ASP.NET 全局路由、任意 HostedService。
- 不提供 Helper transport、sudoers、root shell 或任意命令执行。
- 不提供对 Server 数据库、JWT 密钥、其他模块数据目录的直接访问。
- 不把“签名”当作“代码安全”证明；用户信任仍是安装决定的一部分。

## 10. 商店与来源

模块商店是[包商店来源设计](../platform/RelaxKonOS.PackageStore.Design.md)的一个 `packageKind=server-module` 实例：

- 来源由部署配置定义，不能由包或客户端任意指定 URL；
- 目录带签名、序号、有效期和撤销集合；
- 搜索、分页、详情、下载、更新和离线目录共用商店契约；
- 官方来源与社区来源分开显示，不能把社区包标成官方；
- 下载先进入有界暂存，校验摘要/签名后再交给本安装器。

## 11. 安全与信任

- 官方模块：签名目录 + 受保护公钥 + 撤销 + 反回退；安装和启动前都验证。
- 第三方模块：用户显式信任；签名可选但推荐；安装页显示未验证/社区发布者、发布者、摘要、提权项和运行位置。
- 模块 ID 的 `relaxkonos.*` 前缀保留给官方；第三方使用反向域名。
- 模块不能通过 manifest 获得官方身份、Host 能力或提权授权。
- 安装、更新、回退、移除都要求服务器管理授权；第三方提权另走 Broker 的管理员确认。
- 日志、诊断和操作回执不记录密码、令牌、私钥或模块秘密。

## 12. 实施跟踪

所有条目初始为待实施；不得用设计文本关闭。

| 编号 | 交付 | 完成门槛 |
| --- | --- | --- |
| MD-01 | 冻结 `schemaVersion=2` manifest、共享抽象、错误码、状态 schema | 共享 Protocol/Server/打包工具/文档同步，无旧格式别名 |
| MD-02 | v1 官方模块安装事务、不可变版本目录、state/lastKnownGood | 安装/更新/回退/移除在 Windows/Linux 可重复 |
| MD-03 | v1 独立加载器、启动守卫、失败隔离 | 坏模块不发布能力；连续启动失败可回退/恢复 |
| MD-04 | v1 组件中心安装/信任/进度/回退 UI | 中英日渲染、断线/重启后原操作可查 |
| MD-05 | v2 ModuleHost 进程、专用身份/ACL、IPC | ModuleHost 无 Helper/Server 数据访问；崩溃不影响 Server |
| MD-06 | v2 模块安装、商店信任、用户信任记录 | 未验证包明确标记；更新/扩权重新确认 |
| MD-07 | v2 提权 Broker 接线 | 计划/确认/执行/查询闭环，见 Broker 设计 |
| MD-08 | 商店来源、搜索、下载、离线目录 | 签名/撤销/路径/大小/中断测试通过 |

### v1 首个切片（2026-10-31）

实现入口：`RelaxKonOS.Server/Startup/InstalledModuleStore.cs`（安装事实与加载器）、`InstalledModuleInstaller.cs`（验证/解包/写入）、`Shared/RelaxKonOS.ServerModules.Packaging/ModulePackageBuilder.cs`（manifest/ZIP/签名目录）、`Shared/RelaxKonOS.ServerModule.Abstractions/IInstalledServerModule.cs`（Host ABI）。

已实现并验证：

- `pack-package`：从 publish 目录生成 manifest、`.rkmodule`、签名目录和公钥；不读取/写入 Host 状态。
- `Shared/RelaxKonOS.ServerModule.Abstractions` 的 `IInstalledServerModule` / `InstalledModuleContext`。
- `InstalledModuleStore`：`installed.json` 原子读写、每版本 `install.json`、路径/摘要/ID 校验、移除记录。
- `ModulePackageOperationStore`：`package-operations.json` 记录 install/rollback 的 `AwaitingRestart` 回执；`ApplicationStarted` 后按精确版本/摘要核为 `Succeeded` 或 `Failed`。
- `ModulePackageStartupGuard`：`package-startup.json` 标记本次启动；若上次进程未到达 `ApplicationStarted`，把带 `previousVersion` 的更新操作回退到旧版本并标为 `Failed`（`module-package-operation-startup-rollback`）；没有历史版本的首次安装则把安装记录隔离为 `Quarantined`，不发布能力、不加载程序集，操作标记 `module-package-operation-startup-quarantined`，避免启动循环。
- `InstalledModuleInstaller`：基于已接纳签名目录验证候选，解包到不可变版本目录，写安装事实；同一 release 幂等；支持切换到已安装版本。
- `InstalledModuleCatalog`：启动时发现已安装模块，按需加载程序集；加载前重新核对 `manifest.json` 与每个文件的长度/SHA-256，篡改文件拒绝加载；共享抽象/Protocol 使用 Host 类型身份；模块可注册 DI 和 Endpoint。
- `ServerModules` / `ModuleManagementStore` 接入已安装模块目录；默认启用集合与 managed 状态可识别已安装模块。
- 离线命令：`install-package`、`list-installed`、`list-versions`、`list-package-operations`、`rollback-installed`、`remove-installed`。
- 只读 HTTP：`/server/module-packages/installed`、`/server/module-packages/{moduleId}/versions`、`/server/module-package-operations`。
- 模块搜索/下载 HTTP：`GET /server/module-store/search` 在已接纳签名目录中搜索，`POST /server/module-store/download` 按来源/发布下载并校验摘要到 staging；不暴露任意下载 URL。
- 桌面组件中心：显示已安装版本、所选模块版本和包操作回执；已接入“安装模块包”按钮（选择 `.rkmodule` → upload → auto plan → 确认 apply）、“回退到所选版本”按钮，以及已接纳模块商店搜索/“从商店安装”。
- 在线安装 HTTP：`POST /server/module-packages/upload`（原始 `.rkmodule` body，最大 1 GiB）、`POST /server/module-packages/plans`、`POST /server/module-packages/plans/{planId}/apply`、`POST /server/module-packages/rollback`；计划冻结 sourceId/releaseId/module/version/摘要，apply 重新校验后安装并写 `package-operations.json` 等待重启核实。直接 `POST /server/module-packages/install` 保留为无计划路径。
- 测试：真实模块程序集加载、DI 注册、Endpoint 映射、安装幂等、移除保留文件、双版本切换、回退、篡改拒绝、启动核实和只读包 API；`Tests/ServerModules` 全套通过（Windows 开发机）。

未实现：完整商店服务（远程源同步、评分/审核、搜索索引）、ModuleHost、提权 Broker。

## 13. 验收

至少覆盖：

- v1：官方签名、错误 RID、Host API 不兼容、依赖缺失/循环、路径穿越、摘要不符、损坏 ZIP、安装中断、重启失败回退、卸载保留数据。
- v2：ModuleHost 启动/崩溃/重启、专用账户权限、无法连接 Helper、无法读取 Server 数据、IPC 身份伪造、模块越权提权、用户拒绝确认、管理员验证复用、授权撤销、更新扩权重新确认。
- 商店：签名目录回放、过期、撤销、序号回退、镜像一致性、下载重定向/大小/摘要、离线目录、搜索分页、并发安装。
- 平台：Windows/Linux、System/User Mode 分别记录；不能用单平台构建代替另一平台验收。

## 14. 关联文档

- [模块包候选格式 v1（当前实现）](RelaxKonOS.ServerModulePackage.Format.md)
- [服务端模块管理设计](RelaxKonOS.ServerModuleManagement.Design.md)
- [模块提权 Broker 设计](RelaxKonOS.ServerModulePrivilegeBroker.Design.md)
- [包商店来源设计](../platform/RelaxKonOS.PackageStore.Design.md)
- [功能模块与双端应用扩展设计](RelaxKonOS.ModulesAndExtensions.Design.md)
