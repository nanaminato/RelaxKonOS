# RelaxKonOS 事件与告警中心：实施进度

最后更新：2026-09-21（UTC）  
状态：进行中；**不得**据此关闭 Goal。

## 已完成

- [x] M0 的第一版契约冻结：首批事件类型、来源、默认严重性、资源类型、固定跳转种类和人工关闭允许范围由 `EventAlertCatalog` 集中定义。
- [x] M1 的核心持久化：独立 SQLite 事件账本、告警投影、处理动作和抑制表；事件以 source event key 幂等追加，告警以目录控制的 dedupe key 聚合。
- [x] M1 查询面：`/api/v1.0/event-alerts` 的 events、alerts、detail、summary 游标分页/筛选 API，以及 page-size 和 cursor 验证。
- [x] M1/M4 基础受控动作：确认、允许类型的人工关闭、期限抑制和解除抑制；说明限制为 1–512 字符并在持久化前净化。
- [x] M4 审计失败关闭：详情读取及所有处理动作均先写 Security Audit；审计不可写时不执行处理动作。
- [x] 实时失效通知：独立、授权的 SignalR hub 只传递 alert ID、版本、状态、严重性和次数；客户端必须重新读取 REST 数据。
- [x] M5 的最小读取体验：注册只声明读取权限的 `relaxkonos.event-alerts` 内置应用，新增读/管两项应用权限定义、三语文案和摘要/告警列表刷新界面。
- [x] 新应用权限及 Server `EventsRead`、`EventsManage`、`EventsCriticalSuppress` policy 已接入。
- [x] M2 的部署失败最小接入：`ApplicationDeploymentCoordinator` 在其 terminal ledger 已持久化后发布失败或成功恢复信号；中心失败不会回滚部署。

## 尚未完成（不可作为已交付功能宣传）

- [ ] M2：部署启动重放，以及证书、Docker、隧道的结构化发布器、状态监视器、宽限/退避与持久 journal 重放。
- [ ] M3：Guardian Agent 的 sequence 事件账本、checkpoint、可靠拉取、可用性监视器和重启补偿。
- [ ] M4：按目标领域的二次资源授权、Critical 独立权限角色以及跳转请求审计。
- [ ] M5：详情抽屉、处理操作界面、SignalR 客户端订阅、壳状态徽章/toast、固定激活处理器和各领域深链。
- [ ] M6：Linux Docker/FRP 与 Windows Guardian 的真实环境演练、容量/性能、完整 README/帮助文档和 Android 读取契约评审。
- [ ] 受抑制告警到期时当前存储会恢复为 Open；仍需补充对应 hub 变更广播及自动化测试。

## 本次验证

- 通过：`dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj -c Debug --no-restore`（仅既有两条平台可达性警告）。
- 已新增：`RelaxKonOS.Server.Tests/EventAlertChecks.cs`，覆盖 append、净化、同 key 聚合、确认和恢复投影；尚未实际执行，见下一节。

## 暂时跳过／需恢复的测试

| 项目 | 尝试命令 | 当前结果 | 恢复条件 |
| --- | --- | --- | --- |
| 新增 EventAlertChecks | `dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj -c Debug --no-restore` | 立即失败，退出码非零且没有 MSBuild 错误输出；诊断显示失败发生在 restore project-path graph。 | 修复/确认该现有 restore graph 问题后，执行 build，再运行 `dotnet run --project RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj -c Debug -- --deployment-progress-only`（它会先执行 EventAlertChecks）。 |
| 客户端编译 | `dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj -c Debug --no-restore` | 立即失败，退出码非零且没有编译错误输出。 | 修复客户端项目评估/restore graph 后重新构建，特别检查新 EventAlerts 应用的 Avalonia API。 |
| 全解决方案 | `dotnet build RelaxKonOS.sln -c Debug --no-restore` | 立即失败，退出码非零且没有编译错误输出。 | 在以上项目评估问题解决后执行；Goal §17 要求最终必须通过。 |
| 领域与故障演练 | 未执行 | M2/M3/M6 尚未实施。 | 完成各采集器和 Guardian 可靠协议后，按 Goal §14、§17 逐项补充并执行。 |

补充：直接运行当前磁盘中既有的 `RelaxKonOS.Server.Tests.dll --deployment-progress-only` 会先通过旧的部署诊断检查，随后因测试宿主无法绑定 Kestrel socket（`SocketException: Permission denied`）失败；该二进制未包含本次新增测试，不能作为 EventAlertChecks 的执行证据。
