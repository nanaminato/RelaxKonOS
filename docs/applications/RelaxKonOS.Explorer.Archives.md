# 文件压缩与解压缩设计

日期：2026-10-16

## 范围与交互

桌面文件管理器新增“压缩为 ZIP”和“解压到新文件夹”。压缩可选择多个文件、目录，输入同目录 ZIP 文件名；单项默认 `<名称>.zip`，多项默认 `Archive.zip`。解压仅对单个 `.zip` 显示，输入同目录新文件夹名。选择和目标在打开输入框前固定，提交前复核会话。选择器与 SSH 浏览器不展示入口。

首版使用 .NET `System.IO.Compression`，支持 ZIP/ZIP64、UTF-8 名称、递归目录、空目录与空文件。暂不支持 RAR、7z、TAR、加密包、分卷、包内编辑、符号链接、权限/所有者恢复，以及将内容合并进已有目录；不执行宿主压缩命令。

## 任务与契约

扩展 `FileOperationKind`：`Compress`、`Extract`，沿用 `/files/operations`、幂等请求、身份隔离、重叠路径串行、轮询、取消与完成刷新。压缩请求为多个 `FileOperationItem(SourcePath, 同一个ZIP目标路径)`；解压请求严格为一个 `FileOperationItem(ZIP路径, 新目录路径)`。服务端再次规范化并校验源、目标和重叠选择。

同名目标只允许保留两者、跳过，或取消整个任务；不覆盖或合并。压缩使用目标同目录临时文件，解压使用同目录临时目录，完成后以不覆盖方式移动到目标。失败或取消删除临时输出，保留源和既有目标。发布后的取消不能撤销已经完成的移动。进度为当前输入文件的已处理字节；目录/文件处理数复用任务卡片，压缩比和总剩余时间不作估计。

## 权限与实现边界

压缩算法在 Server 进程中流式运行。用户路径全部通过任务捕获的 OS 身份访问：Personal Unix 沿用直接用户模式；Windows 和 System Mode 沿用既有 user-execution 请求（目录列举、分块读取、暂存追加、创建目录、移动和删除）。Root 会话沿用已授权 Helper。不会把后台任务变成 Server 服务账号的文件访问，也不新增任意命令执行能力或第二套 HTTP 文件接口。归档卡片不自动申请涵盖读、写、创建、移动和删除的组合提权；权限拒绝后可以调整权限再重试。已生效的授权仍按每个底层请求检查，不将解压获得的条目路径自动授权为 root。

读流可寻址（解压读取中央目录），算法缓冲与追加写块不超过 128 KiB；身份绑定读流复用现有最大 1 MiB 的读取缓存。任务清理使用独立取消令牌，即使任务被取消也尝试清理；清理失败记入任务详情，异常退出遗留不做跨重启自动回放。

## 路径与资源策略

压缩不跟随链接，不压缩目标自身；目录深度最多 128。ZIP 条目使用 `/` 分隔，拒绝空/点/父目录组件、绝对路径、盘符/冒号、反斜杠及控制字符；每个组件按可移植文件名校验，避免 Windows ADS 和设备名。解压校验规范化后重复名称、大小写碰撞和文件/目录祖先冲突，拒绝 Unix 符号链接、设备等非普通条目与 DOS reparse 属性。不恢复归档权限、所有者或可执行位；首版以内容和目录结构为准，不保证时间戳等元数据。

首版限制：最多 10,000 条目，单文件最多 4 GiB，总未压缩数据最多 10 GiB；超过 1 MiB 的条目最大压缩比 1,000。声明值预检与实际写入计数双重检查，校验内容长度及 CRC，整个包无效则不发布任何目标。ZIP 输入和压缩输出上限均为 2 GiB；读取尾部 EOCD/ZIP64 记录，在 BCL 构造条目之前限制中央目录不超过 64 MiB。中央目录元数据由 BCL 解析，内存随受限元数据增长；正文始终分块。

## 验证与上线边界

服务端测试：文件/目录往返、Unicode、空条目、多选、重名与保留两者、幂等、取消清理、路径穿越/绝对路径/反斜杠、重复和祖先冲突、链接、长度/容量限制、损坏包、权限拒绝、身份绑定分块请求。客户端测试：命令准入、固定源与目标、会话切换保护、任务提交与刷新。编译 Server、桌面及三种语言资源。

安全依据：[.NET ZIP/TAR 最佳实践](https://learn.microsoft.com/en-us/dotnet/standard/io/zip-tar-best-practices)。真机 System Mode、权限提升及大包性能另行验收，不以单元测试代替。


## 实现与验收记录

实现文件：`Shared/RelaxKonOS.Protocol/Files/FileOperationDto.cs`、Server 的 `FileArchiveService` / `ArchiveFileSystem`、`FileOperationService.Archives`、Client 的 `ExplorerViewModel.Archives` 及已有右键菜单。Linux Helper 的移动在不覆盖模式下使用 `renameat2(RENAME_NOREPLACE)`，防止校验后的目标竞争覆盖。

专项命令：

```text
dotnet build RelaxKonOS.Server.Tests -m:1
dotnet run --project RelaxKonOS.Server.Tests --no-build -- --archives-only
dotnet run --project Tests/Client/RelaxKonOS.Explorer.Tests
python Tools/verify-localization.py
```

测试使用工作区 `.tmp/relaxkonos-archive-tests-*` 的独立生成样本并在结束时清理，不读取用户文件；在当前受限 Windows 环境中，系统临时目录的文件发布被权限拒绝，因此专项改在工作区验证。身份绑定测试使用模拟 transport 检查请求契约和捕获身份；不代表已连接实际 Linux/Windows System Helper。实际系统账号、root Helper allowlist、断线、中途 Helper 进程终止和 GiB 级性能仍需部署验收。任务能报告清理失败；进程异常退出或创建请求在 IPC 取消时已执行但未确认，可能遗留临时条目，首版没有跨重启清理扫描。


本轮结果：37 项服务端归档专项、348 项 Explorer 回归、4291 个三语资源键一致性检查通过；Server（含测试）、PrivilegedHelper 和桌面客户端（含 AXAML）编译通过。构建仍报告仓库现有依赖安全告警。
