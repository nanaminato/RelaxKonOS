# 文件操作中心设计与实现

日期：2026-09-07。先完成设计，再实现；核心远端操作已落地，验证证据见末尾。

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

本次核心覆盖远端复制、移动、删除；本机上传/下载使用现有传输接口，后续需要专门的流式上传/下载任务适配，不伪装成服务端可恢复任务。

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
dotnet run --project Client/RelaxKonOS.Explorer.Tests -p:MSBuildEnableWorkloadResolver=false -m:1 --verbosity quiet
dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false -v minimal
dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false -p:UsePrebuiltServerAssembly=true -v minimal
RELAXKONOS_FILE_JOB_SECONDARY_ROOT="$PWD/.codex-scratch" dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --file-operations-only
git diff --check
```

服务端测试的普通项目引用构建在本地出现既有 Protocol 传递引用缺失，因此使用项目已经提供的 `UsePrebuiltServerAssembly=true`，指向本轮刚编译的 Server/Protocol 程序集。只执行了文件操作专项，未将其他后端专项宣称为本轮验证。
