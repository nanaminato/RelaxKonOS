# 设置与应用管理（BP22）

“更多 → 宿主设置”提供环境变量、时区和主机名；“更多 → 应用”提供当前 Android 客户端版本、系统应用设置与可用原生功能。既有账户/连接/外观/诊断页继续使用。桌面窗口、Shell 包和外部 .roapp 权限不复制到手机。

## 宿主设置

| 项目 | 当前行为 |
| --- | --- |
| 时区 | 读取远端当前 ID、完整支持 ID 列表、内容 revision、provider 与观察时间。输入精确 ID，最多 12 个筛选建议；只能预览当前远端支持的 ID。没有设置宿主时钟或同步手机时区的按钮。 |
| 主机名 | 展示生效名称与待生效名称，按远端上报的长度限制校验单个标签。Windows 的更名可能等待宿主重启；页面展示其生效规则，不自动请求重启。 |
| 环境变量 | Linux 仅 HostMachine；Windows 支持 HostMachine 与当前认证用户映射的 HostUser。先查 target，不从手机猜 SID/UID。默认读取所有值为遮蔽；明确揭示另需授权。显示名称、原始/展开值、警告、观察时间及作用域。最多显示 100 项；其余可输入精确名称修改。 |
| 环境编辑 | 一次预览一个变量；Set 的空字符串与 Delete 分开。Windows 可选 ExpandString，Linux 仅 String。名称、Unicode、值长度、UTF-8 预算与高影响名称按共享规则门控；高影响变更需明确确认。替换完整值，不执行 shell 展开，不进行隐式 PATH 合并。 |

预览提交当前 revision 与一次性 idempotencyKey；服务端生成带精确目标、差异、生效时间、所需 capability、authorizationTarget 和五分钟期限的计划。环境变量预览仅显示配置存在性，不显示值。用户确认后仅发送原 planId；Server 在授权和内容 revision 再校验后执行。回滚先读取原操作，核对 observedRevision，并使用原 ID/版本；外部变更不被覆盖。应用和回滚均沿用当前精确目标授权，只在明确的授权拒绝后重试原计划一次，不对传输失败自动重放。

Settings 错误响应现在包含稳定 problemCode 扩展；Android 不解析 title 作为契约代码。HTTP 枚举按生产共享选项使用 camelCase；Android 不加入 PascalCase 或旧字段回退。

## 操作恢复与页面生命周期

应用前在 noBackupFilesDir 持久保存 serviceId/account、kind、原 planId、target、期限与 unresolved 标记。不保存环境值、差异内容、密码或令牌；最多 100 个跨登录引用，页面展示最近 20 项。相同宿主/账号再次登录可查询原操作；别的账号不显示该引用。

回执丢失、取消、离页、进程回收不清除原提交标记。存在未确定操作时，新的宿主设置变更被阻断。查询校验 ID、target 和 settingId；仅 Applied/Failed/RolledBack 的权威终态解除门禁，Unknown/RecoveryRequired/PartiallyApplied 保持阻断。没有自动修复或假成功按钮。Server 在跨进程锁内将过期 Prepared 计划持久转为 Failed/settings.plan_expired；延迟 apply 随后只能读取 Failed，不会写宿主。手机时钟不能单独证明计划已经过期或解除门禁。

页面仅在 RESUMED 发起读取；离页/后台取消请求、清除揭示值和草稿，旧登录回执不更新新页面。初次普通读取不弹环境授权；用户点击“授权读取/揭示”后才请求。预览和回滚确认面板有固定按钮与有界滚动，手机/平板共用内容。返回丢弃未提交草稿需确认；当前实现不提供跨进程草稿恢复。

## 应用管理范围

当前客户端显示真实 BuildConfig 版本与 Android packageName。系统设置入口仅发送固定 ACTION_APPLICATION_DETAILS_SETTINGS + 当前应用包；Android 决定权限、存储清除和卸载。清除 Android 存储会丢失连接与凭据，不删除远端宿主或桌面包数据。页面不自行卸载应用或清空保险箱。

内置功能随签名客户端更新，没有独立包版本/权限决策/卸载。原生目录仅链接已交付功能，按当前 Server capability 决定可用项，并在点击时复核原登录；capability 不等于授权。远端容器/修订/运行时仍在各领域页面管理。外部 .roapp 的安装、权限、私有数据、更新与移除由目标桌面处理，当前 Android 没有第三方运行时或其 app-settings 数据目录；详见 [移动包方案](../design/ApplicationPackages.Design.md)。
