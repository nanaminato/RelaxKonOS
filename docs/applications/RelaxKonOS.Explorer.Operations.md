# 文件操作中心设计与实现

日期：2026-09-07。先完成设计，再实现；核心远端操作已实现，验证证据见末尾。

## 用户体验

复制粘贴、剪切粘贴、删除和移动路径对话框统一提交后台任务。第一次提交自动打开非模态“文件操作”窗口；后续任务追加到同一个窗口。浏览器提交后可继续导航、选择、发起其他操作。关闭进度窗口只是隐藏，浏览器命令栏提供重新打开入口；关闭来源浏览器不取消任务。

每个任务卡片显示操作、来源/目标、状态、当前路径、处理项目数、当前文件字节进度与结果详情。准备/排队/无法测量的步骤使用不确定进度，不伪造速度或剩余时间。保留完成记录，可以清除已结束任务。

不同任务可并发；全局最多两个任务同时执行，其他排队。已知来源或目标路径树有重叠的任务串行。保留两者生成的名称仍在最终提交时检测竞争，不保证预留名称的原子性。每个任务内部按文件顺序执行，冲突暂停该任务；其他不相干任务仍可推进。后续可根据磁盘特征调整调度，不按文件无限并发。

遇到重名：文件对文件允许替换、跳过、保留两者；目录对目录合并并逐文件决策，绝不通过删除整个目标目录实现覆盖；类型不同只能跳过或保留两者。占用/权限/其他 I/O 错误提供重试、跳过和取消；权限错误还可调用已有提权交互。覆盖不是解除文件占用的办法。勾选“应用到此任务后续同类问题”仅保存本任务的冲突/跳过策略，不全局自动覆盖，也不无限自动重试错误。

取消立即请求服务端停止：排队与等待决策可直接取消；普通复制在数据块之间取消，尚未提交的临时文件清理，原目标保留；移动复制成功后才删除源；已完成复制/移动/删除不回滚。目录可能部分完成；源目录有跳过子项时保留。原子重命名、单次删除及现有提权助手调用不能强行中断，显示“正在取消”，待该步骤结束后停止。暂停/恢复和回收站不在本次范围。

## 服务端协议和执行

新增 POST /files/operations（客户端生成 requestId，按身份幂等）；GET /files/operations（恢复当前身份任务）；GET /files/operations/{id}；POST /{id}/decision（携带 issueId、action、applyToAll）；POST /{id}/cancel。沿用 JWT、files.manage 与宿主 OS 权限，按用户/工作区/设备隔离任务。重复 requestId 的请求必须一致，避免网络重试重复复制。

状态：Queued → Running ↔ WaitingForDecision → Completed / CompletedWithIssues / Cancelled / Failed；取消请求期间 Cancelling。快照回传 requestId 并携带单调递增的快照 revision，客户端拒绝旧响应覆盖新状态。快照包含顶层成功来源、递归处理/跳过数、当前字节数、当前问题和有上限的详细记录。决策编号避免过期点击覆盖下一个冲突。请求取消与决策都幂等。前端轮询读取，网络错误显示连接中断并保留任务 ID，不重发文件操作。

Windows/Linux 文件移动优先使用不允许跨卷隐式复制的原生 rename（MoveFileExW / renameat2）；同卷保留文件本身的元数据且不传输数据。跨卷或不支持该原语时使用可取消的流式复制。目录仍逐项处理，以支持合并、冲突和取消。

普通复制先写同目录随机临时文件，完成并关闭后再提交目标，失败/取消清理临时文件；最终无覆盖提交仍检查冲突。目录合并，递归不跟随符号链接/重解析点，本版本此类项显示可跳过的问题；避免链接循环和意外操作链接目标。已能确认类型的普通文件访问失败时复用现有提权助手及路径授权；助手调用粒度不足时明确显示无法测量的步骤，取消需等待该调用结束。尚不能列举的受保护目录不走不可控的递归提权覆盖，保留权限错误供跳过/取消或外部修复权限后重试。流式复制保留最后修改时间和 Unix mode；不承诺复制 ACL、扩展属性、稀疏布局、备用数据流或硬链接拓扑。原生同卷文件移动保留原文件元数据。

任务存于服务端内存：每批最多 1000 个顶层来源，全局最多 32 个未结束任务、256 条记录；提交时回收超过一小时的已结束任务，容量满时优先移除最旧已结束记录。幂等保证仅在记录保留期间有效。详情最多 200 条，超过后明确标记截断。服务端重启不自动重放任务，客户端找不到 ID 时提示结果需核实；不承诺崩溃恢复或事务。网络断开不自动取消任务。状态轮询不应依赖来源窗口生存期。当前登录身份变化后停止旧身份轮询，不能把旧任务请求发送到新服务器。

## 客户端集成

共享操作中心协调服务端任务与卡片。提交固定选择与目标快照；完成后按实际成功源更新剪切剪贴板（仍检查剪贴板版本），并刷新当前仍位于受影响目录且未忙碌的浏览器。结果中部分完成目录不从剪切剪贴板移除。保留旧批处理路径供未注入操作中心的选择器/无桌面状态回归使用，普通浏览器全部接入新服务。

本次核心覆盖远端复制、移动、删除；本机**上传**已改为分块会话（> 4 MiB 的走 `RelaxKonOS.FileUpload.Design.md` 的编排器，进度与取消仍由操作中心的卡片承载），**下载**使用现有传输接口，后续需要专门的流式下载任务适配，不伪装成服务端可恢复任务。

上传条目进入操作中心时，编排器上报的是"已确认字节 + 在途字节 + 是否在核对"三元组；条目行只有"路径 + 字节数"两栏，因此在与服务器核对偏移期间它**只表现为计数停住**，不显示任何文字状态（`RelaxKonOS.FileUpload.Design.md` §5.6）。

## 参考

- [IFileOperation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperation)
- [文件操作标志](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)
- [CopyFileEx 进度与取消](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-copyfileexa)
- [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)
- [Linux rename / renameat2](https://man7.org/linux/man-pages/man2/rename.2.html)

参考 Windows 的交互与取消语义；以上并发数、保存范围和跨平台处理是 RelaxKonOS 的实现决策。

## 验收计划

服务端真实临时文件回归：复制/移动/删除、递归合并、冲突决策、同类应用、占用/错误重试、部分跳过、取消清理、原目标保护、同目录副本、任务并发/重叠串行、身份隔离、幂等和过期决策。客户端检查多任务提交、完成结果与剪贴板更新，编译 AXAML 及三种语言资源。真实桌面与 Windows 占用行为需单列记录。

## 实现与验证记录

- 已实现 Protocol DTO / 路由、FileOperationService 与 FileOperationEndpoints、IExplorerClient / ExplorerClient、共享 ExplorerOperationCenter、非模态 ExplorerOperationsView 和普通浏览器入口。三种语言均补充资源。
- 服务端 36 项真实临时文件检查通过：包含两个大文件同时流式复制、各自取消、独占源文件锁、冲突、目录合并、幂等、身份隔离、过期决策、同卷原生移动及跨文件系统移动。
- 跨文件系统检查的源位于 `/tmp`（tmpfs），目标位于项目临时目录（LVM 文件系统），测试生成文件已清理。
- Explorer 120 项回归检查通过（新增 10 项操作中心检查）。服务端、客户端（含 AXAML）编译均零警告、零错误。
- 当前文件字节进度与处理数是实测数据；本轮未预扫描完整目录树，因而不提供全任务字节百分比、速度曲线或剩余时间估计。
- 符号链接/重解析点及其父路径本轮拒绝跟随；路径检查不是针对其他宿主进程并发修改目录树的原子文件系统沙箱。服务端重启不会重放任务，也不自动清理崩溃前遗留的临时文件。
- 真实 Windows 占用与权限、UNC、桌面布局/鼠标键盘、会话断线重连及提权助手端到端验收尚未执行。当前无可用原生桌面自动化或 Xvfb，不能以 AXAML 编译代替视觉验收。

验证命令：

```bash
dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false -v minimal
dotnet run --project Tests/Client/RelaxKonOS.Explorer.Tests -p:MSBuildEnableWorkloadResolver=false -m:1 --verbosity quiet
dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false -v minimal
dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false -p:UsePrebuiltServerAssembly=true -v minimal
RELAXKONOS_FILE_JOB_SECONDARY_ROOT="$PWD/.codex-scratch" dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --file-operations-only
git diff --check
```

服务端测试的普通项目引用构建在本地出现既有 Protocol 传递引用缺失，因此使用项目已经提供的 `UsePrebuiltServerAssembly=true`，指向本轮刚编译的 Server/Protocol 程序集。只执行了文件操作专项，未将其他后端专项宣称为本轮验证。


## 系统文件剪贴板（2026-10-15）

文件浏览器统一使用复制、剪切、粘贴；“更多”菜单不再提供上传、下载、上传文件夹或单独的“从本机粘贴”。本机文件在系统文件管理器复制后，可在浏览器粘贴；文件及文件夹沿用现有操作中心的传输进度、取消与分块传输能力。同一服务器内的粘贴仍提交服务器复制/移动任务，内容不绕经客户端。

Windows 客户端复制远程文件时，通过 OLE `IDataObject` 发布 `FileGroupDescriptorW`、按索引读取的 `FileContents` (`IStream`) 和复制效果。复制阶段只递归枚举元数据，支持目录、空目录、多文件、Unicode 相对名称及 64 位文件长度；Windows 不支持的名称、重名和超限选择会报错，不静默改名。上限为 100000 个条目、128 层目录及 Shell 描述符的 260 字符相对路径。

系统文件管理器在粘贴时读取内容，客户端按请求拉取，使用临时磁盘缓存支持回读，内存缓冲为 128 KiB。缓存使用 DeleteOnClose，在 Shell `EndOperation`、流释放或进程退出时删除。OLE 数据对象由独立 STA 线程持有并泵送消息，网络回调不阻塞 Avalonia UI。HTTP 下载使用 `ResponseHeadersRead`，不在 `SendAsync` 阶段整包缓冲。外部目标通过 `FD_PROGRESSUI` 请求 Windows Shell 原生进度、剩余时间与取消窗口；同时把实际读取字节映射为 RelaxKonOS 操作中心的复制任务，支持从操作中心取消网络读取。重名决策由 Windows Shell 承担；取消后不会继续主动读取剩余内容，已经发出的单次网络读取仍需返回。剪贴板保持有效要求客户端进程运行且原登录会话未改变；会话变化后拒绝内容访问。

跨系统剪切当前按**复制**发布，不删除远程源，也不因本机剪贴板的移动标记删除本机源；服务器内剪切继续使用原有完成确认及清理语义。macOS 通过原生 file promises 延迟下载，Linux 发布本地快照文件 URI（见下文）。不依赖 RDP，不引入新的服务器文件传输协议。

验证：299 项 Explorer 回归检查通过，包含 Windows HGLOBAL 描述符及 COM 流创建、延迟打开、顺序/回读/EOF、Unicode 与大于 4 GiB 的长度、会话变化拒绝读取、Shell 无索引探测与组合 aspect 探测、原生进度标志、操作中心进度/取消，以及系统剪贴板被文字替换后不得复活远程选择。FileServices 检查另覆盖真实 HttpClient 下载不整包缓冲与响应释放。桌面客户端（含 AXAML）编译通过，仍有既有 SignalR 依赖安全告警。

Windows 原生验收使用生产剪贴板实现与生成的测试内容，跨进程 OLE 读取成功。通过真实 Windows 文件管理器粘贴：256 KiB 文件、32 MiB 限速文件、中文目录/文件名、空目录与空文件，落盘内容 SHA-256 全部匹配；限速复制弹出系统进度/剩余时间/取消窗口，取消后未完成目标被 Shell 清理。验收截图保存在本地 `.tmp/clipboard-progress-proof.png`。这验证了 Windows 剪贴板与 Shell 集成，未连接实际已部署服务器；真实服务器权限、断线以及外部重名决策仍需专项验收。

可复现命令：运行 `Tests/Client/RelaxKonOS.Explorer.Tests` 的 `--clipboard-source` 模式（会替换系统剪贴板，90 秒有效），在另一个进程运行 `--clipboard-sink` 检查跨进程读取；`--clipboard-source-slow` 提供 32 MiB 限速文件，`--clipboard-source-tree` 提供中文目录及空文件样本，可在 Windows 文件管理器粘贴验收。验收模式不连接真实服务器或读取用户文件。

Windows Shell 格式参考：[Shell Clipboard Formats](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard)。


### 桌面文件图标接入（2026-10-15）

桌面文件图标和内置文件浏览器统一调用 `HostFileClipboard.PublishAsync/ReadAsync`，Windows 虚拟文件发布、会话校验与来源识别共用一处实现。桌面复制/剪切现在可在 Windows 文件管理器粘贴；外部复制文件后可在桌面背景或桌面文件夹粘贴。文件图标支持 Ctrl+C、Ctrl+X、Ctrl+V。外部剪贴板变为文字或空内容后，桌面不会再次使用过期的内部文件选择。

桌面本机粘贴复用 Explorer 的传输编排，不打开文件浏览器、不先列举目标目录；目标路径在提交时固定，大于 4 MiB 的文件继续走分块上传，进度与取消由共享操作中心承载，权限确认沿用既有系统对话框。操作中心窗口在桌面初始化时即可使用，任务完成后刷新桌面，不要求先启动文件浏览器。跨系统剪切依旧按复制处理。

验证：桌面专项 8 项检查通过（复制/剪切发布、发布失败保护、本机粘贴、文件夹目标、过期剪贴板、远程内部复制和快捷键）；301 项 Explorer 回归通过，含无需导航的桌面大文件目标捕获与分块调度。共享 HostFileClipboard 的跨进程 OLE 实际读取 256 KiB 内容通过；桌面客户端编译通过。桌面专项命令为 `dotnet run --project Tests/Client/RelaxKonOS.WindowPreviews.Tests -- --desktop-clipboard-only`；本轮未操作连接真实服务器的桌面会话。


### 文件进度窗口布局（2026-10-15）

文件操作窗口采用 Windows 文件复制的信息层级：所选项目数、较大的完成百分比、绿色速度曲线、细进度条，以及名称/来源/目标/剩余时间/已处理大小；详情可折叠。取消使用带本地化无障碍名称的独立按钮，未提供尚未实现的暂停功能。重名/权限/错误使用强调色并优先显示决策区，避免曲线和统计把替换、保留两者、跳过或重试按钮推到视口外。

速度使用单调时钟与已观察字节差，约半秒采样、五秒平滑，保留最多 60 个曲线样本。可见窗口在没有新进度回调时仍更新采样，停滞速度逐步降至零。新文件、字节回退、等待决策和重新开始都会重置测量。测量不足一秒时显示计算中，不补造曲线或速度。客户端传输拥有全任务总字节时可估算剩余时间；服务端普通任务仅提供当前文件计数，界面明确标注“当前文件”，不据此估算整个任务完成时间。无法测量的排队、原子操作和删除显示状态与不确定进度。

验证：314 项 Explorer 回归检查通过，包含速度测量、停滞、重置、等待时间排除及进度范围检查。真实 Avalonia 控件在中/英/日三语、浅/深色主题下生成预览，详情折叠及冲突按钮可见性检查通过。布局专项命令：`dotnet run --project Tests/Client/RelaxKonOS.ApplicationLayout.Tests -- --file-progress-only`。预览数据是测试任务，不代表某次真实服务器传输。

## macOS 原生文件剪贴板（2026-10-17）

`IHostFileClipboardBackend` 仍是桌面和 Explorer 的统一边界。macOS 使用 AppKit `NSPasteboard.generalPasteboard` / `writeObjects:` 发布每个选中项的 `NSFilePromiseProvider`，文件类型为 `public.data` 或 `public.directory`。复制只验证顶层名称及规范化冲突，不查询目录、不下载内容、不启动传输任务；Finder 请求兑现 promise 时才递归读取目录并下载文件。原生接口依据：[NSFilePromiseProvider](https://developer.apple.com/documentation/appkit/nsfilepromiseprovider)、[写入回调](https://developer.apple.com/documentation/appkit/nsfilepromiseproviderdelegate/filepromiseprovider(_:writepromiseto:completionhandler:))。

`filePromiseProvider:fileNameForType:` 返回来源基础名称；写入回调使用 Finder 提供的完整目标 URL，包括其重名决策后的名称，不信任下载响应文件名。内容通过现有 `IExplorerClient.DownloadAsync`、128 KiB 缓冲及 `ClipboardFileTransfer` 操作中心进度/取消机制写入目标目录旁的私有暂存目录，然后在 `NSFileCoordinator` 写入协调中以不覆盖的重命名完成兑现。文件、目录、空目录和空文件沿用共享下载校验。长度变化、会话失效、取消、下载或目标写入失败均完成失败通知并清理暂存；成功也立即清理暂存，不保留进程期快照。每个文件的操作中心成功通知延迟到目标提交成功。

Objective-C delegate 通过 provider 的 retained association 存活，反向调用函数指针固定在进程期；delegate 释放时移除托管状态。写入使用后台 operation queue / Task，保留目标 URL 并复制 completion block，兑现结束后调用一次完成回调并释放原生引用。剪贴板替换不取消已经开始的请求；重复粘贴会重新下载。未开始的 promise 仍需客户端运行、原登录会话有效和远端内容可用。Explorer 发布时捕获进程服务，不依赖原浏览器窗口继续存在。来源识别继续使用 pasteboard changeCount；宿主替换剪贴板后不再判定为当前远端复制。跨系统剪切按复制处理，不删除远端源。

拒绝路径分隔符、冒号、NUL、点目录、过长组件，以及忽略大小写和 Unicode 规范化后的重名。递归选择上限为 100000 项、128 层。兑现期间目标卷需有完整内容的暂存空间；崩溃可能留下目标目录旁的 `.relaxkonos-promise-*` 暂存目录，目前不跨进程自动清理。

验证：400 项 Explorer 回归通过，新增 promise 发布零下载、所有权变化、Finder 目标名称、重复兑现、目标冲突、会话失效、短流、取消、协调失败、递归空目录/空文件及暂存清理检查。托管测试通过注入发布边界验证，未替代 AppKit ABI 验收。当前环境为 Windows；Apple Silicon / Intel 实机仍需通过实际桌面和 Explorer 复制，验证 Finder 粘贴、多选、中文/emoji、目录、大文件、重复粘贴、关闭源窗口、剪贴板替换、断线/切换会话、取消、重名决策、只读目标和原生对象释放。不能将本轮托管回归视为 Finder 实机验收完成。

## Linux 文件管理器剪贴板（2026-10-16）

`LinuxHostFileClipboardBackend` 通过现有 `IHostFileClipboardBackend` 接入桌面图标和 Explorer，不按发行版或文件管理器拆分后端。使用 Avalonia `IClipboard` 传输原生 MIME 数据，不启动 xclip、wl-copy 或外部命令。目标格式覆盖 GNOME Files (Nautilus)、KDE Dolphin 和 Xfce Thunar：

| 格式 | 内容及用途 |
|---|---|
| `text/uri-list` | UTF-8 文件 URI 列表，CRLF 分隔并结尾；提供通用文件列表 |
| `x-special/gnome-copied-files` | `copy` 后跟 LF 分隔的 URI，无末尾空行；供 GNOME Files、Thunar 使用 |
| `application/x-kde-cutselection` | ASCII `0`，明确按复制处理 |
| RelaxKonOS 应用标记 | 与文件格式同次发布，供内部粘贴识别来源 |

URI 只指向已下载的本机快照，转义空格、中文、emoji、`#`、`%` 和换行；不暴露远端路径为本机 URI。协议依据：[Nautilus 源码](https://github.com/GNOME/nautilus/blob/main/src/nautilus-clipboard.c)、[Thunar 源码](https://github.com/xfce-mirror/thunar/blob/master/thunar/thunar-clipboard-manager.c)、[KDE 剪贴板格式说明](https://api.kde.org/legacy/4.14-api/kde-baseapps-apidocs/lib/konq/html/classKonqMimeData.html)。

Linux 与 macOS 共用 `HostFileClipboardStaging` 的递归下载、128 KiB 分块、操作中心进度与取消、会话检查、长度检查和失败清理。Linux 的成功快照保留到进程退出，确保重复粘贴及替换剪贴板后的在途读取；异常退出可能留下临时文件。Linux 复制阶段需要下载和本机磁盘空间；macOS 在兑现 promise 时下载并立即清理暂存。跨系统剪切始终按复制发布，不删除远端源。已经下载的本地快照不因会话失效而撤回。

文件名保留 POSIX 大小写及 Unicode 原始形式，允许冒号、反斜杠和换行；拒绝空名、点目录、斜杠、NUL、超过 255 UTF-8 字节的组件及完全同名冲突。若本机挂载点采用更严格规则，由文件系统拒绝并清理整个未发布快照，不覆盖现有文件。

准备期间不修改原剪贴板。发布前重新检查本进程所有权、可用的 X11 `TIMESTAMP`、格式集合以及文本、应用标记和文件 MIME 内容，发现变化则放弃发布。不提供所有 Linux 传输下的原子比较交换；没有所有权时间戳的传输不能区分完全相同内容的外部重新复制，最后一次检查与发布之间也存在系统竞争窗口。

当前桌面入口使用 Avalonia `UsePlatformDetect()`，实际 X11 / XWayland / Wayland 能力由部署的 Avalonia 平台提供。本轮未增加独立的原生 Wayland 协议实现，也未承诺绕过 compositor 的跨 XWayland 剪贴板限制。

验证：341 项 Explorer 回归及客户端完整编译（含 AXAML）通过；修复了既有的 Avalonia 类型引用和宿主保存下载元组访问编译错误。涵盖 MIME 字节及 URI 转义、内部来源标记、完整下载、递归空目录/空文件、前次快照保留、会话失效、下载中会话切换和宿主剪贴板替换保护；macOS 的取消、短流和规范化重名检查继续覆盖共享下载路径。执行环境为 Windows，尚未在 Linux 文件管理器中实际粘贴验收。真机验收应在 GNOME Files、Dolphin、Thunar 分别检查中文/emoji/特殊字符、多文件、目录、空文件、重复粘贴、重名决策、取消和退出清理，并分别记录 X11、Wayland 会话下的实际平台及 XWayland 桥接结果。


## 归档任务（2026-10-16）

新增 `Compress` / `Extract` 任务类型，复用现有请求幂等、用户隔离、路径预留、取消和结果刷新。ZIP、TAR、TAR.GZ 实现在身份绑定的流式文件适配器上运行，临时输出完整校验后不覆盖发布。首版重名决策为保留两者/跳过，错误可重试，权限拒绝不自动弹出组合提权流程。详细格式、安全校验、资源限额和验收命令见 [压缩与解压缩设计](RelaxKonOS.Explorer.Archives.md)。
