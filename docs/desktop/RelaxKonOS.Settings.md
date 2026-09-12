# RelaxKonOS 设置系统与设置应用

当前执行基线与完整验收矩阵见 [SettingsSystem.Goal](./RelaxKonOS.SettingsSystem.Goal.md)。G1 正在实施，以下只描述已有代码，不把规划中的宿主写入标为可用。

## 服务与真源

设置应用是入口，偏好读写服务可独立调用。Client 的 `Services/WorkspaceSettings/IWorkspaceSettingsService` 供 Settings、Shell、Explorer、SDK、编码设置及默认程序入口共同使用。`WorkspacePreferencesEditor` 管理冻结草稿、300ms 防抖、目标绑定及重试；窗口关闭不会取消保存，连接变化清理旧目标草稿。`PreferencesSync` 负责登录加载与设置变化订阅，更新 ShellSettings 和 DefaultAppRegistry。订阅绑定连接目标，重连先订阅再重取快照；有草稿时保留草稿，避免远端变化覆盖编辑。

Server `Settings/IWorkspaceSettingsService` 管理偏好验证和版本比较。真源为配置注册表 `Workspace\Desktop` 的 `(Default)` JSON 值；当前 SQLite 缓存延迟落盘，因此 UI 先显示“服务端已接收，等待持久化”；读回的 `persistedRevision` 与保存版本一致时才显示“已保存”。损坏的偏好返回错误并保留原数据。

`GET /api/v1.0/workspaces/{id}/preferences` 返回 `WorkspacePreferencesDto.revision`；PUT 使用同一 DTO，必须携带编辑基线 revision。缺失返回 428，冲突返回 409。不能先读取新 revision 再给旧草稿换版本强行保存。Workspace 归属由认证身份校验；AppSettings 仍仅存应用私有偏好，不能存 OS 配置。

## 实时变化

`/hubs/settings-changes` 使用现有 SignalR 与 JWT 认证。`Subscribe(workspaceId)` 验证认证用户拥有该 Workspace；服务端每秒观察已订阅资源的 revision 与持久 revision，因此普通偏好 API、壁纸更新和注册表编辑器的写入均能触发通知。通知只包含 Workspace 标识与版本，不携带偏好值，接收端使用授权 GET 重读。连接过期关闭，客户端重连重新订阅并读取；另有 30 秒只读恢复检查，修复丢失通知或失败读取，不排队重放写操作。

## 当前页面与保存状态

当前保留系统、个性化、时间和语言、网络、应用、镜像源、默认应用、开发者八页及已有壁纸、主题、Shell 和包工具能力。保存状态支持中文、英文、日文；失败保留草稿并可重试，冲突保留草稿，提供明确的“放弃草稿并重载”操作；重载失败仍保留草稿。逐字段冲突合并体验、首页、账户和辅助功能仍按 Goal 推进，尚未验收。

现有八页使用注册的 Route 导航，共用单色矢量图标；顶部持续显示当前远程连接、用户、Workspace 与分类路径，支持返回历史。小于 760 个逻辑像素时折叠侧栏，使用分类选择框。搜索先查询本地不可变索引，再异步合并远程目录；包括标题、关键词和同义词，显示分类、范围及服务端能力原因，连接切换清除旧目录。Ctrl+F 聚焦搜索、方向键浏览、Enter 或双击打开、Escape 退出搜索。当前结果定位到页面，settingId 控件聚焦与全部详情页仍待完成。页面内容本身的窄布局、200% 缩放及屏幕阅读器体验尚未实测。

## 范围与宿主权限

- ClientDevice：此客户端设备的布局、开发模式和辅助功能。
- Workspace：当前用户 Workspace 的主题、语言、默认应用及后续环境覆盖。
- AppPrivate：AppSettings 隔离的应用私有配置。
- HostUser：认证映射的远程 UID/SID，不能使用 Server 服务账户的用户环境。
- HostMachine：远程机器配置，不归某个 Workspace 所有。

允许设置远程环境变量、时区、主机名和受支持 DNS；旧“Settings 不触及宿主配置”“环境变量操作一律禁止”限制已废止。Server 保持非特权，通过现有 Helper 封闭操作、身份绑定、授权、审计、读回及恢复实现。环境配置数据不得注入 Helper 或特权子进程启动环境。DNS 写入前必须具备不依赖 Client/Server 存活的宿主恢复任务。

远程配置 provider 与全部宿主验收尚未完成；必须在明确指定的远程测试主机或隔离 VM 验证，不得在开发机实验后声称远程验收通过。

## 宿主时区服务（实现，尚未实机验收）

新增目录和时区 GET/preview/apply，以及操作查询与回滚 API。Server 通过原有 Helper 执行 Windows tzutil / Linux timedatectl；预览计划持久加密，应用需要精确 `host/time` 授权，读回成功才报告 Applied。外部版本变化会阻止应用或回滚；丢失结果为 Unknown，不自动重放。客户端 `Services/HostSettings/IHostTimeService` 独立于窗口，冻结 Server URL、用户和会话身份，发送前后检查连接，不自动重定向或重试写请求。时间和语言页现已接入远程快照、目标/身份、远程时区枚举、差异预览、授权并应用、按原 planId 查询及恢复原时区；不再用客户端 `TimeZoneInfo.Local` 冒充宿主值。宿主编辑不触发 Workspace 防抖保存。

授权通过既有 `/privileged/elevation` 和本地渲染的宿主密码对话框；有效的精确资源授权可复用且不延长到期时间。连接切换清除草稿、计划和旧请求结果；窗口关闭不影响 Server 已持久化的操作。三语言按钮与操作状态已接入，但完整错误映射、页面离开确认、恢复记录列表、布局/键盘截图及远程实机验收仍待完成，不能将构建通过视为完整时区交付。
