# Windows Helper Git 验收（2026-10-25）

本次在用户授权的 Windows Server 2022（10.0.20348）主机执行真实安装验收，不是模拟 transport。主机初始未检测到 RelaxKonOS 服务、Git 或 .NET；部署自包含 Release 构建，Server 为 LocalService，Helper 为 LocalSystem，用户执行后端为 `helper`，该测试主机显式启用用户管道。本轮没有修改产品全局的能力开关默认值。

## 环境与制品

- 普通本地账户 `codexdev` 与内置 Administrator；Administrator 的普通 Git 操作也经过受限 S4U token 检查。
- Git for Windows `2.56.0.windows.2`。客户端依赖使用官方 MinGit，SHA-256 为 `da35e72aa21c005a5a0d298cfbae110bc1609a815730ea0dde84b01a1b3cd3be`。
- HTTPS 测试服务使用同版本官方 Portable Git 的 `git-http-backend`，SHA-256 为 `075e158ef8e1f0ab80b347e245405d3eca735c2dc88fd8e032e137d0ca61f61b`。MinGit 不携带该服务端入口，不能用它代替 smart HTTP 测试服务器。
- 两份归档来自 [Git for Windows 固定发布](https://github.com/git-for-windows/git/releases/tag/v2.56.0.windows.2)，下载后与该发布的摘要比对。便携 Git 是验收环境前置条件，不代表 RelaxKonOS 已实现 Git 受管安装、更新或回滚。
- HTTPS fixture 仅监听回环地址，使用临时受信任测试证书、真实 Basic challenge 和合成令牌；没有关闭 TLS 验证，没有使用真实托管平台令牌。

首次验收构建（下述 fetch/pull 补齐前）的 DLL 摘要与本地构建一致：

| 程序 | SHA-256 |
| --- | --- |
| Helper | `14AF0A540C4FECDFE42D88A9285E1C8160C48433D95173B6C4F4CB5928247F2A` |
| Server | `B78DDA68B2131B8AAEE25E0280ADB51EE18A863B430CE36B5C62CFEE6FBFFB75` |

## 已验证行为

| 检查 | 结果 |
| --- | --- |
| 普通账户原生测试 | 16 项通过：CreateProcessAsUser、参数往返、中文提交、真实 HTTP askpass、SID、闭合参数拒绝、取消、后代进程回收、输出超限、预取消 |
| LocalSystem 直接 S4U 检查 | 两个账户均通过 `WindowsUserExecutionExecutor` 的 Git 版本执行 |
| Server → LocalSystem Helper → S4U 端到端 | 两个账户各 20 项通过 |
| 跨用户隔离 | 双向查询对方仓库 ID 返回 404；双向注册对方私有目录返回 400，未借用 SYSTEM 权限打开目录 |
| 错误令牌与日志 | 两个账户显式错误令牌均被拒绝，不改用已保存的正确令牌；控制台未泄露错误令牌 |

端到端覆盖 OS 账户登录、按目标用户探测 Git、在 LocalService 不可访问的私有目录初始化和注册仓库、中文路径提交、状态读取，以及新建 index 的实际所有者。两个账户的 index 均属于各自用户，而不是 SYSTEM 或 LocalService。

真实 HTTPS 操作覆盖显式令牌推送、应用加密保存凭证后的新分支首次推送、保存凭证后的克隆及内容核对。Administrator 在尚未保存自身凭证时不能使用 `codexdev` 已保存的凭证；保存后重新登录可复用自己的凭证。用户名及令牌包含 Unicode 和 shell 特殊字符。

首次验收的 fetch/pull 使用账户私有的本地 bare remote；随后已补齐并验证 HTTPS 保存凭证复用，见下节。

## 同日补齐：保存凭证用于 HTTPS fetch/pull

fetch、pull 和非当前分支更新现在读取该用户为实际 HTTPS 下载地址保存的凭据。通过 Git 的离线 `ls-remote --get-url` 解析命名 remote、显式 URL 和 insteadOf 重写，不使用单独的 pushurl。Helper 仅允许此命令的 `--get-url` 形状，拒绝网络 ls-remote 和外部 upload-pack 选项。

应用显式/保存凭据通过 askpass 下发时，以进程级空 `credential.helper` 重置宿主助手，防止使用错误的宿主缓存或绕过“保存”选择写入 OS 凭据库。未保存的凭据仍仅用于当次操作。

更新上述测试主机的 Server/Helper 后，不重新提交 Git 用户名或令牌，`codexdev` 14 项、Administrator 13 项 HTTPS 专项通过；两者另各验证一次原本地 remote 仍可推送。覆盖下载/推送 URL 分离、冲突的宿主 credential helper、fetch、pull 实际 fast-forward 并拉回独立工作副本提交的新文件、非当前分支更新、显式 URL 和 insteadOf 重写。两个账户以前保存的应用凭据在服务更新与重新登录后仍可复用。

本地 GitWorkspace 64 项及扩展的凭据路由检查通过，验证用户、主机、端口、协议和 URL 重写边界；Windows 原生检查扩展为 17 项并通过，新增冲突宿主助手不能替换应用令牌的真实 HTTP 验证。最终部署摘要再次与本地发布产物核对：Server 为 `02A25FC4ACE9216BB4B983B43F39193CC8BAC5EFC990061BA879C5F39C17B2D6`，Shared Protocol 为 `210B7837C22789B393F881519CE3E41CD302C30AE7C12BF9A971A3632E90F7BE`。

## 边界与清理

Windows Credential Manager 在 S4U network logon 下实际出现无法使用 `wincredman` 的诊断；应用显式凭证和 Data Protection 保存的凭证链路通过，已覆盖 HTTPS push/clone/fetch/pull。本轮不宣称支持交互登录会话的 DPAPI credential manager、SSH agent 或映射盘；没有应用已保存凭据时仍沿用宿主路径或返回 RequiresCredentials。

测试 fixture 的 Git 子进程使用 SYSTEM 创建的远端 bare 仓库。管理员在 SSH 中创建的本地测试 bare 仓库须将 NTFS owner 明确设为该用户，以免受限用户 Git 正确触发 dubious ownership；没有在产品执行路径添加 `safe.directory=*` 或绕过所有权检查。

临时计划任务、HTTPS 绑定、测试证书及私钥、监听端口、暂存发布包和中间测试注册已清理。每个账户保留一个成功的私有工作仓库，其 origin 指向本地测试 bare remote；Server 与 Helper 保留运行。Server 为手动启动并监听 `127.0.0.1:5000`，Helper 自动启动，Guardian 未安装。

本地脱敏结果保存在 `.artifacts/windows-git-acceptance/acceptance-result.json`。完整 Windows 文件执行、更多普通账户的并发 ACL、服务重启和用户 profile 生命周期矩阵仍须单独验收，不能由本次 Git 专项代替。
