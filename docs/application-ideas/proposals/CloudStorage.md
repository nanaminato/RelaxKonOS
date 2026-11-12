# APP-008 云存储与内置文件浏览器

状态：已实现可选 ModuleHost 模块与桌面 Explorer 接入。真实 WebDAV 和 Cloudflare R2 已通过 Ubuntu 实机 ModuleHost 读写验收。真实 AWS S3 端点验收待执行。

## 首版范围

首版交付两个连接器，验收覆盖三个目标。一个 S3 兼容连接器同时接入 AWS S3 和 Cloudflare R2，各自通过真实端点验证后再列入支持名单。

| 验收目标 | 接入与授权 | 首版边界 |
| --- | --- | --- |
| WebDAV | 配置 HTTPS 端点、授权根目录和服务账号凭据。由连接器列举目录、读取属性和流式下载。 | 使用一个真实 WebDAV 服务验证目录和文件名编码、根目录限制及只读权限。写入能力按该连接的授权和服务端行为单独验证。 |
| AWS S3 | 配置区域、桶和可选键前缀，使用 AWS Signature Version 4。只读凭据允许列举指定桶前缀及读取该前缀下的对象。 | 使用真实通用桶验证 `ListObjectsV2`、续页、前缀与对象读取。对象前缀呈现为目录，写入使用单独授权。 |
| Cloudflare R2 | 使用同一 S3 连接器，配置账户的 R2 S3 API 端点、区域 `auto`、桶和可选键前缀。使用限定到目标桶的 R2 S3 API 凭据，并由连接器限制连接根前缀。 | 使用真实 R2 桶单独验收。对象操作按 R2 的兼容矩阵和实测结果启用，分片、校验和与条件写入分别验证。 |

参考 [WebDAV 协议](https://www.rfc-editor.org/rfc/rfc4918)、[AWS S3 列举接口](https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectsV2.html) 和 [R2 S3 API 兼容矩阵](https://developers.cloudflare.com/r2/api/s3/api/)。

## OAuth 网盘和后续候选

百度网盘已接入目录浏览、下载、新建目录和上传。连接默认只读，只有在应用和用户授权允许写入时才启用上传与新建目录。首轮使用注册应用的客户端 ID、客户端密钥和已签发的刷新令牌，由 ModuleHost 自动刷新访问令牌，并将轮换后的刷新令牌写入加密连接记录。百度设备码授权已实现，供应商账户验收和 OneDrive 接入仍待完成。Explorer 中的撤销移除本地连接，应用授权也可在供应商账户设置中撤销。

### 百度网盘

- **接入**　使用百度网盘开放平台的文件列表和元信息 API。下载经文件元信息返回的临时链接，模块仅允许 HTTPS 的百度网盘下载域名及同范围重定向。
- **写入**　文件夹使用创建接口。上传使用预创建、4 MiB 分片和最终创建接口，提交前校验每个已上传分片返回的 MD5。创建请求使用 `rtype=0`，同名冲突返回 409。可写目录和实际容量上限由应用权限和账号决定。
- **授权**　在开放平台注册应用，按平台要求申请接入和文件操作权限。用户通过百度 OAuth 授权，服务端保存并按平台规则刷新令牌。
- **限制**　立项时用实际获批应用和测试账号核验可见目录、写入目录、下载条件、配额和限流。应用专属目录与整盘访问需要分别确认，产品按获批范围显示根入口和操作。

参考 [百度网盘开放平台](https://yun.baidu.com/union/openAbility/openCapabilityDetails) 和 [百度网盘文件列表接口](https://pan.baidu.com/union/doc/nksg0sat9)。

### OneDrive

- **接入**　使用 Microsoft Graph v1.0 的 Drive 和 DriveItem 接口，保存 drive ID 与 item ID，使用 `children` 分页列举并读取文件内容。个人版和企业版分别验证。SharePoint 文档库单列为后续验收范围。
- **授权**　注册 Microsoft Entra 应用并选择拟支持的账户类型，使用 OAuth 用户委托授权。只读连接从 `Files.Read` 起步，写入按需申请 `Files.ReadWrite`。企业租户的应用同意与管理员审批按租户策略核验。
- **限制**　令牌权限和文件的实际共享权限共同决定可见范围。企业版需验证租户策略、共享文件和文件操作差异。下载接口返回的预认证地址应按短期凭据处理，避免缓存或记录到日志。

参考 [Microsoft Graph 文件 API](https://learn.microsoft.com/en-us/graph/api/resources/onedrive?view=graph-rest-1.0)、[目录列举与权限](https://learn.microsoft.com/en-us/graph/api/driveitem-list-children?view=graph-rest-1.0) 和 [文件下载](https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0)。

## 安装与连接

云存储作为可选的服务端 `.rkmodule`，运行在隔离的 ModuleHost。用户可跳过安装。组件中心管理模块的安装、停止、重启和版本，连接管理负责各账户的授权和撤销。首版用一个模块交付 WebDAV 和 S3 兼容连接器，用户选择需要的连接。以后可按发布和依赖边界拆分成独立模块。

桌面 Explorer 通过认证代理发现 `cloud-storage` 模块。模块可用时，侧栏显示「云存储」和当前用户的各个连接根。窄窗口可从云存储面板内切换连接。模块暂不可用时，打开的面板保留导航上下文。撤销连接后移除对应入口。连接按所有者隔离。WebDAV 用 HTTPS 端点中的目录作为根，S3 用桶与键前缀作为根。

## 文件来源契约

首版使用 `CloudStorageApi` 的专用来源契约，经现有 ModuleHost 代理分发。Core 核对 ModuleHost 访问及文件读写权限，模块核对连接所有者和根范围。宿主文件保留现有文件接口。Explorer 使用连接 ID 和不透明条目引用导航。后续接入更多模块时再提取通用来源注册契约。

已实现分页列举、属性、流式下载，以及显式启用写入后的新建目录和不覆盖上传。S3 使用服务端续页令牌，WebDAV 对有界的 Depth 1 响应分页。对象存储的空目录使用带斜线的零字节对象。内置宿主文件选择器继续只显示宿主文件。

### 可恢复上传

桌面 Explorer 复用宿主文件上传的会话客户端、断线偏移对账和续传日志。云模块使用同一会话数据结构和暂存文件分块写入逻辑，并将会话限定到连接所有者、连接及其配置修订。分块上限 8 MiB，单文件上限 4 GiB，模块总暂存预留上限 8 GiB，空闲会话 24 小时后清理。块内中断后，以服务器记录的最后完整偏移续传。

上传完成后由 ModuleHost 后台发布，客户端查询同一提交回执。WebDAV 写入远端随机暂存文件，再用 `MOVE` 和 `Overwrite: F` 发布。S3 大于 16 MiB 时使用 16 MiB 上游分片，并在完成请求中设置 `If-None-Match: *`。发生同名冲突时保留原文件，并尝试清理远端暂存或中止分片上传。客户端到模块的接收阶段支持续传。模块到上游的发布阶段中断后，未确认的提交不会自动重放。

## 凭据和传输

WebDAV 使用 HTTPS Basic 认证，S3 使用 AWS SDK 签名，支持静态访问密钥和可选会话令牌。连接凭据由模块保存在私有目录的 AES-GCM 加密记录中，密钥文件由同一隔离目录的 OS 权限保护。备份和还原时需同时保护这两个文件。客户端只能读取连接摘要。出站连接限定到配置的 HTTPS 源并拒绝重定向、环回和元数据地址。本机接口默认拒绝。控制者显式启用内网 WebDAV 并填写本机私有 IPv4 字面地址时，可连接该地址和指定端口，继续执行默认 TLS 校验。撤销阻止新的请求，已开始的流可结束，上传暂存完成时会再次检查连接。

跨来源复制、覆盖、移动和删除仍为后续工作。跨来源复制计划由服务端传输任务流式搬运，记录源、目标、进度及核验结果。同步、系统级挂载和外部工具也留待后续阶段。连接器的启动保持共享数据可由上一版读取，便于启动失败后的模块版本回退。

## 构建和部署

发布 `ServerModules/RelaxKonOS.ServerModule.CloudStorage/RelaxKonOS.ServerModule.CloudStorage.csproj`，指定目标 RID 并使用框架依赖发布。按[模块打包说明](../../architecture/RelaxKonOS.ServerModulePackage.Format.md#离线打包命令)打包发布目录，使用下列参数。模块不声明宿主提权能力。

| 参数 | 值 |
| --- | --- |
| `--module-id` | `cloud-storage` |
| `--runtime` | `module-host` |
| `--entry-assembly` | `RelaxKonOS.ServerModule.CloudStorage.dll` |
| `--entry-type` | `RelaxKonOS.ServerModule.CloudStorage.CloudStorageModule` |

目标 Server 首次部署需包含云存储代理的读写权限检查，桌面客户端需包含 Explorer 面板。之后优先走模块包在线更新。连接者需要现有 ModuleHost 访问权限及对应的文件读写权限。真机测试沿用目标机器已接纳的测试来源和预配置连接，测试证据仅记录目标、版本、操作和结果。

本地回归入口为 `Tests/ServerModules` 的 `--cloud-storage-checks`。覆盖块内 TCP 断开、偏移续传、S3/R2 分片完成时的不覆盖条件，以及模拟 百度网盘的令牌刷新、目录读取、下载、新建目录、上传和同名冲突。宿主文件上传的 98 项检查也通过。Windows 本地使用临时签名包验证 ModuleHost 加载。

## 验收

- 真实 WebDAV 使用 Windows 本地 ModuleHost 和系统已信任的测试 CA。只读连接通过列举、下载和拒绝上传检查。读写连接通过新建目录、上传、列举、属性和下载检查。同名上传返回 409，原文件内容保持一致，远端暂存文件和测试目录均已清理。此项覆盖 ModuleHost HTTP 接口。
- Windows 本地 ModuleHost 连接真实 WebDAV 读写端点，完成 25,165,961 字节的上传和下载。截断第二块后，重新打开会话仍返回 8,388,608 字节的已确认偏移。同名提交返回 409，下载 SHA-256 与原文件一致，测试文件已删除。
- Ubuntu 26.04 实机复用已安装的 ModuleHost 可执行文件，只上传网盘测试包。通过真实 WebDAV 上传并下载 67,108,995 字节。第二块中途断开 TCP 并重启测试 ModuleHost 后，同一会话从 8,388,608 字节续传。重复提交同名文件返回 409，下载 SHA-256 与原文件一致。远端测试文件和临时部署目录已清理。CA 信任仅作用于测试进程，已安装 Core 的 PID 保持不变。本轮覆盖实机 ModuleHost 和 WebDAV 数据面，组件中心在线安装或版本切换未参与。
- Ubuntu 实机通过独立测试 ModuleHost 验证真实 Cloudflare R2 的列举、对象属性和分片上传。第二块中途断开 TCP 并重启测试 ModuleHost 后，同一会话从 8,388,608 字节续传，最终上传 33,554,563 字节。同名提交返回 409，随后用独立 S3 请求下载并核对 SHA-256，确认原内容未变。另一次通过 ModuleHost 完整流式下载 17,825,823 字节，SHA-256 一致。两个测试对象均已删除并复查为 404。已安装 Core 的 PID 保持不变，测试使用临时模块部署。
- 无模块、已安装无连接、只读连接、多连接、连接撤销和模块停止分别呈现正确状态。
- WebDAV、AWS S3、Cloudflare R2 分别用真实端点验证连接、目录列举、属性、流式下载和只读授权。AWS S3 与 R2 分别验证续页与桶前缀限制。WebDAV 验证根目录限制及大目录列举，并按实际实现核验 Explorer 分页。
- 上传和跨来源复制交付时，分别记录三个目标上实际通过的操作。至少验证覆盖规则、传输中断、权限不足和部分失败结果。
- 同一连接的授权隔离、凭据轮换、异常重定向、传输中断和文件选择器的来源限制均有回归覆盖。
- 更新连接器只中断目标模块。新版无法启动时，旧版恢复后可访问原连接。

关联 [Explorer 设计](../../applications/RelaxKonOS.Explorer.md)、[文件同步提案](FileSync.md) 和 [模块分发设计](../../architecture/RelaxKonOS.ServerModuleDistribution.Design.md)。
