# 文件压缩、解压和包内预览

日期：2026-10-16

## 后端与能力

`FileArchiveService` 统一调度归档操作。ZIP、TAR、TAR.GZ/TGZ 保留 .NET BCL；7z、RAR、ISO、CAB、XZ、BZIP2、ZSTD、WIM 的读取和解压使用随发行包携带的官方 7-Zip 26.04 CLI。`FileArchiveFormats` 区分识别、BCL 后端及创建能力；创建支持 ZIP/TAR/TAR.GZ 和 7z，不能把识别后缀当作支持创建。没有全局解析异常后的静默重试或系统 PATH 后备，也没有新增 SharpCompress/native DLL 的进程内绑定。

包内读取统一面对 `IArchiveReader`（验证后的条目列表与指定条目内容），BCL 和 CLI 对上层隐藏后端差异。受支持的后缀仍需要内容通过相应格式解码和安全验证；不是所有格式变体的支持承诺。支持 ZIP/7z 加密创建，以及后端支持的 ZIP/7z/RAR 密码读取；暂不支持分卷、链接、包内修改、权限/所有者恢复，以及合并同名已有子目录。ZIP 中实际使用的非 BCL 压缩方法在完成 BCL 元数据安全验证后交给 CLI；加密 ZIP 使用 CLI 验证与解码。损坏和安全错误不触发后端重试。

## Explorer 交互与协议

右键“压缩为…”提供 ZIP/TAR/TAR.GZ/7z，以及加密 ZIP、加密 7z；识别的归档文件提供“解压到当前目录”和“解压到…”：前者直接使用当前目录，后者打开服务器文件夹选择器，初始位置为当前目录；取消或会话变化不提交任务。无需输入目标路径或文件夹名。选择器及 SSH 文件浏览器不启用归档功能。

双击压缩包打开独立只读浏览窗口；从已验证的条目补出隐式父目录。双击文件夹或按 Enter 进入，向上按钮返回父目录。选择普通文件时按需请求内容，支持常见文本与位图图片；HTML/脚本仅显示文本，绝不执行。单文件预览上限为 16 MiB；PDF、视频、嵌套压缩包和其他类型需先解压。会话变化关闭窗口，关闭/切换选择取消未完成请求并释放图片。此阶段不跨请求缓存包内容。独立 XZ/BZIP2/ZSTD 没有成员路径，按源文件去后缀生成条目名；通过有界解码测量实际长度，避免信任缺失的大小元数据。

读取协议使用现有鉴权文件路由组和 Read 策略：

- `POST /api/v1.0/files/archive/entries`：请求 `{ path, credentials?: { password } }`，返回相对路径、目录标记、未压缩大小。
- `POST /api/v1.0/files/archive/content`：请求 `{ path, entryName, credentials?: { password } }`，返回经过完整验证的有界内容。每次重新鉴权、打开归档和验证条目；成员路径不会作为宿主文件路径使用。

固实包提取单文件可能需要解码前面的块；TAR.GZ 的列表/预览仍需顺序验证整个 TAR 和 GZip 尾部，不能承诺随机读取或视频拖动播放。CLI 外部格式每次请求需要有界输入快照，首版大包性能还有优化空间。

## 权限与 CLI 执行边界

压缩/解压继续沿用 `/files/operations` 的幂等、捕获身份、路径互斥、取消、进度和任务卡片。`ArchiveFileSystem` 决定所有用户源/目标访问；System Mode、Windows 和 root 会话的读写仍通过已有身份/授权适配器，权限拒绝不能切换到服务账号读取。

读取时，CLI 只收到一个通过已授权读流复制的随机私有临时输入。Unix 临时目录为 0700；Windows 使用只授予执行账号继承 FullControl 的受保护 DACL。CLI 不接收原始用户路径、目标目录或任意命令；密码通过 UTF-8 stdin 单行传递，命令行和环境变量中没有密码；固定格式选择器、ArgumentList、发送密码后关闭 stdin、清空继承环境、禁用通配选择并使用显式字面 include，防止 `@` 成员名被解释为宿主 listfile，并从 stdout 返回指定条目内容。目标路径只由 Server 根据验证后的条目生成，写入仍经身份适配器。隔离快照中没有相邻分卷；不运行 SFX。

解码进程沿用 Server 的 OS 身份，只处理快照。这个设计隔离生命周期和崩溃，**不是 OS 安全沙盒**，没有内核内存/CPU 配额；不应把它描述为已完成针对解码器漏洞的低权限隔离。真实 System Helper、不同 OS/架构与资源耗尽场景仍需部署验收。并发 CLI 上限 2，每次 CLI 调用超时 2 分钟，取消/失败终止进程树并等待退出，最后删除快照。

## 资源、路径与发布策略

最多 10,000 条目，单文件 4 GiB，总未压缩数据 10 GiB，输入及创建输出 2 GiB，元数据 64 MiB，深度 128。ZIP 在 BCL 分配条目之前验证中央目录/ZIP64 边界；TAR 在 BCL 前逐个限制原始头与 PAX/GNU 扩展记录，完整检查结束块和 GZip CRC/ISIZE。CLI 列表有界且拒绝歧义、重复字段、链接、reparse、ADS、anti-item；整体展开比与可用的逐条压缩大小受限。正文检查实际输出长度和 CLI 成功退出，ZIP 额外校验 CRC。

所有后端拒绝绝对/父目录/设备名/控制字符/非法组件、大小写与 Unicode 规范化冲突、文件作为父目录。CLI 的目录分隔符统一后再校验，文本路径不会直接用于文件创建。解压先写临时目录，全部验证成功后不覆盖移动。目标目录已存在时，在目标内部暂存，发布前检查全部顶层名称；同名文件或目录不覆盖、不合并，任务等待重试或跳过。发布中失败/取消会尝试逆序移回已发布条目，再清理暂存；回滚失败会记录清理错误，不能保证跨条目原子发布。新目标目录仍以整体移动发布。清理错误写入任务详情。未做跨重启临时条目扫描或缓存恢复。

`Assets/SevenZip/` 携带 win-x64、win-arm64 的 `7z.exe`/`7z.dll`，Linux x64/arm64 的 `7zz`，以及 macOS universal `7zz`。`runtime-files.json` 记录逐文件 SHA-256，执行前验证；`provenance.json` 记录上游下载及包哈希，原版许可证、README、源码链接随包发布。构建与 Linux 安装/修复流程显式恢复执行权限。默认不依赖用户安装的 7-Zip。分发包版本升级必须整体替换执行文件、DLL、哈希及许可证。

## 验证

```text
dotnet build RelaxKonOS.Server.Tests -m:1
dotnet run --project RelaxKonOS.Server.Tests --no-build -- --archives-only
dotnet run --project Tests/Client/RelaxKonOS.Explorer.Tests
dotnet build Client/RelaxKonOS.Client.Desktop -m:1
python Tools/verify-localization.py
```

Windows 本地验证：47 项原有/身份任务检查、20 项 TAR 检查、39 项 provider 检查通过；真实 7-Zip 覆盖 Unicode、选项形文件名、空目录、列表、单文件预览、解压、取消、损坏包、BZIP2 方法 ZIP 分流、独立 XZ/BZIP2 及身份读拒绝。387 项 Explorer 回归包含包内目录导航、文本只读显示、预览限额及不获取可执行文件；4311 个三语键一致。Server 和桌面编译通过。其他格式变体、Linux/macOS/ARM64 实机、真实 Helper 及 GiB 级性能未在本轮验收；打包这些运行文件不代表完成对应平台运行验证。
## 加密与密码生命周期

“加密 ZIP”固定使用 AES-256，ZIP 文件名仍可见。“加密 7z”使用 7z AES-256，并启用文件名/头加密。创建时需确认密码；取消不提交任务。7-Zip 26.04 的 ZIP 写入器要求可打印 ASCII 密码、最长 99 字符，界面和服务端均提前校验；7z 允许 Unicode 密码。公共密码输入上限 1024 个 UTF-16 字符，拒绝 NUL、CR、LF 和无效 Unicode，保留前后空格。不转换、不截断密码。读取外部压缩包的兼容性取决于实际后端。

读取没有密码或密码错误时返回 `archive-password-required` / `archive-password-invalid`；后者也可能表示加密内容损坏。浏览窗口会提示并允许重试。解压任务停留在等待决策状态，选择“输入密码并重试”提交当前 issue 的密码；不能对所有任务应用同一密码。可以跳过或取消。先完成内容校验再发布目标，因此错误密码不发布部分目录。

列表改用 POST，请求密码仅放在 JSON body；列表和预览返回 `Cache-Control: no-store`。任务提交和决策可以携带 `credentials`，但 Job 的普通 Request、Decision、轮询 DTO 和列表均排除密码。密码只在活动任务/浏览窗口内存中保留引用，结束、取消、关闭或切换会话时释放；不写 usage memory、日志、任务持久化、文件或 URL。幂等比较使用服务实例随机密钥的 HMAC，不保留普通密码摘要。托管字符串不可承诺物理内存即时清零。

CLI 创建先经身份适配器读取已验证源，复制到私有临时 sources 目录，CLI 只处理该快照，生成结果经原适配器追加并发布。暂存源是明文；正常完成、失败和取消会删除，异常退出仍可能遗留，未实现安全擦除或跨重启恢复。创建输出监视器约每 100 ms 检查 2 GiB 上限，最终再做长度校验；并非内核磁盘配额。进度的输入复制完成不代表 CLI 压缩已完成。

新增 27 项真实 CLI 加密检查覆盖 AES ZIP、加密头 7z、普通 7z 创建、Unicode/空格密码、JSON 往返、缺少/错误密码、单文件预览、解密发布及失败清理；身份任务另覆盖密码重试、无密码快照及含密码提交的幂等。Explorer 回归新增创建密码、取消/会话切换、浏览密码重试和任务重试不跨任务复用密码。

上游约束依据：[ZIP 写入器](https://github.com/ip7z/7zip/blob/26.04/CPP/7zip/Archive/Zip/ZipHandlerOut.cpp)、[AES 密码长度](https://github.com/ip7z/7zip/blob/26.04/CPP/7zip/Crypto/WzAes.h)、[stdin 密码读取](https://github.com/ip7z/7zip/blob/26.04/CPP/7zip/UI/Console/UserInputUtils.cpp)。
