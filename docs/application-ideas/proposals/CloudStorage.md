# APP-008 云存储与内置文件浏览器

状态：已实现可选 ModuleHost 模块与桌面 Explorer 接入。真实 WebDAV、AWS S3 和 Cloudflare R2 端点验收待执行。

## 首版范围

首版交付两个连接器，验收覆盖三个目标。一个 S3 兼容连接器同时接入 AWS S3 和 Cloudflare R2，各自通过真实端点验证后再列入支持名单。

| 验收目标 | 接入与授权 | 首版边界 |
| --- | --- | --- |
| WebDAV | 配置 HTTPS 端点、授权根目录和服务账号凭据。由连接器列举目录、读取属性和流式下载。 | 使用一个真实 WebDAV 服务验证目录和文件名编码、根目录限制及只读权限。写入能力按该连接的授权和服务端行为单独验证。 |
| AWS S3 | 配置区域、桶和可选键前缀，使用 AWS Signature Version 4。只读凭据允许列举指定桶前缀及读取该前缀下的对象。 | 使用真实通用桶验证 `ListObjectsV2`、续页、前缀与对象读取。对象前缀呈现为目录，写入使用单独授权。 |
| Cloudflare R2 | 使用同一 S3 连接器，配置账户的 R2 S3 API 端点、区域 `auto`、桶和可选键前缀。使用限定到目标桶的 R2 S3 API 凭据，并由连接器限制连接根前缀。 | 使用真实 R2 桶单独验收。对象操作按 R2 的兼容矩阵和实测结果启用，分片、校验和与条件写入分别验证。 |

参考 [WebDAV 协议](https://www.rfc-editor.org/rfc/rfc4918)、[AWS S3 列举接口](https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectsV2.html) 和 [R2 S3 API 兼容矩阵](https://developers.cloudflare.com/r2/api/s3/api/)。

## 后续候选

以下三个服务各需独立连接器。接入时可共用服务端 OAuth 授权回调、凭据保存、刷新和撤销机制。每个连接绑定授权账户及选定根范围，Explorer 按连接器和条目的实际能力启用操作。

### Google Drive

- **接入**　使用 Google Drive API v3，通过文件 ID 列举和读取条目。普通文件流式下载，Google Docs、Sheets 和 Slides 按支持的格式导出。共享云端硬盘需要单独处理。
- **授权**　注册 Google OAuth 应用并使用用户委托授权。访问应用创建或用户明确选定的文件时优先使用 `drive.file`。若产品要求浏览和下载整个网盘，评估受限范围 `drive.readonly` 及适用的应用验证和安全评估。
- **限制**　授权范围决定可见文件集合。`files.export` 的响应内容上限为 10 MB，导出格式取决于文件类型。共享云端硬盘和外部共享文件需要分别核验组织策略及下载权限。

参考 [Drive API 授权范围](https://developers.google.com/workspace/drive/api/guides/api-specific-auth) 和 [下载与导出](https://developers.google.com/workspace/drive/api/guides/manage-downloads)。

### 百度网盘

- **接入**　使用百度网盘开放平台的文件 API。先验证授权目录列举、文件信息和下载，再根据应用获批能力评估上传。
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

已实现分页列举、属性、流式下载，以及显式启用写入后的新建目录和不覆盖上传。S3 使用服务端续页令牌，WebDAV 对有界的 Depth 1 响应分页。上传上限为 16 MiB，使用 ModuleHost 私有暂存文件校验长度后提交。对象存储的空目录使用带斜线的零字节对象。内置宿主文件选择器继续只显示宿主文件。

## 凭据和传输

WebDAV 使用 HTTPS Basic 认证，S3 使用 AWS SDK 签名，支持静态访问密钥和可选会话令牌。连接凭据由模块保存在私有目录的 AES-GCM 加密记录中，密钥文件由同一隔离目录的 OS 权限保护。备份和还原时需同时保护这两个文件。客户端只能读取连接摘要。出站连接限定到配置的 HTTPS 源并拒绝重定向、环回、本机接口和元数据地址。内网 IPv4 WebDAV 需要控制者显式启用。撤销阻止新的请求，已开始的流可结束，上传暂存完成时会再次检查连接。

跨来源复制、覆盖、移动、删除和大文件分片仍为后续工作。跨来源复制计划由服务端传输任务流式搬运，记录源、目标、进度及核验结果。OAuth 网盘、同步、系统级挂载和外部工具也留待后续阶段。连接器的启动保持共享数据可由上一版读取，便于启动失败后的模块版本回退。

## 构建和部署

发布 `ServerModules/RelaxKonOS.ServerModule.CloudStorage/RelaxKonOS.ServerModule.CloudStorage.csproj`，指定目标 RID 并使用框架依赖发布。按[模块打包说明](../../architecture/RelaxKonOS.ServerModulePackage.Format.md#离线打包命令)打包发布目录，使用下列参数。模块不声明宿主提权能力。

| 参数 | 值 |
| --- | --- |
| `--module-id` | `cloud-storage` |
| `--runtime` | `module-host` |
| `--entry-assembly` | `RelaxKonOS.ServerModule.CloudStorage.dll` |
| `--entry-type` | `RelaxKonOS.ServerModule.CloudStorage.CloudStorageModule` |

目标 Server 首次部署需包含云存储代理的读写权限检查，桌面客户端需包含 Explorer 面板。之后优先走模块包在线更新。连接者需要现有 ModuleHost 访问权限及对应的文件读写权限。真机测试沿用目标机器已接纳的测试来源和预配置连接，测试证据仅记录目标、版本、操作和结果。

本地回归入口为 `Tests/ServerModules` 的 `--cloud-storage-checks`。已通过 41 项检查，用本地 HTTP 宿主与模拟上游分别验证 WebDAV、AWS S3 与 R2 的连接器行为。Windows 本地用临时签名包验证了 ModuleHost 就绪、健康检查和停止。真实服务按下节独立验收。

## 验收

- 无模块、已安装无连接、只读连接、多连接、连接撤销和模块停止分别呈现正确状态。
- WebDAV、AWS S3、Cloudflare R2 分别用真实端点验证连接、目录列举、属性、流式下载和只读授权。AWS S3 与 R2 分别验证续页与桶前缀限制。WebDAV 验证根目录限制及大目录列举，并按实际实现核验 Explorer 分页。
- 上传和跨来源复制交付时，分别记录三个目标上实际通过的操作。至少验证覆盖规则、传输中断、权限不足和部分失败结果。
- 同一连接的授权隔离、凭据轮换、异常重定向、传输中断和文件选择器的来源限制均有回归覆盖。
- 更新连接器只中断目标模块。新版无法启动时，旧版恢复后可访问原连接。

关联 [Explorer 设计](../../applications/RelaxKonOS.Explorer.md)、[文件同步提案](FileSync.md) 和 [模块分发设计](../../architecture/RelaxKonOS.ServerModuleDistribution.Design.md)。
