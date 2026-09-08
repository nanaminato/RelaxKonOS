# RelaxKonOS 设置系统与设置应用

当前执行基线与完整验收矩阵见 [SettingsSystem.Goal](./RelaxKonOS.SettingsSystem.Goal.md)。G1 正在实施，以下只描述已有代码，不把规划中的宿主写入标为可用。

## 服务与真源

设置应用是入口，偏好读写服务可独立调用。Client 的 `Services/WorkspaceSettings/IWorkspaceSettingsService` 供 Settings、Shell、Explorer、SDK、编码设置及默认程序入口共同使用。`WorkspacePreferencesEditor` 管理冻结草稿、300ms 防抖、目标绑定及重试；窗口关闭不会取消保存，连接变化清理旧目标草稿。`PreferencesSync` 负责登录加载与设置变化订阅，更新 ShellSettings 和 DefaultAppRegistry。订阅绑定连接目标，重连先订阅再重取快照；有草稿时保留草稿，避免远端变化覆盖编辑。

Server `Settings/IWorkspaceSettingsService` 管理偏好验证和版本比较。真源为配置注册表 `Workspace\Desktop` 的 `(Default)` JSON 值；当前 SQLite 缓存延迟落盘，因此 UI 先显示“服务端已接收，等待持久化”；读回的 `persistedRevision` 与保存版本一致时才显示“已保存”。损坏的偏好返回错误并保留原数据。

`GET /api/v1.0/workspaces/{id}/preferences` 返回 `WorkspacePreferencesDto.revision`；PUT 使用同一 DTO，必须携带编辑基线 revision。缺失返回 428，冲突返回 409。不能先读取新 revision 再给旧草稿换版本强行保存。Workspace 归属由认证身份校验；AppSettings 仍仅存应用私有偏好，不能存 OS 配置。

## 实时变化

`/hubs/settings-changes` 使用现有 SignalR 与 JWT 认证。`Subscribe(workspaceId)` 验证认证用户拥有该 Workspace；服务端每秒观察已订阅资源的 revision 与持久 revision，因此普通偏好 API、壁纸更新和注册表编辑器的写入均能触发通知。通知只包含 Workspace 标识与版本，不携带偏好值，接收端使用授权 GET 重读。连接过期关闭，客户端重连重新订阅并读取；另有 30 秒只读恢复检查，修复丢失通知或失败读取，不排队重放写操作。

## 当前页面与保存状态

当前保留系统、个性化、时间和语言、网络、应用、镜像源、默认应用、开发者八页及已有壁纸、主题、Shell 和包工具能力。保存状态支持中文、英文、日文；失败保留草稿并可重试，冲突保留草稿，提供明确的“放弃草稿并重载”操作；重载失败仍保留草稿。逐字段冲突合并体验、目录搜索、首页、账户、辅助功能和响应式导航仍按 Goal 推进，尚未验收。

## 范围与宿主权限

- ClientDevice：此客户端设备的布局、开发模式和辅助功能。
- Workspace：当前用户 Workspace 的主题、语言、默认应用及后续环境覆盖。
- AppPrivate：AppSettings 隔离的应用私有配置。
- HostUser：认证映射的远程 UID/SID，不能使用 Server 服务账户的用户环境。
- HostMachine：远程机器配置，不归某个 Workspace 所有。

允许设置远程环境变量、时区、主机名和受支持 DNS；旧“Settings 不触及宿主配置”“环境变量操作一律禁止”限制已废止。Server 保持非特权，通过现有 Helper 封闭操作、身份绑定、授权、审计、读回及恢复实现。环境配置数据不得注入 Helper 或特权子进程启动环境。DNS 写入前必须具备不依赖 Client/Server 存活的宿主恢复任务。

远程配置 provider 与全部宿主验收尚未完成；必须在明确指定的远程测试主机或隔离 VM 验证，不得在开发机实验后声称远程验收通过。

## 宿主时区服务（实现，尚未实机验收）

新增目录和时区 GET/preview/apply，以及操作查询与回滚 API。Server 通过原有 Helper 执行 Windows tzutil / Linux timedatectl；预览计划持久加密，应用需要精确 `host/time` 授权，读回成功才报告 Applied。外部版本变化会阻止应用或回滚；丢失结果为 Unknown，不自动重放。当前设置窗口的时区编辑交互尚待接入，不能将 API 构建通过视为完整时区交付。
