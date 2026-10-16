# 文件压缩、解压和包内预览

日期：2026-10-16

## 后端与能力

`FileArchiveService` 统一调度归档操作。ZIP、TAR、TAR.GZ/TGZ 保留 .NET BCL；7z、RAR、ISO、CAB、XZ、BZIP2、ZSTD、WIM 的读取和解压使用随发行包携带的官方 7-Zip 26.04 CLI。`FileArchiveFormats` 区分识别、BCL 后端及创建能力；本阶段创建仍仅开放 ZIP/TAR/TAR.GZ，不能把识别后缀当作支持创建。没有全局解析异常后的静默重试或系统 PATH 后备，也没有新增 SharpCompress/native DLL 的进程内绑定。

包内读取统一面对 `IArchiveReader`（验证后的条目列表与指定条目内容），BCL 和 CLI 对上层隐藏后端差异。受支持的后缀仍需要内容通过相应格式解码和安全验证；不是所有格式变体的支持承诺。暂不支持密码、分卷、链接、包内修改、权限/所有者恢复，以及解压合并已有目录。ZIP 中实际使用的非 BCL 压缩方法在完成 BCL 元数据安全验证后交给 CLI；加密 ZIP 明确拒绝。损坏和安全错误不触发后端重试。

## Explorer 交互与协议

右键“压缩为…”继续提供 ZIP/TAR/TAR.GZ；识别的归档文件提供“解压到新文件夹”。解压默认名称去掉完整归档后缀。选择器及 SSH 文件浏览器不启用归档功能。

双击压缩包打开独立只读浏览窗口；从已验证的条目补出隐式父目录。双击文件夹或按 Enter 进入，向上按钮返回父目录。选择普通文件时按需请求内容，支持常见文本与位图图片；HTML/脚本仅显示文本，绝不执行。单文件预览上限为 16 MiB；PDF、视频、嵌套压缩包和其他类型需先解压。会话变化关闭窗口，关闭/切换选择取消未完成请求并释放图片。此阶段不跨请求缓存包内容。独立 XZ/BZIP2/ZSTD 没有成员路径，按源文件去后缀生成条目名；通过有界解码测量实际长度，避免信任缺失的大小元数据。

读取协议使用现有鉴权文件路由组和 Read 策略：

- `GET /api/v1.0/files/archive/entries?path=...`：返回相对路径、目录标记、未压缩大小。
- `POST /api/v1.0/files/archive/content`：请求 `{ path, entryName }`，返回经过完整验证的有界内容。每次重新鉴权、打开归档和验证条目；成员路径不会作为宿主文件路径使用。

固实包提取单文件可能需要解码前面的块；TAR.GZ 的列表/预览仍需顺序验证整个 TAR 和 GZip 尾部，不能承诺随机读取或视频拖动播放。CLI 外部格式每次请求需要有界输入快照，首版大包性能还有优化空间。

## 权限与 CLI 执行边界

压缩/解压继续沿用 `/files/operations` 的幂等、捕获身份、路径互斥、取消、进度和任务卡片。`ArchiveFileSystem` 决定所有用户源/目标访问；System Mode、Windows 和 root 会话的读写仍通过已有身份/授权适配器，权限拒绝不能切换到服务账号读取。

CLI 只收到一个通过已授权读流复制的随机私有临时输入。Unix 临时目录为 0700；Windows 使用当前账号临时目录。CLI 不接收原始用户路径、目标目录、密码或任意命令；固定格式选择器、ArgumentList、关闭 stdin、清空继承环境、禁用通配选择并使用显式字面 include，防止 `@` 成员名被解释为宿主 listfile，并从 stdout 返回指定条目内容。目标路径只由 Server 根据验证后的条目生成，写入仍经身份适配器。隔离快照中没有相邻分卷；不运行 SFX。

解码进程沿用 Server 的 OS 身份，只处理快照。这个设计隔离生命周期和崩溃，**不是 OS 安全沙盒**，没有内核内存/CPU 配额；不应把它描述为已完成针对解码器漏洞的低权限隔离。真实 System Helper、不同 OS/架构与资源耗尽场景仍需部署验收。并发 CLI 上限 2，每次 CLI 调用超时 2 分钟，取消/失败终止进程树并等待退出，最后删除快照。

## 资源、路径与发布策略

最多 10,000 条目，单文件 4 GiB，总未压缩数据 10 GiB，输入及创建输出 2 GiB，元数据 64 MiB，深度 128。ZIP 在 BCL 分配条目之前验证中央目录/ZIP64 边界；TAR 在 BCL 前逐个限制原始头与 PAX/GNU 扩展记录，完整检查结束块和 GZip CRC/ISIZE。CLI 列表有界且拒绝歧义、重复字段、加密、链接、reparse、ADS、anti-item；整体展开比与可用的逐条压缩大小受限。正文检查实际输出长度和 CLI 成功退出，ZIP 额外校验 CRC。

所有后端拒绝绝对/父目录/设备名/控制字符/非法组件、大小写与 Unicode 规范化冲突、文件作为父目录。CLI 的目录分隔符统一后再校验，文本路径不会直接用于文件创建。解压先写目标同目录临时目录，全部成功后不覆盖移动；失败/取消清理整个暂存。清理错误写入任务详情。未做跨重启临时条目扫描或缓存恢复。

`Assets/SevenZip/` 携带 win-x64、win-arm64 的 `7z.exe`/`7z.dll`，Linux x64/arm64 的 `7zz`，以及 macOS universal `7zz`。`runtime-files.json` 记录逐文件 SHA-256，执行前验证；`provenance.json` 记录上游下载及包哈希，原版许可证、README、源码链接随包发布。构建与 Linux 安装/修复流程显式恢复执行权限。默认不依赖用户安装的 7-Zip。分发包版本升级必须整体替换执行文件、DLL、哈希及许可证。

## 验证

```text
dotnet build RelaxKonOS.Server.Tests -m:1
dotnet run --project RelaxKonOS.Server.Tests --no-build -- --archives-only
dotnet run --project Tests/Client/RelaxKonOS.Explorer.Tests
dotnet build Client/RelaxKonOS.Client.Desktop -m:1
python Tools/verify-localization.py
```

Windows 本地验证：40 项原有/身份任务检查、20 项 TAR 检查、39 项 provider 检查通过；真实 7-Zip 覆盖 Unicode、选项形文件名、空目录、列表、单文件预览、解压、取消、损坏包、BZIP2 方法 ZIP 分流、独立 XZ/BZIP2 及身份读拒绝。378 项 Explorer 回归包含包内目录导航、文本只读显示、预览限额及不获取可执行文件；4296 个三语键一致。Server 和桌面编译通过。其他格式变体、Linux/macOS/ARM64 实机、真实 Helper 及 GiB 级性能未在本轮验收；打包这些运行文件不代表完成对应平台运行验证。
