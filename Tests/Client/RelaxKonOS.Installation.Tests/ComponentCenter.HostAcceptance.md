# 桌面组件中心生产宿主验收

这是显式开启的变更型验收入口，不属于默认模拟检查。它连接专用 RelaxKonOS System 宿主，复用生产 `SingBoxRuntimeCatalogClient`、`TaskPlanPreviewClient`、`TaskExecutionClient`、运行时与组件中心 ViewModel、草稿和任务引用存储，通过当前 HTTPS API 和宿主 Helper 执行生命周期。它不模拟领域回执，不调用本地安装器，不绕过冻结计划或审批。

## 前置条件

- 使用允许清理本次测试数据的专用宿主及专用登录用户。登录会获取该用户 Workspace 的控制权，不要使用正在处理业务的账号。
- sing-box 必须未安装、无运行中或待恢复操作；业务入站、使用者、凭据、订阅、发布/回退配置和保留的运行时修订均须为空。每个动作前再次检查业务消费者。
- 宿主已独立登记并审查可信签名来源，包含该目标平台和架构上两条明确的、受支持的稳定版发布。旧版与新版必须能证明真实跨版本更新；不以同版本 Repair 代替。
- Server、Helper 必须包含当前任务执行与证据接口。HTTPS 证书须通过运行客户端的正常系统信任链和名称验证。入口不允许明文 HTTP、凭据 URL、重定向或跳过证书验证。
- 设置 `RK_CC_PASSWORD`、`RK_CC_ADMIN_PASSWORD`。管理员用户名可由 `RK_CC_ADMIN_USERNAME` 指定，缺省使用登录用户名。不要把密码放在参数或聊天中。

## 运行

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

本入口覆盖生产桌面 ViewModel/客户端至任务 HTTP、领域台账和真实 Helper 的受管归档生命周期路径；它没有显示实际桌面窗口，不证明鼠标交互、真实窗口关闭、Server 进程重启、取消时机、补偿故障或协议互通。布局、键盘、权限变化、结果未知/恢复另由相应专项覆盖。真实宿主上的执行结果必须单独记录平台、来源、两条版本和证据文件，不能用工具编译或默认模拟检查通过宣称实机验收完成。

2026-10-25：入口编译及 11 项参数门禁检查通过，未取得本次专用宿主的真实执行证据；CC-01～CC-03 仍为实施中。
