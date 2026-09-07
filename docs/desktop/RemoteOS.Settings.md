# RemoteOS 设置系统与设置应用

当前执行基线与完整验收矩阵见 [SettingsSystem.Goal](./RemoteOS.SettingsSystem.Goal.md)。G1 正在实施，以下只描述已有代码，不把规划中的宿主写入标为可用。

## 服务与真源

设置应用是入口，偏好读写服务可独立调用。Client 的 `Services/WorkspaceSettings/IWorkspaceSettingsService` 供 Settings、Shell、Explorer、SDK、编码设置及默认程序入口共同使用。`WorkspacePreferencesEditor` 管理冻结草稿、300ms 防抖、目标绑定及重试；窗口关闭不会取消保存，连接变化清理旧目标草稿。`PreferencesSync` 负责登录加载到 ShellSettings 和 DefaultAppRegistry。

Server `Settings/IWorkspaceSettingsService` 管理偏好验证和版本比较。真源为配置注册表 `Workspace\Desktop` 的 `(Default)` JSON 值；当前 SQLite 缓存延迟落盘，因此 UI 明示“服务端已接收，等待持久化”，不能声称已落盘。损坏的偏好返回错误并保留原数据。

`GET /api/v1.0/workspaces/{id}/preferences` 返回 `WorkspacePreferencesDto.revision`；PUT 使用同一 DTO，必须携带编辑基线 revision。缺失返回 428，冲突返回 409。不能先读取新 revision 再给旧草稿换版本强行保存。Workspace 归属由认证身份校验；AppSettings 仍仅存应用私有偏好，不能存 OS 配置。

## 当前页面与保存状态

当前保留系统、个性化、时间和语言、网络、应用、镜像源、默认应用、开发者八页及已有壁纸、主题、Shell 和包工具能力。保存状态支持中文、英文、日文；失败保留草稿并可重试，冲突需要重新加载和合并。完整冲突编辑体验、目录搜索、首页、账户、辅助功能和响应式导航仍按 Goal 推进，尚未验收。

## 范围与宿主权限

- ClientDevice：此客户端设备的布局、开发模式和辅助功能。
- Workspace：当前用户 Workspace 的主题、语言、默认应用及后续环境覆盖。
- AppPrivate：AppSettings 隔离的应用私有配置。
- HostUser：认证映射的远程 UID/SID，不能使用 Server 服务账户的用户环境。
- HostMachine：远程机器配置，不归某个 Workspace 所有。

允许设置远程环境变量、时区、主机名和受支持 DNS；旧“Settings 不触及宿主配置”“环境变量操作一律禁止”限制已废止。Server 保持非特权，通过现有 Helper 封闭操作、身份绑定、授权、审计、读回及恢复实现。环境配置数据不得注入 Helper 或特权子进程启动环境。DNS 写入前必须具备不依赖 Client/Server 存活的宿主恢复任务。

远程配置 provider 与全部宿主验收尚未完成；必须在明确指定的远程测试主机或隔离 VM 验证，不得在开发机实验后声称远程验收通过。
