# APP-008 云存储与内置文件浏览器

状态：已实现可选 ModuleHost 模块与桌面 Explorer 接入。WebDAV 和 S3 兼容 Provider 分别由独立服务端包 `cloud-webdav` 和 `cloud-s3` 提供。真实 AWS S3 端点验收待执行。

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

云存储由可选的 `cloud-storage` 服务端 `.rkmodule` 提供。组件中心管理模块安装、停止、重启和版本。`cloud-storage` 持有连接配置、加密凭据、用户授权和上传会话，网盘协议继续随该模块运行。`cloud-webdav` 执行 WebDAV 请求，`cloud-s3` 执行兼容 S3 的请求，包括 Cloudflare R2。停用或卸载 Provider 后，连接记录和 ID 仍保留，对应协议的文件操作返回 `cloud-storage-provider-unavailable`。重新启用兼容的 Provider 即可复用连接。

桌面 Explorer 通过认证代理发现 `cloud-storage` 模块。模块可用时，侧栏显示「云存储」和当前用户的各个连接根。窄窗口可从云存储面板内切换连接。模块暂不可用时，打开的面板保留导航上下文。撤销连接后移除对应入口。连接按所有者隔离。WebDAV 用 HTTPS 端点中的目录作为根，S3 用桶与键前缀作为根。

## 文件来源契约

首版使用 `CloudStorageApi` 的专用来源契约，经现有 ModuleHost 代理分发。Core 核对 ModuleHost 访问及文件读写权限，模块核对连接所有者和根范围。宿主文件保留现有文件接口。Explorer 使用连接 ID 和不透明条目引用导航。后续接入更多模块时再提取通用来源注册契约。

已实现分页列举、属性、流式下载，以及显式启用写入后的新建目录和不覆盖上传。S3 使用服务端续页令牌，WebDAV 对有界的 Depth 1 响应分页。对象存储的空目录使用带斜线的零字节对象。内置宿主文件选择器继续只显示宿主文件。

### Server 与云存储之间传输

桌面 Explorer 的上传从 Server 选择一个或多个文件，下载在 Server 选择文件名和保存路径。客户端只提交路径、连接 ID 与不透明引用。Server 文件任务用当前登录用户的宿主文件权限读取源文件或创建同目录暂存文件，数据在 Server 与云存储之间流动。任务中心提供字节进度、取消、重试及同名处理。下载支持显式替换同名文件、保留两份或跳过，成功后才发布到最终路径。上传默认条件创建。同名时可保留两份或跳过。也可显式选择替换同名文件，具体条件由连接器落实。

Server 将上传按块提交给 ModuleHost。模块将暂存会话限定到连接所有者、连接及其配置修订。分块上限 8 MiB，单文件上限 4 GiB，模块总暂存预留上限 8 GiB，空闲会话 24 小时后清理。模块会话记录最后一个确认偏移，Explorer 中重试会创建新的传输尝试。

上传完成后由 ModuleHost 后台发布，客户端查询同一提交回执。WebDAV 写入远端随机暂存文件，再用 `MOVE` 和 `Overwrite: F` 发布。S3 Provider 流式读取 CloudStorage 的上传暂存，每次最多缓冲 16 MiB，较大的文件按 16 MiB 分片上传。默认在单次写入或分片完成请求中设置 `If-None-Match: *`。显式选择替换时，替换意图与上传会话绑定，发布时允许写入同一个对象键。条件创建遇到同名冲突时保留原文件，并尝试清理远端暂存或中止分片上传。取消任务会停止接收或请求取消正在执行的发布。模块到上游的发布阶段中断后，未确认的提交不会自动重放。

## 凭据和传输

WebDAV 使用 HTTPS Basic 认证，S3 使用 AWS SDK 签名，支持静态访问密钥和可选会话令牌。连接凭据由模块保存在私有目录的 AES-GCM 加密记录中，密钥文件由同一隔离目录的 OS 权限保护。备份和还原时需同时保护这两个文件。客户端只能读取连接摘要。出站连接限定到配置的 HTTPS 源并拒绝重定向、环回和元数据地址。本机接口默认拒绝。控制者显式启用内网 WebDAV 并填写本机私有 IPv4 字面地址时，可连接该地址和指定端口，继续执行默认 TLS 校验。撤销阻止新的请求，已开始的流可结束，上传暂存完成时会再次检查连接。

Core 仅在 ModuleHost 私有通道中向获准的 `cloud-storage` 实例开放固定的 Provider 路由，按运行代际核验令牌，并只转发给对应的获准 Provider 实例。授权以宿主配置的来源、发布者和模块 ID 为准。Provider 在请求执行期间使用所需的连接信息和数据流，持久化凭据仍由 `cloud-storage` 管理。Explorer 保留原有服务端代理、文件读写权限和连接所有者校验。

S3 Provider 使用连接配置提供的凭据和端点。AWS SDK 的配置查找限制在 Provider 的数据目录。

文件级上传和下载已使用服务端任务。桌面 Explorer 的可写云连接可以剪切条目，再到同一连接的目标目录粘贴。移动不覆盖目标。删除需要再次确认，删除目录会包含其内容。模块拒绝移动或删除连接根目录，WebDAV 返回部分失败时向客户端报错。

桌面 Explorer 可以选择宿主目录上传，或将选中的云目录下载到宿主目标目录。服务端按目录逐级复制，保留空目录。下载使用同级临时目录，复制完成后再发布。上传中断后，已创建的云端目录和文件可能保留。

S3 兼容连接也提供移动与删除。目录操作先列出选中前缀中的对象，最多 10,000 个。移动在所有目标对象条件创建成功后才开始删除源对象，删除使用源 ETag。R2 通过 16 MiB 条件范围读取和分片写入复制对象，单次移动的每个对象最多 10,000 个分片。失败可能留下已经复制的目标对象，或者只删除了部分源对象。S3 兼容连接的新操作待真实端点验收。

百度网盘移动和删除使用供应商文件管理接口。移动遇到同名条目时失败，模块在收到单项成功结果后才向客户端确认。替换上传先检查目标为文件，再使用供应商的替换模式。WebDAV 替换上传要求目标为文件且具有强 ETag，并以 `If-Match` 执行写入。同步、系统级挂载和外部工具留待后续阶段。连接器的启动保持共享数据可由上一版读取，便于启动失败后的模块版本回退。

## 构建和部署

发布 `ServerModules/RelaxKonOS.ServerModule.CloudStorage/RelaxKonOS.ServerModule.CloudStorage.csproj`，指定目标 RID 并使用框架依赖发布。按[模块打包说明](../../architecture/RelaxKonOS.ServerModulePackage.Format.md#离线打包命令)打包发布目录，使用下列参数。模块不声明宿主提权能力。

发布者工作台可通过项目目录内的 `module-release.json` 发现 CloudStorage 和两个 Provider，按所选 RID 批量构建并签发目录。WebDAV 包入口为 `RelaxKonOS.ServerModule.CloudWebDav.CloudWebDavModule`，S3 包入口为 `RelaxKonOS.ServerModule.CloudS3.CloudS3Module`。三个包均要求 ModuleHost ABI 2.2.0，沿用现有签名模块包来源策略。自定义来源需授权所分发的模块 ID。已有来源调整授权项时更新来源策略版本。

| 参数 | 值 |
| --- | --- |
| `--module-id` | `cloud-storage` |
| `--runtime` | `module-host` |
| `--entry-assembly` | `RelaxKonOS.ServerModule.CloudStorage.dll` |
| `--entry-type` | `RelaxKonOS.ServerModule.CloudStorage.CloudStorageModule` |

跨 ABI 升级时先停用已部署的云存储模块及 Provider，再更新 Core 和 ModuleHost，安装 ABI 2.2.0 的 `cloud-storage` 和所需 Provider 后重新启用。升级过程中保留 CloudStorage 数据目录，Explorer 沿用已有路由和连接。后续单独更新 Provider 时走模块包在线更新，启动验证失败由现有机制恢复上一版。卸载按组件中心现有的停止、停用、重启 Core 和移除流程执行。真机测试沿用目标机器已接纳的测试来源和预配置连接，测试证据仅记录版本、操作和结果。

本地回归入口为 `Tests/ServerModules` 的 `--cloud-storage-checks`。覆盖块内 TCP 断开、偏移续传、S3/R2 分片完成时的不覆盖条件、模拟百度网盘操作，以及 Server 文件任务对 WebDAV 的双向传输、下载替换、上传保留两份和取消。宿主文件上传的 98 项检查也通过。Windows 本地使用临时签名包验证 ModuleHost 加载。

## 验收

- 新增操作的本地回归覆盖模拟 WebDAV、S3、R2 和百度网盘的替换、移动、删除，以及经模拟 WebDAV 的宿主与云端双向整目录复制。桌面布局测试覆盖目录选择与文件任务提交、只读连接的操作可见性。新增操作的真实端点验收仍待执行。
- R2 条件删除可通过 `Tests/ServerModules` 的 `--cloud-r2-conditional-delete-acceptance` 验收。使用专用测试桶或前缀，通过环境变量配置 `RELAXKONOS_R2_ENDPOINT`、`RELAXKONOS_R2_BUCKET`、`RELAXKONOS_R2_TEST_PREFIX`、`RELAXKONOS_R2_KEY_ID` 和 `RELAXKONOS_R2_SECRET`。验收仅使用一个随机命名的测试对象，检查旧 ETag 的删除被拒绝且对象内容仍在，并在结束时清理。
- 2026-11-15 真实 R2 接受了带旧 ETag 的删除请求，验收命令确认临时对象已清理。连接器在删除前重新读取并比较 ETag。若已检测到变化，保留源对象并返回冲突。R2 上读取与删除之间仍存在竞态窗口。
- 2026-11-15 使用 `--cloud-r2-connector-acceptance` 通过真实 R2 连接器验收。测试在随机前缀创建目录和空子目录，覆盖文件，移动整棵目录并核对替换后的内容和空子目录，最后删除目标目录并确认前缀为空。此命令使用与条件删除探针相同的环境变量。
- 同一验收命令通过本机 Server 文件任务和真实 R2 进行宿主 → 云端 → 宿主整目录往返，核对文件内容与空子目录，确认下载暂存目录和云端测试前缀均已清理。
- `--cloud-r2-package-acceptance <CloudStorage 发布目录> <CloudS3 发布目录>` 复用 R2 验收环境变量，通过现有模块包接口验证 S3 Provider 安装、在线更新、启动失败回滚和卸载。测试在独立前缀内通过 Explorer 使用的 API 验证真实分片上传、同名冲突及逐字节下载。
- `--cloud-s3-package-checks <CloudStorage 发布目录> <CloudS3 发布目录>` 在临时 Core 上用签名测试源和两个实际 ModuleHost 进程验证相同的包操作、现有连接和上传会话，不需要上游凭据。云存储回归另用模拟 S3 与 R2 验证分片上传、同名冲突、读写及签名错误的处理。
- 2026-11-21 在 Windows 临时 Core 和两个实际 ModuleHost 进程上完成 S3 Provider 签名包及真实 R2 验收。用原有连接经 Explorer 文件接口列举目录，分片上传并逐字节核对 16,777,219 字节的下载。同名写入返回 409，原内容保留。在线从 `1.0.0` 更新到 `1.1.0`，`1.2.0` 启动健康检查失败后自动回滚至 `1.1.0`，同一连接和文件在更新与回滚后均可读取。Core 与 CloudStorage 的运行代际保持稳定。停止后暂存上传仍可查询，移除后原连接保留，文件操作返回 503。远端测试前缀已清理。
- WebDAV 可使用 `Tests/ServerModules` 的 `--cloud-webdav-connector-acceptance` 验证。通过环境变量设置 `RELAXKONOS_WEBDAV_ENDPOINT`、`RELAXKONOS_WEBDAV_USER` 和 `RELAXKONOS_WEBDAV_SECRET`。私有 IPv4 端点可显式设置 `RELAXKONOS_WEBDAV_ALLOW_PRIVATE=1`，仍需受信任的 TLS 证书。测试在指定根下创建和清理一个随机目录，覆盖替换、移动、删除和双向整目录复制。
- `--cloud-webdav-package-acceptance <CloudStorage 发布目录> <CloudWebDav 发布目录>` 复用同一组环境变量，在临时 Core 服务中通过正式模块包接口验证签名包安装、在线更新、失败回滚和卸载。测试使用两个真实的 ModuleHost 进程，对 HTTPS WebDAV 执行目录列举、上传及下载校验，并清理随机测试目录。
- 2026-11-21 在 Windows 临时 Core 上使用已接纳的签名测试目录安装两包，连接真实 HTTPS WebDAV。Provider 在线从 `1.0.0` 更新到 `1.1.0`，`1.2.0` 候选包启动健康检查失败后回滚至 `1.1.0`。原连接仍能列举和下载同一文件，Core 与 CloudStorage 的运行代际保持稳定。经停用、重启并移除 Provider 后，原连接配置仍可查看，WebDAV 操作返回 503。临时目录已清理。测试使用开发启动器运行 ModuleHost 子进程。
- 2026-11-15 使用本地已授权的读写 WebDAV 连接通过真实验收。验证文件替换、目录移动与删除，以及本机 Server 文件任务的双向整目录复制。文件内容与空子目录均保留，清理后确认随机测试目录已不存在。
- AWS S3 可使用 `Tests/ServerModules` 的 `--cloud-aws-connector-acceptance` 验证。通过环境变量设置 `RELAXKONOS_AWS_REGION`、`RELAXKONOS_AWS_BUCKET`、`RELAXKONOS_AWS_TEST_PREFIX`、`RELAXKONOS_AWS_KEY_ID` 和 `RELAXKONOS_AWS_SECRET`。临时凭据还须设置 `RELAXKONOS_AWS_SESSION_TOKEN`。测试在专用测试前缀下验证替换、原生分片复制后移动、删除和双向整目录复制，并清理本次创建的对象。
- 百度网盘可使用 `Tests/ServerModules` 的 `--cloud-baidu-connector-acceptance` 验证。通过环境变量设置 `RELAXKONOS_BAIDU_CLIENT_ID`、`RELAXKONOS_BAIDU_CLIENT_SECRET`、`RELAXKONOS_BAIDU_REFRESH_TOKEN` 和 `RELAXKONOS_BAIDU_TEST_PARENT`。使用具有写权限的测试账号和专用父目录。测试在随机目录内验证替换、移动、删除和双向整目录复制，并清理该目录。

- 2026-11-12 Ubuntu 26.04 实机将 Core 升级至 `0.2.8-dev.1456cb5b7`，因为服务端云传输引入了 Core 文件任务契约。CloudStorage 随后通过现有签名测试来源在线从 `0.2.8` 更新至 `0.2.9`，操作回执 `87acb99e-ed15-4c04-b054-8e72424fec7d` 成功，模块健康，Core PID 和终端模块状态在模块更新前后相同。首次在线更新因旧包父目录归 root 所有而无法暂存，修正父目录属主后重试成功，旧版本目录未改动。
- 通过 Windows 上的验收客户端调用文件任务 API，使用真实 WebDAV 在 Ubuntu 与云端往返传输 16 MiB。分别从 Ubuntu 文件和 WebDAV 对象计算 SHA-256，三份内容的摘要一致。352 个客户端 HTTP 请求只交换 JSON 控制数据，请求正文合计 2,339 字节，响应正文合计 189,119 字节，最大单个响应 3,123 字节。上传和下载的同名测试都选择保留两份，验证了原文件和编号副本。另用 128 MiB 文件，在上传确认 8 MiB、下载写入 1 MiB 时分别取消，两项任务均进入取消状态，未生成目标文件，下载暂存文件已清理。Ubuntu 与 WebDAV 的临时测试文件均已清理。
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
