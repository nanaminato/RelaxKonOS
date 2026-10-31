# 服务端独立模块候选包 v1

> 状态：2026-10-31，候选格式、纯校验器、只读维护命令、本机管理员离线目录接纳/远程目录刷新、离线/在线打包安装、进程内独立加载器、重启核实和启动失败回退/隔离已实现。完整商店服务、自动重启/健康探测、ModuleHost 仍待实施。本格式不适用于 Android、桌面或第三方隔离扩展。
>
> 独立模块安装、v1 官方进程内 / v2 第三方 ModuleHost 的宿主边界、模块商店与示例由[服务端独立模块分发与 v1/v2 宿主设计](RelaxKonOS.ServerModuleDistribution.Design.md)定义；第三方提权由[模块提权 Broker 设计](RelaxKonOS.ServerModulePrivilegeBroker.Design.md)定义。v2 实施时直接升级到唯一当前契约，不保留 v1 双解析或旧字段别名。

本文件细化[模块总设计](RelaxKonOS.ModulesAndExtensions.Design.md)的服务端包边界；交付验收仍由[模块管理设计](RelaxKonOS.ServerModuleManagement.Design.md)拥有。软件安装器及受管运行时目录不处理本格式。

## 包与签名目录

`.rkmodule` 是 ZIP，只允许根部 `manifest.json` 和清单逐一声明的 `server/<rid>/…` 普通文件。不接受显式目录、符号链接、设备、Windows reparse 标记、未声明文件或大小写冲突路径。路径只允许 ASCII 字母、数字、点、下划线、连字符和目录分隔符 `/`；拒绝空段、`.`、`..`、尾点、Windows 保留设备名及文件/目录冲突。包内没有可信公钥或可自行宣称的“官方”授权。

清单唯一当前 JSON 契约为 `Shared/RelaxKonOS.ServerModules.Packaging/ModulePackageContracts.cs` 中 `ModulePackageManifest`，使用精确 camelCase 属性，拒绝未知、重复、缺失字段和大小写别名。`schemaVersion=2`、`packageKind=server-module`；声明模块/发布/发布者身份、purpose、版本、单一 RID、Host API 半开区间、入口程序集路径及类型、构建身份、源码提交、依赖、能力和每个文件的长度及 SHA-256。版本必须为无前导零的三段非负整数；不接受预发布、构建后缀或浮动版本。依赖版本使用相同的最小含端点、最大不含端点区间；安装前解析已安装依赖、版本区间和依赖环。

支持的候选 RID 为 linux-x64、linux-arm64、win-x64、win-arm64、osx-x64、osx-arm64；只接受与当前进程 `RuntimeInformation.RuntimeIdentifier` 完全一致的候选。这不表示这些平台已有安装或业务验收。当前候选 Host API 版本为 `1.0.0`；`Shared/RelaxKonOS.ServerModule.Abstractions` 已提供 `IInstalledServerModule` 加载接口，校验入口类型的语法不代表已加载或发现该类型。

外部 `SignedModulePackageCatalog` envelope 使用 `schemaVersion=1`、`keyId`、Base64 `payload`、Base64 `signature`。签名输入为 UTF-8 `RelaxKonOS.ServerModuleCatalog.v1\n` 前缀（末尾为真实 LF）连接解码后的原始 payload 字节；算法 ECDSA NIST P-256 / SHA-256，签名编码为 64 字节 IEEE P1363。公钥为完整 DER SubjectPublicKeyInfo 的 Base64；拒绝尾随字节和其他曲线。不将 JSON 重新序列化后验签。

payload 为 `ModulePackageCatalog`，包含 sourceId、严格递增序号、UTC 签发/过期时间、发布条目和撤销集合。每条发布绑定完整类型化清单、归档中原始 `manifest.json` 的 SHA-256、整个 ZIP 的 SHA-256/字节数及下载地址。目录签名成功后仍检查归档与每个展开文件。下载地址只能使用 Host 配置的 HTTPS origin，无用户信息或 fragment；下载器拒绝重定向并限制 origin、长度和摘要。

边界：目录 payload/清单最多 4 MiB，envelope 最多 6 MiB，发布条目最多 2048，单清单文件最多 4096、单文件最多 256 MiB、展开合计最多 1 GiB、归档最多 1 GiB + 4 MiB；读取展开内容时重新累计长度，不能只信任 ZIP 头部。依赖和能力各最多 128；最大目录有效期由 Host 策略限制，不能超过 31 天。

## Host 信任与历史

可信部署配置 `ModulePackages:Sources:<sourceId>` 使用 `ModulePackageTrustPolicy` 的字段：schemaVersion、sourceId、revision、enabled、publisherId、purpose、artifactOrigin、authorizedModuleIds、publicKeysSpki、maximumValiditySeconds、developmentServiceId。sourceId 必须与配置键一致。公钥 ID 到 SPKI 的映射和授权模块集合由部署方提供，不读取候选包内的授权。

`purpose=official` 要求 developmentServiceId 为 null。开发来源必须 `purpose=development`，明确绑定本机稳定 serviceId，同时 Host 环境为 Development 且 `ModulePackages:DevelopmentInstance=true`。生产实例不能接纳开发来源；开发包也不能因 manifest 字段自行升级成官方包。

纯校验器接受既有 `ModulePackageTrustHistory`，返回独立的 `NextHistory`，不改写输入、不写文件。历史绑定 sourceId、serviceId 和 purpose；保护策略修订、同序号目录内容、发布身份及 moduleId/version/rid 的不可变内容。移除的 keyId 永久退休，不能重新引入或换绑公钥；撤销 releaseId/digest 取历史并集，后续目录遗漏不会恢复信任。策略同修订内容变化、目录降序/同序号不同 payload、发布或版本内容变更均拒绝。历史证据容量有界，不能以静默裁剪历史解决超限。

**接纳方必须先原子持久保存返回历史，再让任何发布可用于安装。** 本机离线接纳将完整签名 envelope、每来源历史、跨来源 moduleId/version/rid 内容身份和操作者/幂等键/精确摘要/修订/operationId 回执共同写入 `data/module-management/package-catalogs.json`。使用与 Host 相同的 host.lock，拒绝在 Host 运行时执行；精确 expectedRevision 冲突及文件外部修改均拒绝。相同操作者、幂等键和原始 envelope/目标修订只返回原回执，重签名文件或改目标不视为原请求。原回执是历史事实，不能绕过当前有效期检查。

首次保存前持久写入绑定 serviceId 的 `package-catalogs.initialized` 标记，此后标记或状态单独丢失均要求恢复，不能当作空历史重新初始化。单个状态文档最多 32 MiB、来源最多 16、跨来源版本证据和接纳回执各最多 10000，不自动裁剪证据。文件 WriteThrough、Flush(true) 后原子替换；现阶段覆盖进程退出与原子文件可见性，尚无断电/文件系统故障注入验收。目录沿用模块管理的受保护 ACL/0700，Linux 文件为 0600，提权执行按现有服务 state.json 保留所有者；离线写入当前只支持 Windows/Linux。

跨来源不能重新定义同一模块版本/RID 的清单或归档；相同内容可由另一已授权来源镜像。候选资格合并所有已接纳来源的 releaseId/digest 撤销，releaseId 的已知历史摘要也参与撤销，镜像不能恢复已撤销字节。来源停用或其目录过期不会清除既有撤销证据。读取已接纳目录仍重新验签、检查有效期及当前 Host 策略；策略修订变化须先持久接纳刷新，不能在只读查询中悄然接受新信任状态。

后续安装、修复和回滚都必须依据当前信任及撤销状态重新检查；有效期过后，已有文件也不能凭缓存自动成为新的可安装候选。当前已实现 v2 清单的离线安装与按 runtime 选择加载器；在线版本目录、自动重启核实、更新/回退事务仍须在后续阶段完成。安装成功不等于模块已启用或已生效。

## 只读维护命令

```text
RelaxKonOS.Server modules verify-package --content-root <绝对服务目录> --source-id <已配置来源> --catalog <本地签名目录> --archive <本地候选.rkmodule> --release-id <精确发布ID>
```

命令读取已有模块管理 state.json 的稳定 serviceId，按照普通 Host 配置优先级及 appsettings.host.json 读取来源；不创建服务身份。读取唯一 package-catalogs.json 中的既有历史和跨来源撤销；首次接纳前只进行候选检查。结果显式包含 `readOnly=true`、trustHistoryPresent、`trustHistoryCommitted=false`、`installed=false`，不能作为已安装、已生效、健康或可信目录已持久接纳的回执。

命令不创建目录、保存历史、提取文件、加载程序集、初始化模块、下载、启动软件或修改运行中的服务。安装类动作必须使用下面的独立命令；`verify-package` 本身不安装。

## 离线接纳命令

```text
RelaxKonOS.Server modules catalog-status --content-root <绝对服务目录>
RelaxKonOS.Server modules accept-catalog --content-root <绝对服务目录> --source-id <已配置来源> --catalog <本地签名目录> --service-id <精确本机身份> --expected-revision <catalog-status返回修订> --idempotency-key <本次请求键>
RelaxKonOS.Server modules refresh-catalog --content-root <绝对服务目录> --source-id <已配置来源> --expected-revision <catalog-status返回修订> --idempotency-key <本次请求键> [--catalog-uri <HTTPS目录地址>]
```

catalog-status 只读报告已保存目录及修订，首次为 `uninitialized`；观察范围为 persisted-catalogs，不把未重新验签的状态摘要作为可安装清单。accept-catalog 要求本机管理员/root、已有服务身份、Host 已停止和精确修订，不自动停服务或取得新信任根。成功仅返回 `trustHistoryCommitted=true`、`installed=false` 和原子保存的目录接纳回执。

refresh-catalog 使用显式 `--catalog-uri` 或配置 `ModulePackages:Sources:<sourceId>:CatalogUri`，只接受 HTTPS、无用户信息/片段、无重定向、最大 6 MiB 的签名目录；要求同样的本机管理员、Host 停止和精确修订，下载后走与 accept-catalog 相同的验签/历史/撤销接纳路径。

Linux 隔离进程验证入口为 `Tests/ServerModules/verify-linux-module-catalogs.sh <自包含构建目录>`；只使用随机临时目录和测试生成的签名候选，不修改生产配置、服务或信任根。

## 离线打包命令

```text
RelaxKonOS.Server modules pack-package \
  --publish-dir <dotnet publish输出目录> --output-dir <打包输出目录> \
  --module-id <模块ID> --version <x.y.z> --release-id <发布ID> \
  --publisher-id <发布者ID> --purpose official --rid <RID> \
  --entry-assembly <publish内相对路径> --entry-type <入口类型> \
  --build-id <构建ID> --source-commit <40或64位小写十六进制> \
  --source-id <来源ID> --sequence <目录序号> --artifact-origin https://<origin>/ \
  --private-key <ECDSA P-256 PKCS#8 PEM> --key-id <公钥ID> --validity-seconds <1..2678400>
```

可选参数：`--runtime in-process|module-host`、`--metadata <JSON文件>`（严格包含 `privileges` 与 `ui`）、`--host-api-min`、`--host-api-max`、`--capabilities a,b`、`--dependencies id:min:max,...`。命令把 publish 目录下所有普通文件放到 `server/<rid>/`，生成 `manifest.json`、`.rkmodule`、`catalog.json` 和 `public-key.spki`；私钥只用于本次签名，不写入输出或日志。它不读取/写入 Host 状态，也不安装模块。

## 在线上传与安装 API

```text
POST /api/v1.0/server/module-packages/upload
Content-Type: application/octet-stream
<body: .rkmodule>

POST /api/v1.0/server/module-packages/plans
{ "sourceId": "official", "releaseId": "…", "uploadId": "<32位十六进制>" }

POST /api/v1.0/server/module-packages/plans/auto
{ "uploadId": "<32位十六进制>" }

POST /api/v1.0/server/module-packages/plans/{planId}/apply

POST /api/v1.0/server/module-packages/rollback
{ "moduleId": "…", "version": "…" }

POST /api/v1.0/server/module-packages/install
{ "sourceId": "official", "releaseId": "…", "uploadId": "<32位十六进制>" }

GET /api/v1.0/server/module-store/search?q=…&sourceId=official&offset=0&limit=25

GET /api/v1.0/server/module-store/sources

POST /api/v1.0/server/module-store/refresh
{ "sourceId": "official", "expectedRevision": "<sources.catalogRevision>", "idempotencyKey": "…", "catalogUri": "https://…" }

POST /api/v1.0/server/module-store/download
{ "sourceId": "official", "releaseId": "…" }

GET /api/v1.0/server/module-health

GET /api/v1.0/server/module-hosts

GET /api/v1.0/server/module-hosts/{moduleId}/{**path}
```

上传要求服务器管理授权，原始 body 最大 1 GiB，返回 uploadId/字节数/SHA-256；上传本身不验证包。plans 重新按已接纳签名目录验证 sourceId/releaseId/上传文件，生成十分钟有效的冻结计划，绑定 module/version/publisher/归档摘要/manifest 摘要；plans/auto 通过上传摘要在所有已接纳来源中查找唯一 release，找到多个不同模块/版本时返回 `module-package-upload-ambiguous`。plan apply 重新验证计划和上传文件仍为同一目标，然后安装；重复 apply 返回原 operationId，不重复安装。rollback 重新核对目标版本文件哈希，切换 `installed.json` 当前版本。

`module-store/search` 只读已接纳签名目录，不执行包；`module-store/sources` 只读报告已接纳来源的 sequence/policy revision/release 数/撤销数、`catalogRevision` 和是否配置远程刷新 URI。`module-store/refresh` 使用 `catalogUri` 或配置的 `CatalogUri` 获取 HTTPS 签名目录，要求精确 `expectedRevision` 和幂等键；它只持有独立的目录锁，因此可在 Host 运行时安全接纳签名目录，不改启用集合、不重启。配置 `ModulePackages:Refresh:Enabled=true` 和 `ModulePackages:Refresh:IntervalSeconds`（60–86400）后，Host 会周期刷新所有配置了 `CatalogUri` 的来源；已是最新 sequence 时跳过，失败仅记录日志，不阻断 Host。`module-store/download` 按 sourceId/releaseId 从策略允许的 HTTPS origin 下载，拒绝重定向、超过 1 GiB、字节数或 SHA-256 不一致的内容，成功后才写入 staging。客户端不能提交任意下载 URL。

`module-health` 返回内置模块和已安装模块的启用状态/版本/来源/health status。已安装模块可实现可选的 `IInstalledServerModuleHealth`；未实现时报告 `loaded`，不冒充健康。隔离模块报告 `quarantined`，加载失败报告 `load-failed`。该接口只读，不改变启用集合、不重启 Host。`module-hosts` 只读报告 v2 ModuleHost 条目状态；`module-hosts/{moduleId}/{**path}` 在认证后反向代理到该 ModuleHost 的 loopback 路由。`AllowedUsers` 为空时仅 `ModuleManagement:Administrators` 可访问，配置用户 ID 时仅这些用户可访问；Server 传递 `X-RelaxKonOS-Module-User`、`X-RelaxKonOS-Module-Role`、`X-RelaxKonOS-Module-Request-Id`，不转发 Authorization/Cookie。每个 ModuleHost 还要求随机服务令牌 `X-RelaxKonOS-Module-Token`，由 Server 代理自动附加；令牌通过子进程环境变量传递，不写入命令行或日志。

可选看门狗配置 `ModuleHealth:Enabled=true`、`ModuleHealth:StopOnUnhealthy=true`、`ModuleHealth:ProbeIntervalSeconds`（10–3600）、`ModuleHealth:StopExitCode`（1–255，默认 1）：启动后周期检查启用模块，发现 `unhealthy`/`load-failed`/`quarantined` 时写 `health-stop.json`、设置非零退出码并请求 Host 停止；外部服务监督器负责重启。示例见[进程监督与健康停机恢复](../services/ServerProcessSupervision.md)。

plan apply / direct install / rollback 都写 `package-operations.json` 的 `AwaitingRestart` 回执，并删除已消费的上传文件。安装成功不等于已启用或已生效；重启后的启动核实把操作标为 `Succeeded`/`Failed`。

服务端提供 plans 与 plans/auto 路由；桌面组件中心已接入“安装模块包”按钮：选择 `.rkmodule` 后上传并 auto plan，再确认 apply；已安装版本列表旁提供“回退到所选版本”，模块商店区域支持搜索已接纳来源并“从商店安装”。direct install 是无计划路径，优先使用 plans。

## 离线安装与移除命令

```text
RelaxKonOS.Server modules list-installed --content-root <绝对服务目录>
RelaxKonOS.Server modules list-versions --content-root <绝对服务目录> --module-id <模块ID>
RelaxKonOS.Server modules list-package-operations --content-root <绝对服务目录>
RelaxKonOS.Server modules rollback-installed --content-root <绝对服务目录> --module-id <模块ID> --version <目标版本>
RelaxKonOS.Server modules install-package --content-root <绝对服务目录> --source-id <已配置来源> --archive <本地候选.rkmodule> --release-id <精确发布ID>
RelaxKonOS.Server modules remove-installed --content-root <绝对服务目录> --module-id <模块ID>
RelaxKonOS.Server modules host-probe --content-root <绝对服务目录> --package-root <模块版本目录> --module-id <模块ID> [--data-path <私有数据目录>] [--host-executable <ModuleHost路径>]
```

install-package 要求本机管理员/root、Host 已停止、目录已通过 accept-catalog 接纳；它重新验证签名目录/归档/清单，解包到 `data/module-management/packages/<moduleId>/<version>/`，写入每版本 `install.json` 和顶层 `installed.json`。安装前还会检查依赖：未知/缺失依赖、自依赖、已安装依赖版本不满足范围、依赖被隔离或依赖成环都会拒绝。它不修改 `state.json` 的期望启用集合，也不自动重启；`installed=true` 不代表 effective。同一 release 重装幂等；同版本不同摘要拒绝。模块被启用并加载前，Host 还会重新核对安装清单和每个文件的长度/SHA-256。

`host-probe` 是本机管理员诊断命令（开发诊断不提供 OS 隔离承诺）：要求 Host 已停止，启动独立 ModuleHost 骨架、完成 `ready`/`health`/`shutdown` 行协议后退出；不安装、不启用、不写 `installed.json`，当前也不提供 HTTP 路由或提权。缺少 ModuleHost 可执行文件时返回 `module-host-executable-missing`。

安装/回退同时写入 `package-operations.json` 的 `AwaitingRestart` 回执；Server 启动完成、`ApplicationStarted` 后按 `installed.json` 的精确版本/releaseId/artifactSha256 核对为 `Succeeded` 或 `Failed`。这不是自动重启；更新操作的启动崩溃回退见下。

Host 在加载已安装模块前写 `package-startup.json` 标记，成功到达 `ApplicationStarted` 后删除。若上次进程未删除标记，Host 会把带 `previousVersion` 的待重启更新回退到旧版本并标记 `module-package-operation-startup-rollback`；首次安装没有历史版本时把安装记录标为 `quarantined`，不发布能力、不加载程序集，并标记 `module-package-operation-startup-quarantined`，避免启动循环。

list-versions 只读版本目录的 `install.json`，不加载程序集。rollback-installed 要求 Host 已停止、管理员/root，重新核对目标版本文件哈希后把 `installed.json` 当前版本切回该版本；不修改期望启用集合、不自动重启。

remove-installed 只移除 `installed.json` 记录，默认保留版本文件；模块仍在期望启用集合中时拒绝移除。list-installed 是只读安装事实，不读出生效状态。


## v2 唯一当前清单（2026-10-31）

`ModulePackageManifest` 只接受 schemaVersion=2，新增必填 `runtime`、`privileges`、`ui`。旧 v1 清单、缺字段、未知运行模式或未知提权类型都拒绝；不保留双解析。发布者身份继续使用 `publisherId`，不接收 `publisher` 别名。签名目录 envelope/payload、来源策略仍是其各自的 schemaVersion=1 与 `ServerModuleCatalog.v1` 签名域；它们不是模块清单版本。

- `runtime=in-process` 只接受 `purpose=official`，继续使用进程内 ABI 1.0.0。
- `runtime=module-host` 使用 ModuleHost ABI 2.0.0，不能公布 Core capabilities；来源可为 Host 授权的 `third-party`、`development` 或 `official`。开发来源仍需绑定服务身份与开发环境；第三方不得使用 `relaxkonos.*` 前缀。
- `privileges` 是 `{kind,target}` 数组；目前仅允许 `service-start`、`service-stop`、`service-restart`。执行时仍须 Broker 确认及 Host/Helper 双重服务白名单。
- `ui` 必含 `name`、`summary`、`description`、可空 `homepage`、`license`。homepage 非空时只允许 HTTPS。它们不参与来源授信判断。
- 安装事实 `installed.json` 与冻结安装计划 `package-plans.json` 同步升级 schemaVersion=2。每版本 install.json 必须保留 runtime；已安装清单按原始 manifest SHA-256 和全部文件摘要复查。旧安装事实不会静默转换。
- 在线 ModuleHost 安装必须使用冻结计划；直接 install 路由拒绝它。apply 是对预览中精确版本、摘要、运行模式和提权声明的代码信任确认，写入独立的 module-trust.json。离线安装仅记录文件事实，不自动信任或执行代码。回退/更新不能借用其他摘要的代码信任。
- `POST /api/v1.0/server/module-trust/{moduleId}` 使用 `{artifactSha256,manifestSha256,trusted}` 修改精确安装的代码信任；要求 ServerModulesManage 且 managed 管理模式。撤销立即阻断代理和 Broker，监督周期内关闭原进程。

Linux 隔离启动、Broker 实施证据和未完成项见 [v2 实施记录](RelaxKonOS.ServerModuleV2.Progress.md)。
