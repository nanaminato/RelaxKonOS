# 桌面组件中心生产宿主验收

这是显式开启的变更型验收入口，不属于默认模拟检查。它连接专用 RelaxKonOS System 宿主，复用生产 `SingBoxRuntimeCatalogClient`、`TaskPlanPreviewClient`、`TaskExecutionClient`、运行时与组件中心 ViewModel、草稿和任务引用存储，通过当前 HTTPS API 和宿主 Helper 执行生命周期。它不模拟领域回执，不调用本地安装器，不绕过冻结计划或审批。

## 前置条件

- 使用允许清理本次测试数据的专用宿主及专用登录用户。登录会获取该用户 Workspace 的控制权，不要使用正在处理业务的账号。
- sing-box 必须未安装、无运行中或待恢复操作；业务入站、使用者、凭据、订阅、发布/回退配置和保留的运行时修订均须为空。每个动作前再次检查业务消费者。
- 宿主已独立登记并审查可信签名来源，包含该目标平台和架构上两条明确的、受支持的稳定版发布。旧版与新版必须能证明真实跨版本更新；不以同版本 Repair 代替。
- Server、Helper 必须包含当前任务执行与证据接口。HTTPS 证书须通过运行客户端的正常系统信任链和名称验证。入口不允许明文 HTTP、凭据 URL、重定向或跳过证书验证。
- 设置 `RK_CC_PASSWORD`、`RK_CC_ADMIN_PASSWORD`。管理员用户名可由 `RK_CC_ADMIN_USERNAME` 指定，缺省使用登录用户名。不要把密码放在参数或聊天中。

## 运行

### 准备可复现的 Linux 签名来源

在测试宿主之外、具备 `cryptography` 的 Python 准备机运行：

```powershell
python Tests/ManagedRuntimes/prepare_singbox_http_fixture.py --base-uri https://192.168.1.7:18443/ --source cc-singbox-unique-id --older-archive /local/older.tar.gz --newer-archive /local/newer.tar.gz --output /local/new-private-source
```

生成器只接受与 `SingBoxRuntimeAcceptance.cs` 相同的已审查 Linux amd64 1.13.21/1.14.3 归档摘要，逐文件生成签名布局，不解包或运行输入。新目录包含 `policy.json`、`catalog.json` 和 `publisher-private.pem`；只将前两个文件及两份原始归档交付测试机，签名私钥留在准备机。归档分别以 `sing-box-1.13.21-linux-amd64.tar.gz` 和 `sing-box-1.14.3-linux-amd64.tar.gz` 提供。HTTPS 镜像证书必须在 Helper 和客户端正常信任链内，不能关闭验证。清单有效六小时，策略仅允许一个明确私网地址和端口，没有重定向权限。

使用本机管理员入口先审查策略，再以返回指纹明确登记；已有 sourceId 不得删除历史重新登记：

```sh
RelaxKonOS.PrivilegedHelper runtime-source inspect /protected/policy.json
RelaxKonOS.PrivilegedHelper runtime-source confirm /protected/policy.json 0 <inspect返回的指纹>
```

测试源发布者仅代表本地验收身份。结束后把策略 `enabled` 改为 `false`，使用当前预期信任修订经 inspect/confirm 提交，保留来源/目录历史；停止本次镜像，撤下仅为验收添加的 CA、TLS 临时私钥及独立服务授权。记录源码快照、实际执行的 Client/Server/Helper 程序集摘要和证据目录；旧构建通过不能证明新工作树通过。

### 执行当前桌面客户端

先构建：

```powershell
dotnet build Tests/Client/RelaxKonOS.Installation.Tests/RelaxKonOS.Installation.Tests.csproj --no-restore -m:1 -p:UsedAvaloniaProducts=
```

下面的服务器、账号、来源与 releaseId 必须替换成已准备好的专用测试目标。第二个服务器参数必须与第一个逐字相同，表示明确确认执行以下全部生命周期操作：

```powershell
$testServer = 'https://test-host.example:5000/'
dotnet run --project Tests/Client/RelaxKonOS.Installation.Tests/RelaxKonOS.Installation.Tests.csproj --no-build --no-restore -- --component-center-host $testServer $testServer cc_test sing-box-acceptance release-1.13.21 release-1.14.3 artifacts/component-center-host
```

入口依次执行：安装旧版、更新新版、同版本修复、回退旧版、保留数据卸载、重装旧版、删除本次测试数据卸载。首次安装检查不启动服务、不开机启动。更新必须改变实测版本；保留数据重装比较配置身份；每步核对实际宿主、安装身份、状态修订、活动发布和匹配回执。

每个动作均通过桌面草稿和冻结计划，明确确认后审批、启动；同键启动重放必须返回同一任务。随后重建组件中心 ViewModel 和本地存储实例，检查重开只读观察不会推进 Queued 任务，明确继续后才推进原任务，并读取持久宿主证据。此路径不直接提交旧领域安装入口。

## 证据与失败处理

每次调用在输出目录下建立独立 `component-host-<随机ID>` 子目录，保存公开任务引用、草稿和原子更新的 `evidence.json`。不保存登录响应、token、密码、配置正文或管理员授权。接受任务后立即记录任务身份；任一步失败、超时或 NeedsRecovery 都停止，保留证据，不自动清理、重新登录、取消或执行恢复。

查看最后输出的证据目录和原任务 ID，通过组件中心显式核对原操作后再决定处理方式。不要为了重跑而删除宿主保护账本或手工制造未安装状态。当前入口使用单次登录，在约四分钟内完成，接近令牌到期停止；不绕过控制租约或自动续批。慢下载/短租约环境须先解决测试条件，不能将超时当作安装失败后自动重装。

## 验证边界

### Server 重启后的新进程只读复核

七步验收成功后保留其 `component-host-…` 目录。记录专用 Server 的进程身份，重启该专用 Server，再记录新进程身份；不要重启正在处理业务的服务器。使用新客户端进程执行：

```powershell
dotnet run --project Tests/Client/RelaxKonOS.Installation.Tests/RelaxKonOS.Installation.Tests.csproj --no-build --no-restore -- --component-center-observe-host $testServer $testServer cc_test artifacts/component-center-host/component-host-PREVIOUS_ID artifacts/component-center-observations
```

仅需 `RK_CC_PASSWORD`，不读取管理员凭据。入口要求原七步证据为 passed、七个任务引用属于当前用户和服务器，通过生产桌面 ViewModel 重开每个任务，核对历史实测版本、活动发布、状态修订和回执，并检查当前宿主仍无安装/数据/待恢复。观察使用新的随机输出目录，不覆盖原生命周期证据。HTTP 传输层仅在初次认证时允许登录 POST，此后只允许 GET；审批、推进、恢复、安装和再次登录均在传输前拒绝。新登录仍会获取专用用户 Workspace 控制权。

此复核验证已完成任务在 Server 重启后的持久读取；进程前后身份由宿主部署通道另存证，工具本身不声称已经重启 Server，不证明执行中断、取消或补偿。

### 排队和下载阶段的真实取消

`--component-center-cancel-host` 是另一个显式变更型验收入口，同样仅用于无安装、无保留配置/修订、无业务消费者或待恢复的专用 System 宿主。准备有效的独立签名来源与正常受信任 TLS。用 `serve_singbox_cancellation_fixture.py` 替代普通镜像：

```sh
python3 Tests/ManagedRuntimes/serve_singbox_cancellation_fixture.py \
  --bind 192.168.1.7 --port 18443 --directory /protected/cancellation-mirror \
  --certificate /protected/server.crt --key /protected/server.key \
  --gate-file /protected/cancellation-control/download-started.json
```

镜像目录仅放签名目录和前述两份原始归档。镜像进程对 gate 文件的父目录须有写权限；gate 文件必须不存在。该脚本只服务固定三条 GET/HEAD 路径，归档发送 65536 字节后暂停 45 秒，再继续传输，提供可重复的下载中取消窗口。gate 是本机测试证据，不传给生产 Server/Helper，不构成文件导入或安装权限。

在同一测试机运行当前 Linux 自包含客户端（或具备 SDK 的构建环境），通过环境变量提供 `RK_CC_PASSWORD`、`RK_CC_ADMIN_PASSWORD`、`RK_CC_ADMIN_USERNAME` 和 `RK_CC_DOWNLOAD_GATE`。最后一个变量指向镜像生成的本机绝对 gate 路径：

```sh
RelaxKonOS.Installation.Tests --component-center-cancel-host \
  https://192.168.1.7:15443/ https://192.168.1.7:15443/ cc_test \
  cc-cancellation-source cc-cancellation-source-v1.13.21 /protected/cancellation-evidence
```

先通过桌面草稿/冻结计划/审批创建任务，重开后在未推进时取消，要求无领域 operationId 且宿主安装状态修订不变。第二个任务明确继续，等待镜像已发送选定归档字节且原领域操作报告可取消，再通过桌面取消，等待原操作 Cancelled 回执；重复取消必须保留同一任务和终态修订。两次均核对原宿主/安装身份、无业务数据/保留修订/待恢复。失败保留原任务引用和证据，不自动重装、清理或恢复；先检查原操作再处理测试 gate，不删除安装或来源账本来重跑。此入口不证明切换后的晚到取消或执行中断恢复。

本入口覆盖生产桌面 ViewModel/客户端至任务 HTTP、领域台账和真实 Helper 的受管归档生命周期路径；它没有显示实际桌面窗口，不证明鼠标交互、真实窗口关闭、Server 进程重启、取消时机、补偿故障或协议互通。布局、键盘、权限变化、结果未知/恢复另由相应专项覆盖。真实宿主上的执行结果必须单独记录平台、来源、两条版本和证据文件，不能用工具编译或默认模拟检查通过宣称实机验收完成。

2026-10-25：入口编译及 11 项参数门禁检查通过，未取得本次专用宿主的真实执行证据；CC-01～CC-03 仍为实施中。

2026-10-25 后续实际执行：当前 Linux 构建在 `192.168.1.7` 的独立 HTTPS System 实例通过七步生命周期 **87 项**，Server 重启后新客户端只读复核 **28 项**通过，来源禁用后加强身份检查的版本再次通过；客户端默认回归 **381 项**通过。来源为 `cc-current-20261025`，两版本为 1.13.21/1.14.3，测试结束已禁用来源并撤下临时授权、CA 和 TLS 私钥。源码/程序集摘要、证据位置、清理与范围见[当前构建真实闭环记录](../../../docs/platform/RelaxKonOS.ComponentCenter.Design.md#桌面当前构建真实宿主闭环2026-10-25)。CC-01～CC-03 的全部交付与故障场景尚未完成。

2026-10-25 取消专项：`cc-cancel-20261025` 的真实排队取消与传输中取消 **27 项通过**，源已禁用并保留账本，临时环境已撤下；桌面取消修订/权限竞争的最终默认回归 **410 项通过**。实测包和实测后补充查询前门禁回归的边界见[取消修订竞态与证据](../../../docs/platform/RelaxKonOS.ComponentCenter.Design.md#桌面取消修订竞态与真实传输取消2026-10-25)。

### 当前提交证明验收

七步生命周期入口现在对每个已完成原操作检查不可变 commitWitness 与 Helper 新鲜核验结果完全一致，共 94 项；旧的 87 项实测记录对应新增证明检查前的构建。当前 HTTP 快照必须显式包含 commitWitness，null 表示没有可用原提交证明；历史保存的缺失证明不能在读取时补造。正常七步成功不证明真正执行中断或补偿成功。

2026-10-25 当前提交证明构建：专用 Debian 13 x64 上七步 94 项、重启后只读复核 28 项通过；来源和临时环境已撤下，保护账本保留。实际部署摘要与公开证据包见[组件中心提交证明](../../../docs/platform/RelaxKonOS.ComponentCenter.Design.md#桌面原操作成功提交证据2026-10-25)。
