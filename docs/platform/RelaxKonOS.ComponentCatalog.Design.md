# 本地声明式组件目录

> 2026-11-02：已实现本地声明式目录、版本/制品选择和桌面包安装链路。依据 [PR #43](https://github.com/nanaminato/RelaxKonOS/pull/43) 的 Phase 1 方向；下文明确区分当前契约与后续设计，不把提案示例当作已实现 API。

## 当前实现

`Shared/RelaxKonOS.ManagedRuntimes/ComponentCatalog/` 目录中的 JSON 与 `.roapp` 随引用该库的 Client、Server 和发布产物复制到 `ComponentCatalog/`。入口固定为 `AppContext.BaseDirectory/ComponentCatalog/catalog.json`，不依赖工作目录或网站，不扫描用户目录。它是随产品部署的元数据，不是新的可信包商店。部署方必须保护该目录，不能让不受信任的进程并发改写它。

`Catalog/ComponentManifest.cs` 定义唯一当前 JSON 契约，`LocalCatalogSource` 严格读取：

- 索引 `schemaVersion: 1`、`components: [{ id, manifest }]`、`workloads`；
- manifest 的身份、显示/本地化键、关联应用、依赖/冲突、集成程度和 `targets`；
- 每个 target 明确声明 `os`、`architectures`、`sourceKind`、`installer: { type, available }`、`officialDownloads`、`releases` 和 `artifact`；
- provider 类型仅限 `apt`、`archive`、`download`、`roapp`；未知字段、重复 JSON 属性、缺失必填字段、重复身份、索引/manifest 身份错配、未知 schema、无效目标及未解析依赖均拒绝；单文件上限 256 KiB、深度 24、组件最多 256 个。

十个原有宿主组件与两个工作负载已经全部移出桌面 C# 目录。`ComponentCatalog.cs` 只投影解析后的声明，继续生成原有列表、来源标签、工作负载和应用依赖；没有硬编码目录回退。官方归档入口也从 target 的 `officialDownloads` 派生，保留原来的四种文件/下载方式。`available` 仅描述集成程度，不授予权限。

参考实际文件：`ComponentCatalog/catalog.json`、`manifests/nginx.json`、`manifests/mihomo.json`。Mihomo 保留 Linux/Windows 官方归档入口；Nginx 保留 Linux 系统包及 Windows 官方归档入口。所有这些 target 声明 x64/arm64；这是当前软件路径接受的目标范围，不是新的实机验证结论。

## 相对引用与安全边界

`manifest` 相对索引所在目录解析；`ResolveReference(reference, declaringFile)` 也支持相对声明文件定位。规范化后的声明位置和目标必须位于根目录内。允许仍在根内的 `../`，拒绝越界、绝对路径、盘符/UNC、反斜线、URL、百分号编码、查询/fragment、ADS 和不明确的尾空格/点。读取前检查文件及全部祖先，拒绝符号链接、junction 和其他 reparse point，即使链接当前指向根内。

本切片只接受部署拥有的静态本地目录，不提供不受信任目录的并发写入隔离、第三方来源注册或远程 Source。解析得到的本机路径不传给 Helper。manifest 不接受 shell、PowerShell、命令行、脚本、下载 URL、公钥或任意可执行路径；也不能动态加载 provider。声明不会改变固定包名、受管目录或服务名。

## 兼容性与 provider 派发

`CompatibilityResolver` 根据真实目标宿主的 OS/架构及 provider 可用性返回 `Supported`、`UnsupportedPlatform`、`ProviderUnavailable`、`PolicyBlocked` 或 `Unknown`。环境缺失保留 Unknown，不推断“未安装”。Server 用自身 `OSArchitecture`，不使用远程桌面所在机器的架构；发行版、包管理器、服务宿主、权限、信任和源修订继续由领域预检核对。

`ComponentInstallerProviderRegistry` 是封闭注册表，按安装机制派发：

| 类型 | 当前宿主桥接 |
| --- | --- |
| `apt` | 原 `IComponentLifecycleAdapter` 系统包计划与执行；package ownership、冻结候选和 Helper 事务保持权威 |
| `archive` | 原受管归档适配器；Mihomo 发布协调器和 FRP/Nginx 安装域保留各自的拒绝/执行语义 |
| `download` | 原 NativeInstaller 模式适配器；当前 Tailscale MSI 的可信发现与安装仍在领域实现 |
| `roapp` | 桌面 `RoappComponentInstallerProvider` 暂存本地精确制品，交给原 AppInstaller 界面审查并显式安装；Server 不注册桌面安装器 |

通用组件 plan 路由、Nginx 冻结执行、系统包执行与 FRP 冻结复核已经改经 provider 派发。请求的组件、源/信任修订、明确版本/release、状态修订、幂等键和冻结期望原样交给领域代码；派发层不刷新 latest、不自行生成可执行计划、不扩大权限、不把领域拒绝改为成功。执行前再次解析机制/兼容性，然后由原领域重规划/身份复核与安装台账处理审批、排他、恢复和进度。

已有官方文件安装入口、签名归档 TaskOrchestration 和应用专有协调器继续拥有自身领域语义；它们没有被替换成通用 shell。新增 manifest 可以复用已注册能力，但不能凭声明获得尚不存在的领域后端或权限映射。

## 版本发现与制品选择

`IComponentReleaseProvider` 与 `IComponentArtifactSelector` 各有封闭注册表；manifest 只能组合已实现的机制，不能下载实现代码。当前类型：

| 版本来源 | 制品选择 | 当前接入 |
| --- | --- | --- |
| `github-releases` | `pattern` | Mihomo/FRP 复用固定官方仓库；原分页、稳定版筛选、大小/时间上限与缓存保持；一次最多三页、每页五条，取得匹配批次即停 |
| `nginx-official` | `pattern` | 复用 nginx.org 页面解析和数字版本排序 |
| `apt` | `package` | 规范化现有 Helper/领域已经解析的确切候选及依赖；APT 安装 provider 消费同一次冻结结果，不再次查询 latest，不把 `.deb` 当普通归档下载安装 |
| `catalog` / `static` | `catalog-artifact` | 本地 `.roapp` 的明确版本、相对文件和 SHA-256 |
| `tailscale-official` | `native` | 复用原 Tailscale MSI 官方解析 |

`pattern` 只支持 `{version}`、`{versionNumber}`、`{os}`、`{arch}` 四种有限占位符，不接受 regex 或表达式。选择必须唯一，重复/歧义资源拒绝；不能把别的平台文件当匹配项。GitHub 资源地址必须与已批准仓库、tag 和文件名的精确地址一致。宿主领域的原发布者/制品规则仍独立复核，修改本地 JSON 的 repository 或模板不能扩大已有组件信任。元数据请求拒绝重定向；既有制品下载传输与合法 CDN 重定向行为保持。

现有 `ComponentDownloadRelease` HTTP 契约仍投影到原调用者，Android 不增加第二套目录契约；APT 界面继续使用原领域冻结计划。执行不会重新发现 latest 再静默替换版本。新的上游协议/发布者仍需宿主实现和独立授权，不因一个 manifest 自动获得安装许可。

## 本地 `.roapp` 计划与审查

同一 manifest 不能混合宿主与桌面安装。桌面 target 的 `installer.type` 为 `roapp`，`sourceKind` 为 `DesktopApp`，`releases.type` 为 `catalog` 或 `static`，`artifact.selector` 为 `catalog-artifact`，并明确声明 `format: roapp`、`reference`、`version`、`sha256`；`reference` 相对其 manifest 解析，可使用仍在根内的 `../packages/example.roapp`。应用身份使用 `applicationId`（未指定时使用组件 ID），必须与包内身份和版本一致。

桌面平台/进程架构用于本机包的兼容性检查，远端 Server 平台不能替代它。组件中心按相同目录生成列表，桌面包不会被加入宿主组件 launch gate，也不会向 Server 查询一个虚构的安装状态。已安装版本来自原 DeveloperPackageManager。

点「检查软件包」仅生成 `RoappComponentInstallPlan`：先按大小上限复制到私有暂存文件，校验摘要和包内身份/版本，再打开原 AppInstaller 审查来源、版本及申请权限。没有自动安装，也不把本地来源称为签名商店或 BuiltIn。安装时在串行锁内重新核对已安装版本与摘要，把同一个已验证文件句柄交给原安装域；计划后文件变化或其他安装造成版本变化即拒绝。安装更新仍重置权限决策，包不因进入目录而获得沙箱或额外信任。

## 已安装事实与信任

状态读取直接访问领域适配器，不要求目录仍然包含该组件。卸载、修复、回退的 provider 选择不加载当前目录，继续按保存的领域身份、当前授权和所有权检查处理；不承诺领域尚不支持的动作。目录缺失/损坏会阻止新的目录驱动安装/更新，不会抹除安装台账或将查询失败显示为“未安装”。

[Package Store](RelaxKonOS.PackageStore.Design.md) 继续拥有发布者、公钥、签名、撤销、sequence、有效期、反回退、缓存、来源和下载策略。本地组件描述不具有商店包授信意义，也不替代签名运行时或官方下载安装域的产品规则。没有新增自动信任文件、任意源注册或 Helper 许可通道。

## 后续计划

Phase 2/3 的 HTTP(S) Source、签名商店目录刷新、第三方来源注册与授权 UI 仍未实现；不要求网站即可使用本地阶段。更丰富的发行版/API 能力声明与其他包管理器仍按领域需求扩展。未来远程 Source 必须完整复用 Package Store 的信任、撤销、反回退与下载约束，不能把本地读取器直接改成任意 HTTP 文件下载器。

本次验证与剩余实机范围见[组件中心实施进度](RelaxKonOS.ComponentCenter.Progress.md)。目录/派发测试不构成新的真实安装、跨版本升级、回退或重启恢复验收。
