# Android 界面与流程专项审查

按本次用户要求建立专项进度追踪，集中维护在此；当前产品规则仍以 `design/` 与 `features/` 为准。

## 审查流程与验收

每个界面依次完成：现有功能与入口清单 → 主要用户任务与状态流 → 正常及错误路径审阅 → 调整实现 → 相关回归检查 → 设备视觉与真实业务验收。修改前后核对功能，不以删功能、跳过认证或隐藏错误减少步骤。

状态为待审查、审查中、已实现待验证、已验收、受阻。编译通过不等于设备验收；主入口清单还需逐页补充编辑器、子页和对话框，文件数量不等于功能覆盖率。

- 信息层级：一处主要操作，列表使用行，卡片承载独立内容；避免嵌套卡片与重复标题；按需展开说明，完整信息仍可访问。
- 流程：复用同一次操作已验证的结果；认证、保存与提权分别处理，切换身份不会复用另一身份的凭据。
- 恢复：空输入、非法参数、认证拒绝、授权取消/失效、主机密钥变化、超时/断网、重复点击、迟到响应、旋转、后台和进程回收均有下一步。提交结果未知时先核实，不盲目重放写操作。
- 布局：小屏、大字体、键盘、长地址、平板和横屏；中/英/日、浅深色、对比度、TalkBack与点击区域；重要信息不能因截断永久丢失。
- 验证：按行为风险补充回归测试，视觉和远端业务按照 [验证矩阵](Verification.md) 验收。

参考 [Material 分组列表](https://github.com/material-components/material-components-android/blob/master/docs/components/List.md)、[Material 自适应布局](https://m3.material.io/foundations/layout/canonical-examples/overview) 和 [Apple 按需展示](https://developer.apple.com/design/human-interface-guidelines/disclosure-controls)。结合现有 Android Compose、导航、安全及图标体系采用这些原则。

## 首批：服务器中心

确认的问题：管理表单的提交门控忽略协调器中已验证的会话密码；无安全锁屏设备因此再次输入。

已实现：会话凭据允许留空提交并显示复用提示，手动密码优先；认证拒绝后清除会话副本，保留保险箱记录；连接及保存授权期间禁止重复提交与切主机；取消验证恢复状态。主机列表采用组外标题与分组行，管理目标以只读地址行替代三个禁用输入框，加入键盘避让与整行保存选项。主机信任、保存、忘记密码、删除主机与工作区入口均保留。

| 验收场景 | 预期 |
| --- | --- |
| 无锁屏，添加验证成功后点管理 | 留空可连接，不重复输入，不保存明文 |
| 已有会话或保存凭据，手动输入新密码 | 优先使用新输入 |
| 会话密码已被主机拒绝 | 清除副本，允许手动更正，不自动删除保存记录 |
| 网络失败后重试 | 保留已验证副本，原因不误报成密码错误 |
| 首次或变更主机密钥 | 显式确认后续接原操作，取消可重新开始 |
| 取消解封或保存授权 | 表单恢复可操作；取消保存仍可进入已认证工作区 |
| 验证/保存期间连续提交及切主机 | 同时只有一条连接与授权流程 |
| 关闭服务器中心或进程重启 | 无安全保存时重新认证，不沿用旧会话 |
| 三语、大字体、键盘、长地址 | 操作可滚动到，内容可读，单一复选框语义 |

`ServerCenterSubmissionTest.kt` 覆盖会话复用、不可用凭据后的手动更正、可用保存凭据、忙碌状态与缺少目标。实际构建与测试结果见下方验证记录。

## 总清单

优先顺序：连接与认证 → SSH 工作区 → 首页与应用导航 → 文件/编辑器/终端 → 管理应用 → 设置和帮助。下表登记现有 Screen / Workspace 主入口；待审查项不能计为已审阅。

已登记 43 个主入口；本批调整 1 个，设备验收 0 个。

| 界面入口 | 状态 |
| --- | --- |
| connect/ConnectionListScreen.kt | 待审查 |
| connect/LoginScreen.kt | 待审查 |
| connect/OwnerDevicePairingScreen.kt | 待审查 |
| files/FilesScreen.kt | 待审查 |
| home/HomeScreen.kt | 待审查 |
| manage/certificates/CertificatesScreen.kt | 待审查 |
| manage/deployments/DeploymentsScreen.kt | 待审查 |
| manage/docker/DockerControlScreen.kt | 待审查 |
| manage/docker/DockerResourceScreen.kt | 待审查 |
| manage/docker/DockerScreen.kt | 待审查 |
| manage/docker/DockerWorkspace.kt | 待审查 |
| manage/firewall/FirewallScreen.kt | 待审查 |
| manage/git/GitScreen.kt | 待审查 |
| manage/guardian/GuardianScreen.kt | 待审查 |
| manage/ManageScreen.kt | 待审查 |
| manage/monitor/MonitorScreen.kt | 待审查 |
| manage/operations/OperationsScreen.kt | 待审查 |
| manage/processes/ProcessesScreen.kt | 待审查 |
| manage/proxy/ProxyScreen.kt | 待审查 |
| manage/scripts/ScriptsScreen.kt | 待审查 |
| manage/smb/SmbScreen.kt | 待审查 |
| manage/TaskManagerWorkspace.kt | 待审查 |
| manage/tunnels/TunnelsScreen.kt | 待审查 |
| manage/websites/WebsitesScreen.kt | 待审查 |
| more/AboutScreen.kt | 待审查 |
| more/AccountSecurityScreen.kt | 待审查 |
| more/AppearanceScreen.kt | 待审查 |
| more/ConnectionsScreen.kt | 待审查 |
| more/DiagnosticsScreen.kt | 待审查 |
| more/HelpScreen.kt | 待审查 |
| more/HostSettingsScreen.kt | 待审查 |
| more/MobileApplicationsScreen.kt | 待审查 |
| more/MoreScreen.kt | 待审查 |
| more/OutboundProxyScreen.kt | 待审查 |
| more/ServerInformationScreen.kt | 待审查 |
| servercenter/ServerCenterScreen.kt | 已实现待设备验收 |
| servercenter/ServerMaintenanceScreen.kt | 待审查 |
| servercenter/SshFilesScreen.kt | 待审查 |
| servercenter/SshForwardsScreen.kt | 待审查 |
| servercenter/SshSystemScreen.kt | 待审查 |
| servercenter/SshTerminalScreen.kt | 待审查 |
| servercenter/SshWorkspaceScreen.kt | 待审查 |
| terminal/ServerTerminalScreen.kt | 待审查 |

## 验证记录

当前没有 adb 连接设备，真机视觉、三语、大字体与远端 SSH 场景待验收。

本批 Debug APK 构建与 `testDebugUnitTest` 通过：1116 项测试，0 失败、0 错误，其中新增服务器中心提交门控回归 4 项。测试使用本机现有 SDK/JDK/Gradle 和离线依赖；设备与远端验收未执行，不能据此标为已验收。

