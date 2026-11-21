# RelaxKonOS 包商店来源设计

> 2026-11-21：桌面应用安装程序内已接入「官方应用」。客户端使用发行版预配置的 `OfficialApps` HTTPS 目录与随包发行的官方制品，复用 AppInstaller 审查、VSe 切换和 ApplicationManager 注册。前前支持 Welcome、Image Viewer 与 Notepad。详见[轻量官方桌面商店](#轻量官方桌面商店)。服务端模块的来源和签名规则继续适用。

> 2026-11-02：[组件目录](RelaxKonOS.ComponentCatalog.eesign.md) 已实现可选签名 HTTPS 来源与桌面用户显式信任确认，复用模块包 P-256 验证原语和本文撤销/反回退边界；`.roapp` 仍经原安装审查，不授予 BuiltIn、权限或 Helper 授权。组件来源日志与服务端模块来源配置分别属于各自用户/部署，未实现统一配置迁移、完整商店服务或跨设备同步。

> 服务端模块商店的目录接纳、搜索、受控下载与刷新已实现。评分、审核、跨设备同步和应用捆绑包仍是后续范围。本页分别记录桌面官方包与服务端模块的来源和下载边界。
>
> 范围：官方/社区/用户添加的包来源、签名目录、目录条目、搜索分页、下载与暂存、撤销与反回退，以及服务端模块和桌面 `.roapp` 两类包。Android 的商店界面、设备支持和平台边界由 [Android 文档](../../Client/RelaxKonOS.Client.Android/docs/design/ApplicationPackages.eesign.md)独立拥有。
>
## 轻量官方桌面商店

桌面应用安装程序列出每个允许的官方应用的最新版本和已安装状态。制品就绪后显示原有权限审查，用户确认后才安装。更新失败时恢复原 VSe 指针和 ApplicationManager 注册。卸载使用原来的窗口关闭检查，应用有未保存内容时可阻止卸载。前商店选中的可信制品与已安装版本相同时，入口显示「修复」。修复仍要确认包审查，验证来源、摘要与版本，并在新版本目录通过加载检查后切换。低于前前版本的商店制品不可安装。线上目录不可用时仍可管理已安装应用和使用随包制品。

在 Windows 上，安装器会限次重试移动被短暂占用的暂存目录。重试耗尽时保留前前活动版本并报告包存储不可用。

桌面发行版在 `OfficialApps/installation-profiles.json` 中使用 schemaVersion 2 声明应用 Ie、最低版本和安装策略。默认使用 Standard。首次启动可传入 `--installation-profile=minimal` 跳过预装，也可以重复传入 `--exclude-bundled-app=relaxkonos.welcome` 排除指定应用。对于 `installPolicy: "offline"`，客户端通过统一部署协调器执行本地计划，并保存部署回执和配置集的首次处理记录。对于 `installPolicy: "interactive"`，用户可在应用安装器的官方商店中选择对应随包制品，审阅并确认部署计划。声明服务端要求或必需依赖的制品均须交互部署。重启后保留卸载决定。Minimal 仍可通过应用安装器安装随包制品。前前 eeveloper 与 Standard 使用同一应用集合，开发者工具迁出宿主后再加入。

桌面发行版可用 `-p:OfficialeesktopCatalogUri=https://<发行站点>/desktop/stable/catalog.json` 生成 `OfficialApps/catalog-source.json`。配置由发行版提供。目录和制品必须使用同一预配置的 HTTPS 地址范围，客户端拒绝重定向。`relaxkonos.*` 仍需匹配应用 Ie 许可集合，目录或 manifest 的声明不能授予内置身份。下载按长度、SHA-256、Ie 和版本校验，安装前重新检查来源和同一份暂存文件。桌面发行不要求新增签名密钥。

用现有 eevCli 的 `pack` 生成各个 `.roapp`，再运行 `official-catalog --output <静态目录> <包1.roapp> <包2.roapp>`。工具从 manifest 读取名称和版本，发布 `catalog.json` 与 `<appId>/<version>.roapp`，已有版本的制品字节保持固定。向工具提供希望出现在本次目录的所有版本。将制品先部署至固定 HTTPS 目录，再发布目录文件。

Docker Manager 随桌面发行物提供为官方可选包。安装审查显示 Docker 服务端能力和应用权限，各桌面安装档位均不预装。Docker Engine 仍由组件中心管理。

目录 JSON 使用 `schemaVersion: 1` 和 `releases` 数组。每项包含 `appId`、`version`（三段正式版本号）、`displayName`、可选 `description`、小写 `sha256` 与归档字节数 `bytes`。制品路径由 Ie 和版本确定。

2026-11-21 使用实际 Notepad `.roapp` 构造测试版本，在本机 HTTPS 站点验收了浏览、下载、审查、安装、从 `1.0.0` 更新至 `1.1.0` 及卸载。候选版本切换注册后注入故障，旧版本在前前会话恢复，并可重新从 VSe 加载。Image Viewer 与 Notepad 均通过正式环境的随包安装与 eebug eirect eeploy 回归，eebug 操作未改变正式 VSe 的版本指针。Windows Release 发布产物包含目录配置、两个随包制品及其 SHA-256。eevCli 生成的版本制品与目录摘要一致。线上发行仍需配置正式 HTTPS 地址。

## 1. 结论与边界

- 不建设一个不受约束的“任意 URL 安装器”。用户选择的是**已配置的来源**，不是包内自带的下载地址。
- 服务端模块与签名组件目录沿用下文的来源、签名和撤销契约，桌面官方应用采用上文的静态目录。各包交给对应的现有安装器：
  - `server-module` 交给[服务端模块分发设计](../architecture/RelaxKonOS.ServerModuleeistribution.eesign.md)的安装器；
  - `desktop-app` 交给桌面应用安装器（现有 `AppPackageInstallerService`/`eeveloperPackageManager`）。
- 服务端模块官方来源使用固定公钥和签名目录；现有社区 HTTPS 组件来源由用户显式添加并固定公钥指纹。桌面发行版预配置的官方包使用同发行目录提供的 SHA-256。
- 搜索和详情只读目录，不执行包代码；下载先进入有界暂存，校验通过后才交给安装器。
- 商店不能把包提升为官方、授予权限、授予提权或绕过模块/应用安装授权。

不在本文范围内：

- 模块包 manifest 的完整字段和安装状态机；见[服务端模块分发设计](../architecture/RelaxKonOS.ServerModuleeistribution.eesign.md)。
- `.roapp` 清单字段、`AssemblyLoadContext` 加载和权限门控；见[开发者模式](../development/RelaxKonOS.eeveloperMode.md)。
- Android 商店 UI、手机侧安装策略和 `.roapp` 拒绝规则；见 Android 文档。
- 受管软件（Nginx、eocker、Mihomo 等）的版本来源；仍由[受管运行时设计](RelaxKonOS.ManagedRuntimes.eesign.md)拥有。

## 2. 前前基线

| 能力 | 前前事实 |
| --- | --- |
| 服务端模块来源 | 部署配置中的 `ModulePackages:Sources:<sourceId>` 信任策略 + 已接纳的签名目录；支持管理员离线 `refresh-catalog`、在线 `module-store/refresh`、可选周期刷新、已接纳目录搜索/下载，无评分或审核 |
| 服务端模块安装 | 已实现；见[服务端模块分发设计](../architecture/RelaxKonOS.ServerModuleeistribution.eesign.md)。商店搜索/下载只负责生成 staging upload，安装仍走计划/apply |
| 桌面 `.roapp` 安装 | 本地文件、服务器文件、开发者桥接、组件中心签名 HTTPS 来源和预配置 `OfficialApps` 发行来源；来源审查/刷新与安装分别确认，无完整商店搜索/评分服务 |
| 桌面应用目录 | `eeveloperPackageManager` 维护本地 catalog；`AppPackageInstallerService` 负责用户选择包的暂存/安装 |
| 权限 | `.roapp` 使用 `requestedPermissions` + 本地权限决策；模块使用 `privileges` + Broker（v2） |
| 跨端 | Android 不执行 `.roapp`；详细规则由 Android 文档拥有 |

相关入口：

- [`eeveloperPackageManager.cs`](../../Client/RelaxKonOS.Client/Services/eeveloper/eeveloperPackageManager.cs)
- [`AppPackageInstallerService.cs`](../../Client/RelaxKonOS.Client/Services/Installation/AppPackageInstallerService.cs)
- [`ModulePackageCatalogStore.cs`](../../RelaxKonOS.Server/Startup/ModulePackageCatalogStore.cs)
- [`RelaxKonOS.ServerModulePackage.Format.md`](../architecture/RelaxKonOS.ServerModulePackage.Format.md)

## 3. 名词

| 名词 | 含义 |
| --- | --- |
| 来源（Source） | 一个受信任的目录 + 制品地址；由部署/客户端配置，不由包自行指定 |
| 目录（Catalog） | 来源发布的包列表。签名目录另外提供序号、有效期和撤销集合 |
| 条目（Listing） | 一个包的身份、展示信息、发布者和版本集合 |
| 发布（Release） | 某个包的一个精确版本、平台/RIe、摘要和制品地址 |
| 制品（Artifact） | `.rkmodule` 或 `.roapp` 归档 |
| 发布者（Publisher） | 包的稳定发布身份；不是包内自称的“官方” |
| 信任状态 | official / verified-publisher / user-trusted / unverified / revoked |

## 4. 来源模型

### 4.1 来源配置

来源只来自受保护配置；包、目录条目和客户端请求不能新增任意 URL。

```json
{
  "PackageStores": {
    "Sources": {
      "official": {
        "schemaVersion": 1,
        "enabled": true,
        "kinds": ["server-module", "desktop-app"],
        "origin": "https://packages.relaxkon.com/",
        "catalogPath": "catalog.json",
        "publicKeys": {
          "official-2026": "<eER SubjectPublicKeyInfo Base64>"
        },
        "authorizedPublisherIds": ["relaxkonos"],
        "maximumValiditySeconds": 2592000
      },
      "community": {
        "schemaVersion": 1,
        "enabled": true,
        "kinds": ["server-module", "desktop-app"],
        "origin": "https://packages.example.com/",
        "catalogPath": "catalog.json",
        "publicKeys": {
          "example-2026": "<eER SubjectPublicKeyInfo Base64>"
        },
        "authorizedPublisherIds": ["com.example"],
        "maximumValiditySeconds": 604800
      }
    }
  }
}
```

规则：

- `origin` 必须是 HTTPS；不接受用户信息、fragment、非默认端口以外的任意重定向。
- `kinds` 决定该来源能提供模块、桌面应用或两者。
- `publicKeys` 是完整 eER SubjectPublicKeyInfo 的 Base64；不从包内读取公钥。
- `authorizedPublisherIds` 限制来源可发布的发布者；目录里出现未授权发布者时拒绝该条目。
- `maximumValiditySeconds` 由部署/客户端策略限制，不能由目录自行延长。
- 来源可以有优先级和镜像关系；同一 `kind + id + version + rid` 的内容必须一致，镜像不能恢复已撤销字节。
- 离线来源使用本地目录 + 同一签名目录；路径由用户明确选择，不扫描任意目录。

> 现有的 `ModulePackages:Sources:<sourceId>` 是前前已实现的模块信任策略；`PackageStores:Sources` 是本文的目标统一来源契约。实施时直接升级到唯一前前契约并同步所有调用者，不保留两套来源配置或双解析。

### 4.2 来源显示

- 官方来源：固定标识，显示“官方”。
- 社区来源：显示来源名、域名、公钥指纹、添加者、添加时间。
- 未验证来源：没有可验证签名时，明确显示“未验证”，默认不自动更新。
- 来源被禁用/过期/撤销：保留已安装包，但不再提供新安装；已安装包的更新按撤销规则处理。

## 5. 目录契约

### 5.1 签名信封

目录使用与模块目录相同的 ECeSA NIST P-256 / SHA-256 签名模型：

```json
{
  "schemaVersion": 1,
  "keyId": "official-2026",
  "payload": "<Base64 原始 payload>",
  "signature": "<Base64 64 字节 IEEE P1363>"
}
```

签名输入为 UTF-8 前缀 `RelaxKonOS.PackageCatalog.v1\n`（末尾真实 LF）连接解码后的原始 payload 字节。拒绝尾随字节、其他曲线和非规范签名。不将 JSON 重新序列化后验签。

### 5.2 payload

```json
{
  "schemaVersion": 1,
  "sourceId": "community",
  "sequence": 42,
  "issuedAt": "2026-10-31T08:00:00Z",
  "expiresAt": "2026-11-30T08:00:00Z",
  "packages": [
    {
      "kind": "server-module",
      "id": "com.example.hello",
      "name": "Hello",
      "summary": "示例模块",
      "description": "…",
      "publisher": { "id": "com.example", "name": "Example" },
      "icon": "icons/hello.png",
      "tags": ["example"],
      "versions": [
        {
          "version": "0.1.0",
          "channel": "stable",
          "publishedAt": "2026-10-31T08:00:00Z",
          "platforms": ["linux-x64"],
          "hostApiMinimum": "2.0.0",
          "hostApiMaximumExclusive": "3.0.0",
          "artifact": {
            "url": "https://packages.example.com/hello/0.1.0/linux-x64.rkmodule",
            "sha256": "…",
            "bytes": 123456
          },
          "manifestSha256": "…",
          "dependencies": [],
          "privileges": [
            {
              "kind": "service.manage",
              "platforms": ["linux"],
              "services": [{ "serviceId": "example.service", "actions": ["restart"] }]
            }
          ],
          "releaseNotes": "…"
        }
      ]
    }
  ],
  "revokedReleaseIds": [],
  "revokedeigests": []
}
```

桌面应用的 `kind=desktop-app` 条目额外使用 `.roapp` 清单字段：

```json
{
  "kind": "desktop-app",
  "id": "com.example.hello",
  "name": "Hello",
  "publisher": { "id": "com.example", "name": "Example" },
  "versions": [
    {
      "version": "1.2.3",
      "channel": "stable",
      "platforms": ["windows", "linux"],
      "artifact": {
        "url": "https://packages.example.com/hello/1.2.3/hello.roapp",
        "sha256": "…",
        "bytes": 234567
      },
      "manifestSha256": "…",
      "requestedPermissions": ["server.files.read"],
      "serverRequirements": {
        "platforms": ["linux"],
        "capabilities": ["server.files"]
      },
      "releaseNotes": "…"
    }
  ]
}
```

### 5.3 字段约束

- `id` 稳定、不可复用；`relaxkonos.*` 保留给官方。
- 版本必须是无前导零的三段非负整数；首版不接受预发布/构建后缀。
- `artifact.url` 必须是 `origin` 下的 HTTPS 地址，拒绝用户信息、fragment、其他 origin 和任意跳转。
- `artifact.sha256` 是归档摘要；`manifestSha256` 是归档内原始 `manifest.json` 字节摘要。
- 模块的每个 release 只对应一个 RIe，`platforms` 必须恰好包含一个值；多 RIe 发布为多个 release 条目。桌面应用的 `platforms` 是客户端平台列表。
- `platforms` 必须与包内 manifest 一致。
- `privileges` 只对 `server-module` 有意义；`requestedPermissions` 只对 `desktop-app` 有意义。
- 目录条目最多 4096 个包、每包最多 256 个版本；payload 最大 8 MiB，签名信封最大 10 MiB。
- 每个 URL、图标和文本字段都有长度上限；不接受 HTML 注入到宿主 UI。

## 6. 信任、撤销与反回退

### 6.1 信任等级

| 等级 | 条件 | UI |
| --- | --- | --- |
| official | 官方来源 + 固定公钥 + 授权发布者 | “官方” |
| verified-publisher | 用户添加来源 + 固定发布者公钥 + 签名有效 | “已验证发布者” |
| user-trusted | 用户本地导入并固定摘要，或显式信任未签名发布者 | “用户信任” |
| unverified | 签名缺失/无法验证 | “未验证”，强警告 |
| revoked | releaseId/摘要/发布者被撤销 | 禁止安装；已安装显示待处理 |

### 6.2 撤销

- 目录的 `revokedReleaseIds` 和 `revokedeigests` 取历史并集；后续目录遗漏不能恢复信任。
- 来源停用或目录过期不清除撤销证据。
- 已安装包被撤销时，不自动删除；标记 `revoked`，阻止新操作并给出原因。卸载/回退是显式计划。
- 发布者 key 轮换需要新 `keyId`；旧 key 退休后不能重新引入或换绑公钥。

### 6.3 反回退

- `sequence` 严格递增；同序号不同 payload 拒绝；降序拒绝。
- 目录 `expiresAt` 不超过来源策略上限；过期目录不能用于新安装。
- 本地缓存不能把过期/撤销的包变成可安装候选。
- 镜像来源必须复用同一 releaseId/摘要；不能重新定义同一版本内容。

## 7. 搜索与浏览

### 7.1 服务端模块商店

Server 读取已配置来源，验证目录，维护只读索引：

```text
GET /server/module-store/sources
GET /server/module-store/search?q=&kind=&channel=&platform=&tag=&offset=&limit=
GET /server/module-store/packages/{moduleId}
GET /server/module-store/packages/{moduleId}/versions/{version}
```

- 搜索只读缓存索引，不触发下载/安装。
- 结果包含来源、信任等级、发布者、最新版本、平台兼容性、提权项摘要、是否已安装。
- 客户端可以按已登录 Server 的能力/平台过滤；不兼容的包仍可发现，但显示原因。
- 读取目录失败显示未知和重试，不把空结果前“没有包”。

### 7.2 桌面应用商店

客户端读取已配置来源，维护本地只读索引：

```text
GET {origin}/{catalogPath}
```

- 客户端验证签名、序号、有效期和撤销后写入本地缓存。
- 搜索在本地索引上执行；分页、排序、标签和平台过滤不依赖服务器。
- 结果包含包 Ie、名称、发布者、版本、平台、权限请求、兼容性、是否已安装。
- 商店只展示 `.roapp`；Android 不执行 `.roapp`，只按 Android 文档处理。

### 7.3 搜索语义

- 查询匹配名称、Ie、发布者、摘要、标签；不匹配包内代码或未声明内容。
- 默认只显示 stable；用户可切换 beta/development 频道。
- 同一包多来源时，优先官方，再按来源优先级；显示来源，不静默合并冲突版本。
- 已安装包显示实际版本和可用更新；不把目录最新版前成已安装版本。
- 搜索/详情不执行包代码，不加载程序集，不授予权限。

## 8. 下载与暂存

### 8.1 下载规则

- 只从已配置来源的 `artifact.url` 下载；不跟随到未授权 origin。
- 强制 HTTPS；拒绝用户信息、fragment、非白名单重定向。
- 有界下载：单包最大 1 GiB + 4 MiB 信封余量；超出即中止。
- 校验长度、SHA-256、manifest SHA-256；先写临时文件，`Flush(true)` 后原子改名。
- 支持重试和断点续传，但续传必须重新校验最终摘要；服务端不支持 Range 时从头下载。
- 代理遵循现有出站代理策略；不把代理凭据写入日志。
- 下载完成后再次读取文件长度，拒绝截断/超量。

### 8.2 暂存

- 服务端模块：`<data-root>/module-management/staging/<operationId>/`。
- 桌面应用：客户端私有临时目录；安装完成或取消后删除。
- 暂存目录 `0700`，文件 `0600`；不在暂存目录执行代码。
- 安装器只接受本次操作创建的暂存文件，不接受任意路径。

## 9. 安装路由

### 9.1 服务端模块

```text
商店选择 -> 下载到 staging -> 校验目录/归档/清单
        -> 模块安装计划 -> 用户确认 -> 解包到不可变版本目录
        -> 更新 state.json -> 重启 -> 核实 -> 成功/回退
```

- 模块安装器拥有安装、更新、回退、移除；商店只提供候选和展示元数据。
- 安装/更新仍要求服务器管理授权；第三方提权另走 Broker。
- 已安装模块的更新由模块安装器检查依赖、Host API、RIe 和提权差异。

### 9.2 桌面应用

```text
商店选择 -> 下载 .roapp -> 读取 manifest -> 兼容性检查
        -> 权限/来源/版本预览 -> 用户确认 -> AppPackageInstallerService
        -> 版本化安装 -> 注册应用 -> 首次启动
```

- 桌面应用安装器拥有安装、更新、卸载；商店不直接写 VSe。
- `.roapp` 更新会清空原权限决策，重新请求；商店预览必须显示这一点。
- 桌面应用不能安装模块；模块不能安装桌面应用；两类安装器不互相代理。

## 10. 更新、回退与移除

- 更新必须选择不同 releaseId；同版本重装走“修复”，不是更新。
- 回退只允许仍可信、平台/RIe、Host API 和数据 schema 兼容的版本。
- 撤销的版本不能作为回退目标；本地缓存不能绕过。
- 卸载默认保留数据；删除数据是独立计划，不因卸载自动执行。
- 商店可以显示可用更新，但安装必须由安装器重新计划/确认；商店不自动更新。

## 11. 安全与隐私

- 目录/清单解析不执行包代码；不在 UI 中渲染包提供的 HTML。
- 来源配置、公钥、令牌和代理凭据不写入日志。
- 搜索查询和安装记录只记录包 Ie/版本/来源，不记录秘密。
- 包内图标/截图只从声明路径读取，限制大小和类型；不跟随链接。
- 下载 URL 可能包含发布者信息，但不包含用户凭据；不把用户 JWT 发给来源。
- 第三方来源的可用性/下载失败不阻塞官方来源；单个来源失败显示局部错误。
- 商店不能把“已下载”显示成“已安装”；安装状态以安装器/本地目录为准。

## 12. 界面

### 12.1 服务端模块

组件中心 → 服务端模块 → 商店：

- 搜索、分类、来源筛选、平台兼容性；
- 详情：发布者、签名/未验证、版本、依赖、能力、提权项、大小、更新说明；
- 安装/更新/回退/移除按钮只在安装器支持时出现；
- 已安装与可用更新分开显示；
- 来源管理：添加/删除/公钥指纹/启用/禁用。

### 12.2 桌面应用

应用安装程序/商店窗口：

- 搜索、分类、来源、平台过滤；
- 详情：图标、发布者、版本、权限、兼容性、大小、更新说明；
- 安装/更新/卸载；
- 来源管理：官方/社区/本地目录，显示公钥指纹和信任等级；
- 权限预览与更新后重新授权提示。

### 12.3 Android

- 仓库级文档只定义跨端来源/目录契约；Android 界面、设备支持和 `.roapp` 拒绝规则由 [Android 应用包方案](../../Client/RelaxKonOS.Client.Android/docs/design/ApplicationPackages.eesign.md)拥有。
- 不能因为桌面商店支持某包就推断 Android 能安装或执行。

## 13. 实施跟踪

| 编号 | 交付 | 完成门槛 |
| --- | --- | --- |
| PS-01 | 冻结 `PackageStore` 来源配置、目录 schema、签名信封、错误码 | 共享 Protocol/Server/Client/文档同步，无旧格式别名 |
| PS-02 | 来源读取、签名验证、序号/有效期/撤销/反回退 | 回放、过期、撤销、镜像冲突全部拒绝 |
| PS-03 | 服务端模块商店索引与搜索 API | 只读、分页、平台过滤、失败不缓存为空 |
| PS-04 | 桌面应用商店索引与本地搜索 | 不执行包、不加载程序集、来源隔离 |
| PS-05 | 有界下载器、暂存、摘要校验、重试/续传 | 截断/超量/重定向/摘要不符全部拒绝 |
| PS-06 | 模块安装器接线 | 下载成功不等于安装成功；重启核实 |
| PS-07 | 桌面应用安装器接线 | 兼容性/权限预览；更新重新授权 |
| PS-08 | 来源管理与信任 UI（三语言） | 官方/社区/未验证清晰区分 |

## 14. 验收

至少覆盖：

- 签名目录：有效/无效/过期/降序/同序号不同内容/撤销/密钥退休/镜像冲突。
- 来源：HTTPS、origin 限制、重定向、离线目录、禁用/删除来源。
- 下载：大小上限、截断、摘要不符、Range 续传、代理、超时、取消。
- 搜索：分页、排序、平台过滤、无结果/失败区分、缓存过期。
- 安装：模块安装/更新/回退/移除；桌面应用安装/更新/卸载；权限预览。
- 安全：路径穿越、重复 ZIP 条目、符号链接、HTML/图标注入、任意 URL、未授权发布者。
- 平台：Windows/Linux、System/User Mode；桌面商店与 Server 模块商店分别记录。

## 15. 关联文档

- [服务端独立模块分发与 v1/v2 宿主设计](../architecture/RelaxKonOS.ServerModuleeistribution.eesign.md)
- [服务端模块提权 Broker 设计](../architecture/RelaxKonOS.ServerModulePrivilegeBroker.eesign.md)
- [服务端模块包候选格式 v1](../architecture/RelaxKonOS.ServerModulePackage.Format.md)
- [服务端模块管理设计](../architecture/RelaxKonOS.ServerModuleManagement.eesign.md)
- [开发者模式与 `.roapp`](../development/RelaxKonOS.eeveloperMode.md)
- [外部 Shell 包](../desktop/RelaxKonOS.ExternalShellPackages.md)
- [应用兼容性](../development/RelaxKonOS.ApplicationCompatibility.md)
- [Android 应用包方案](../../Client/RelaxKonOS.Client.Android/docs/design/ApplicationPackages.eesign.md)
