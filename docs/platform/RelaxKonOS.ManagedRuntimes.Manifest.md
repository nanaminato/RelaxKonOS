# 受管运行时签名目录与本机信任配置

> 实现边界：2026-10-17。本文说明 MR-01 已接通的 HTTPS 签名目录发现链路，以及尚未接入安装的公共归档/制品准备组件；运行时安装适配器、远端发布源配置界面、下载代理及 GitHub Releases 适配器仍待实现。目录发现成功不代表其中版本可安装。

## 信任配置

Helper 使用独立的受保护宿主账本：Windows 为 `%ProgramFiles%\RelaxKonOS-RuntimeTrust`，Linux 为 `/etc/relaxkonos/runtime-sources`。只有本机提升的管理员/root 可以确认来源；Server 只能通过封闭的 Helper 操作查询和刷新已登记源，不能提交 URL、公钥或摘要建立信任。Windows 核对管理员/SYSTEM 所有权和写权限；Linux 核对 root 所有权及组/其他用户不可写，拒绝链接路径。已有目录权限不满足要求时拒绝使用，不自动接管。

本机管理员创建 JSON 策略文件，字段与 `Shared/RelaxKonOS.ManagedRuntimes/RuntimeSource.cs` 的唯一当前契约一致：

| 字段 | 含义 |
| --- | --- |
| `sourceId`、`runtimeId`、`publisher` | 明确的来源、运行时和发布者；已有 sourceId 不能更换运行时或发布者 |
| `kind` | 当前仅 `HttpsManifest` |
| `baseUri`、`manifestPath` | HTTPS 元数据目录基址（以 `/` 结束）和相对清单路径 |
| `keyId`、`publicKeySpki` | 管理员独立取得的 ECDSA P-256 密钥标识及 Base64 DER SubjectPublicKeyInfo；不是 PEM |
| `enabled`、`trustRevision` | 启用状态及当前信任修订；确认时由账本递增，输入不能指定生效修订 |
| `channels` | `Stable` 和可选 `Prerelease`；返回给普通客户端时默认过滤预发布版 |
| `maximumMetadataBytes` | 签名信封总字节上限，1024 至 4 MiB |
| `maximumValiditySeconds` | 清单自身有效期上限，60 秒至 31 天；不是刷新间隔 |
| `downloadPrefixes`、`redirectPrefixes` | 明确 HTTPS 下载/重定向目录前缀，均以 `/` 结束；约束主机、端口及路径边界 |
| `privateNetworks` | 内网镜像的 `{ "cidr": "10.20.0.0/16", "port": 8443 }` 授权列表，默认 `[]`；仅 RFC1918/IPv6 ULA 范围 |

不接受认证秘密字段。当前下载器直接连接 HTTPS，不读取进程代理环境、不携带凭据、不支持认证源。不能把这个限制解释为已支持宿主下载代理。

确认步骤（命令中的路径和指纹由管理员替换）：

```text
RelaxKonOS.PrivilegedHelper runtime-source inspect policy.json
RelaxKonOS.PrivilegedHelper runtime-source confirm policy.json 0 <inspect返回的confirmationFingerprint>
RelaxKonOS.PrivilegedHelper runtime-source list
```

`inspect` 只读取候选文件，显示完整策略及其 SHA-256 指纹，不修改信任。管理员必须核对身份、地址、公钥、允许位置和内网范围后再执行 `confirm`；新来源预期修订为 `0`，后续使用账本当前修订。指纹覆盖整个策略（仅将输入 trustRevision 归零），修改任一权限都需要重新确认。禁用来源、密钥轮换同样通过完整策略确认；不提供静默默认密钥、自动扩权、删除来源历史或重置信任高水位的普通接口。

账本原子写入信任修订、确认者和时间；目录高水位、不可变版本身份、冲突和撤销信息不会因禁用、密钥轮换、重启或清单删除旧条目而清空。目录下载不持有账本锁，接受结果时重新核对信任修订。

## 发布清单格式

唯一格式为严格 UTF-8 JSON；拒绝重复属性、未知字段、缺少必需字段、越界深度和超限正文。无 HTML/autoindex、旧格式或任意脚本解析回退。

外层 `SignedRuntimeCatalog`：

```json
{
  "schemaVersion": 1,
  "keyId": "publisher-key-1",
  "payload": "<Base64编码的RuntimeManifest原始UTF-8字节>",
  "signature": "<Base64编码的64字节IEEE-P1363签名>"
}
```

签名输入是 UTF-8 常量 `RelaxKonOS.RuntimeCatalog.v1\n`（最后为一个 LF 字节）拼接解码后的 payload 原始字节；采用 ECDSA P-256 / SHA-256、IEEE-P1363 固定长度 `r || s` 格式。验证时不重新序列化 payload。发布者需在宿主之外保管私钥；Helper 只取得公钥。测试中的签名构造见 `Tests/ManagedRuntimes/CatalogChecks.cs`。

payload 的 `RuntimeManifest` 字段为 `schemaVersion: 1`、`sourceId`、单调增加的正整数 `revision`、`issuedAt`、`expiresAt`、`releases`、`revokedReleaseIds`。时间使用 JSON DateTimeOffset 格式；有效期上限由本机策略决定，签发时间最多允许超前五分钟。相同修订只能再次接受相同 payload 字节，低于高水位或同修订不同内容分别拒绝为重放/冲突。

每个 `ManifestRelease` 包含：

- 不透明的 `releaseId`、固定 `runtimeId`、原始 `originalVersion`、`channel`、可空 `publishedAt`。
- `target`：`os`、`architecture`、可空 `distribution`/`libc`、`installMode`、`serviceHost`，只作明确身份匹配，不猜测资源命名或架构别名。
- `artifact`：`artifactId`、64 位十六进制 `sha256`、`maximumBytes`、`format`、`entryPoint`、`verificationIdentity`、`layout`。format 为 `zip` 或 `tar.gz`，verificationIdentity 必须等于来源 keyId；包管理器不能使用此归档适配器。
- `artifact.layout`：`maximumExpandedBytes`（解压文件总量，最多 2 GiB）、`maximumEntries`（含目录，最多 4096）、`files`。每个文件明确 `path`、`length`、`sha256`、`executable`；入口必须恰好匹配一个声明的可执行文件。允许目录仅由这些文件的祖先目录推导。当前契约要求 layout，缺少时直接拒绝，不提供旧格式回退。
- `downloadUri`：必须落在受信任 downloadPrefixes 内，不能凭签名自行扩大来源权限。

清单不接受配置能力、已验证互通、安装许可或依赖声明。这些结论属于本机运行时适配器：依赖要求由适配器从已验证制品与将要发布的配置推导，宿主实测后才进入计划（见[宿主依赖预检](RelaxKonOS.ManagedRuntimes.Transactions.md#宿主依赖预检)）。目录条目是否可安装由本机生命周期适配器决定：sing-box 目前把 `1.13.0 ≤ v < 1.15.0` 的稳定版（能力 `sing-box-v1`）标为配置支持，其余（更旧、`1.15` 及以后、预发布）仍为配置不支持。清单里的 `evidence` 只是发布者自述，不改变这条判定。sing-box 的明确 SemVer 标签（可带一个官方 v 前缀）取得排序版本；其他运行时或未知名称保留原始版本，不猜测其排序。版本可排序不代表能安装或配置兼容。

sing-box 目录按数值/预发布标识比较后降序返回，构建元数据保留但不参与优先级，超大数值使用任意精度；默认仍隐藏预发布版。已识别版本的预发布属性与清单 channel 不一致时拒绝接受该目录。规则依据 [SemVer 2.0.0](https://semver.org/spec/v2.0.0.html)，不用于系统包发行版修订或其他未声明使用 SemVer 的运行时。

## 归档验证与制品准备组件

`RuntimeArchiveExtractor` 先对整个可定位归档流做受限读取和 SHA-256 校验，再创建宿主保护暂存根下的随机子目录。只写声明中的普通文件，并逐文件核对准确长度和 SHA-256；拒绝路径穿越、绝对/驱动器/ADS 路径、Windows 设备名、尾随点、大小写别名、重复入口、链接、设备/FIFO、未声明文件与多余空目录。归档权限、属主、setuid 等元数据不会被应用；新目录和文件使用宿主私有权限，可执行位只来自签名声明。

ZIP 在创建 ZipArchive 条目集合前先限制中央目录条目数和各元数据长度，仅接受经典单卷 stored/deflate ZIP，拒绝加密、ZIP64 及不支持的条目类型。tar.gz 使用有界块读取，核对 tar 校验和、文件大小与结束记录，并计算全部膨胀字节（含目录、头和填充）；接受普通 USTAR/GNU/V7 文件/目录，拒绝 PAX、GNU 长名称/其他扩展、base-256 大小字段及拼接的非零尾随负载。上游归档超出此明确范围时应报告不支持，不能放宽安全规则或猜测提取。

`RuntimeArtifactPreparer` 从受保护账本解析 sourceId/releaseId、平台及预期修订，要求来源启用、目录有效、制品未撤销/冲突、配置能力已由本机适配器允许，预发布还须显式选择。使用同一 HTTPS/DNS/重定向策略将归档流式写入随机私有文件，正文总时限四分钟，写入层也独立限制最大字节；之后由归档校验器再次核对内容。完成后重新授权同一明确目标和修订；来源禁用、过期、撤销或修订变化会拒绝本次准备，不改选其他版本。

下载临时文件在完成/失败/取消时删除，解包失败只清理本次随机子目录；已有调用方文件保留。解包成功由调用方持有 `VerifiedRuntimeArchive`，可在同一暂存根中显式 Discard。另已实现 [持久归档事务组件](RelaxKonOS.ManagedRuntimes.Transactions.md) 的活动指针、幂等和补偿框架；保护暂存根、恢复时寻找本次准备、原生版本/配置检查和真实服务健康仍由待接入的 Helper 运行时适配器负责。生产目录只对声明范围内版本标为配置支持（见[真实跨版本与协议验收](RelaxKonOS.ManagedRuntimes.Transactions.md#真实-sing-box-跨版本与协议验收)），范围外仍为不支持。

相同 releaseId 或原始 tag/target 的制品身份改变会持久化冲突，保留原始身份并阻止新旧相关条目；换 releaseId 不绕过隔离。撤销记录同时保留已知制品摘要，换标签或 releaseId 不能重新开放同一已知撤销制品。普通刷新和重新确认来源不清除冲突或撤销；管理员审查解除流程尚未实现。

## Server 查询与刷新

| 路由 | 权限/行为 |
| --- | --- |
| `GET /api/v1.0/managed-runtimes/sing-box/installation` | RuntimeRead；返回安装状态及明确操作回执 |
| `POST /api/v1.0/managed-runtimes/sing-box/plan` | RuntimeManage 与 SingBoxInstall；从保护目录生成冻结计划 |
| `GET /api/v1.0/managed-runtimes/mihomo/recovery` | RuntimeRead；合并 Helper 恢复事实与 Server 未确认提交，不清除冲突操作身份 |
| `POST /api/v1.0/managed-runtimes/mihomo/recovery/retry` | RuntimeManage、Mihomo 安装权限与 MihomoInstall 对应提权；按命名 Pending 分派安装/配置/卸载发布恢复，否则恢复 Helper 原事务，不重新准备 |
| `GET /api/v1.0/managed-runtimes/sing-box/recovery` | RuntimeRead；返回待恢复事务、原始/补偿失败码与实测服务状态，不写台账 |
| `POST /api/v1.0/managed-runtimes/sing-box/recovery/retry` | RuntimeManage 与 SingBoxInstall；重新补偿同一事务，不重新准备 |
| `POST /api/v1.0/managed-runtimes/sing-box/recovery/stop` | RuntimeManage 与 SingBoxInstall；只停服务并关闭开机启动，不读凭据 |
| `POST /api/v1.0/managed-runtimes/sing-box/recovery/close` | RuntimeManage 与 SingBoxInstall；仅在补偿失败、显式确认且实测服务未运行时关账 |
| `GET /api/v1.0/managed-runtimes/sources` | RuntimeRead；返回非秘密来源身份、类型、地址、确认指纹和目录修订 |
| `GET /api/v1.0/managed-runtimes/sources/{sourceId}/catalog` | RuntimeRead；返回缓存，不触发网络请求 |
| `POST /api/v1.0/managed-runtimes/sources/{sourceId}/refresh` | RuntimeManage；Helper 以自身保护的策略下载、验证并原子接受目录，不安装软件 |

RuntimeRead 允许 controller/observer；RuntimeManage 仅允许 controller，均要求宿主支持 PrivilegedOperations。目录查询/刷新默认隐藏预发布版，显式 `?includePrerelease=true` 才显示。当前没有远端新增/修改信任路由。三条恢复写路由另有该操作员的新鲜提权要求，缺失时返回 `elevation-required`；关账还必须同时满足补偿已失败、请求体 `"confirmed":true` 和实测服务未运行，详见[人工恢复入口](RelaxKonOS.ManagedRuntimes.Transactions.md#人工恢复入口)。取消下载**没有**新的运行时路由：它复用台账既有的 `POST /api/v1.0/installations/{operationId}/cancel`，只有主机把准备阶段报为可取消时台账才接受，并把它转成一次 `SingBoxRuntimeCancel` 请求，详见[下载取消](RelaxKonOS.ManagedRuntimes.Transactions.md#下载取消)。

快照分别报告 `catalog`、`fresh` 和 `problemCode`：未取得目录返回 `runtime.catalog_missing`；过期缓存仍可显示；刷新失败保留最后已接受目录，并附带失败码，即使缓存尚未过期也不得报告“已是最新版”。新信任修订必须刷新目录后才能恢复新鲜状态。

下载器的总时限为 45 秒，最多三次重定向，限制响应头声明及实际读取长度，拒绝压缩响应；每跳校验 HTTPS 地址范围和所有 DNS 结果，连接到已验证 IP，保留原主机名进行 TLS 验证。默认拒绝回环、私网、链路本地、元数据、组播及特殊地址；私网镜像需要精确网段/端口授权，该授权不开放回环或元数据地址。限流、网络不可达、超时、签名错误、过期、重放及摘要冲突各有独立状态。自动重试、API 分页及代理下载尚未实现。

## 验证范围与剩余项

`dotnet run --project Tests/ManagedRuntimes/RelaxKonOS.ManagedRuntimes.Tests.csproj` 当前在 Windows 覆盖 385 项，包含原有 358 项及 27 项 Windows 路径/状态/启动门禁/PID 监听检查；实际 SocketsHttpHandler 在连接前拒绝回环元数据请求，事务测试包括四处子进程退出后的磁盘恢复，监听测试只创建本进程临时回环 TCP/UDP。没有修改系统服务、向生产信任目录写入或安装真实运行时。

Server 和 Helper 编译通过，代理服务端既有测试通过。通用 Server 测试在 Windows 的证书 PEM 私钥导入处失败（`CertificateBindingChecks` / CNG），未完成整套通过，不能作为本次生命周期验收证据。Linux 的文件所有权检查及真实 TLS 镜像、跨平台安装/更新/恢复仍需平台验收。

另有 [sing-box 归档适配器可选检查](RelaxKonOS.ManagedRuntimes.Transactions.md)：Windows x64 上以固定摘要的 1.14.3 缓存实际执行 version/REALITY 生成/check，通过 12 项；平台服务仍被模拟。该适配器已接入生产路由；公共目录的配置支持标记只对 `sing-box-v1` 范围内的稳定版为 true，没有因此扩展平台或协议兼容范围。

2026-10-18 已补充 Helper 固定生命周期组合、本机 Query/Plan/Execute/Recover 操作、Server 新鲜授权提供者/组合服务与每操作认证通道；固定版配置路径已与归档事务使用同一实例锁；受管活动修订上的已发布配置发布、启停、开机启动与配置回滚已接通；卸载按保留/删除数据走独立归档移除事务；补偿失败后的重试/紧急停止/关账三条人工恢复入口与对应客户端面板已接通；准备阶段的下载取消（台账取消 + 特权取消请求 + 客户端取消按钮）已接通；宿主依赖预检（平台/服务宿主/磁盘/受管目录/IPv6）在计划与前置检查两处生效，读权限接口随安装快照返回宿主环境事实；客户端版本计划页与宿主环境面板已接入。Debian 13 x64 通过 608 项常规检查及 12 项真实 systemd 测试程序检查，范围见[事务证据](RelaxKonOS.ManagedRuntimes.Transactions.md#linux-固定宿主与进程证明)。这没有开放目录安装或扩大受支持版本，当前公开目录的配置支持仍全部为 false。

已接通既有 Server 安装台账及冻结计划入口，并接通受管修订上的已发布配置发布/服务控制、卸载/数据保留、人工恢复入口与宿主依赖预检；真实 sing-box 跨版本更新与 VLESS/REALITY 协议互通已在 Linux x64 上验收（见[事务证据](RelaxKonOS.ManagedRuntimes.Transactions.md#真实-sing-box-跨版本与协议验收)），下一阶段接其余运行时适配器。此文不替代 [整体设计及任务表](RelaxKonOS.ManagedRuntimes.Design.md)。

执行复用 `/api/v1.0/installations/SingBox/{Install|Upgrade|Repair|Rollback}`，提交严格 `ManagedRuntimeInstallationRequest`；提权目标为 `singbox`。不另建安装台账。公共目录配置支持仍为 false，新增路由不开放未经验证的版本。
