# Android 当前实现与验证状态

> 更新：2026-09-30。本文件维护当前事实与已有验证证据；详细行为见 [文档目录](../README.md)，未关闭测试见 [验收清单](Verification.md)，未实现功能见 [部署后续工作](../plans/Deployment.md) 与 [内置应用补齐计划](../plans/BuiltInParity.md)。
>
> 已移除重复修复流水账、旧环境路径、过时“待实现”步骤和已完成目标。历史完整记录可查 Git；本轮文档整理没有执行构建或产品测试，也没有把未验证项目标成通过。

## 1. 当前功能

| 领域 | 已实现范围 | 仍缺代码的部分 |
| --- | --- | --- |
| 基础 Shell | Kotlin/Compose/Material 3，Compact/Medium/Expanded 导航、五个顶级目的地、三语/多主题、语言设置、监控/进程与服务器信息 | 逐页面平板分栏及桌面差异按 BP 计划核对；Shell 断点不代表全部页面已分栏 |
| 登录与本机安全 | 稳定 serviceId/账号键、密码与保险箱登录、两种独立保险箱、指纹/设备锁、token 刷新与注销、切换登录、动态系统徽标、debug 无锁屏明文兜底 | 设备密钥找回等尚未接入路径按独立协议设计 |
| Windows 10/11 设备密钥 | 现有配对载荷、扫码/图片/粘贴确认、P-256 Keystore、nonce 签名登录 | 不宣称覆盖 Windows Server 或所有设备找回流程 |
| 文件 | 浏览/详情/目录与文件操作、路径浏览器、流式落盘下载、缩略图/图片缓存、有界分块上传与续传、应用级进度/通知 | 普通文本编辑及完整图片/文件操作差异见 BP11/BP12 |
| 服务器中心 / AD01 | 宿主资料、SSH 主机密钥固定、SSH/SFTP/终端、隧道与稳定受管登录、部署选项、固定启动器回执读取 | 可信首次安装执行、发布资产/校验器接入、完整维护 UI；安装按钮仍不可用 |
| 应用部署 / AD02 | 四类来源、七步向导、流式归档暂存/服务器引用、日志、生命周期、修订与回滚、原操作恢复 | 新安装依赖不由登录隐式安装 |
| 模板目录 / AD03 | 可信内置目录、动态受限字段、兼容阻断、精确版本安装、实例/修订版本关联 | M4 更新说明/差异/显式版本更新（BP16）；M3 是验收任务 |
| Docker/Compose / AD04 | 资源浏览、生命周期、日志、受限导入、definitionVersion 预览、持久 Stack 操作、部分失败/重启核实、卷保护 | 完整引擎/资源创建编辑与代理集成见 BP02/BP09 |
| 网站 / AD05 | 只读诊断、确认式 HTTPS 发布、权威站点关联、持久操作恢复、带观察位置/时间的 DNS/TLS/HTTP 结果 | 独立 Nginx/证书全生命周期（BP03/BP04）；DNS/内网集成 M4 |
| Git / AD06 | 注册既有仓库、受限 UTF-8 编辑/差异/条件保存/单文件提交/推送；引用/固定 SHA、受限 Ubuntu BuildKit 任务、镜像发布关联 | 基础分支/暂存/历史/冲突补齐 BP10/BP11；M4 模板扩展与安全产物回收 |
| 终端/脚本/守护 / AD07 | Server Hub 会话/恢复/扩展键、固定活动屏幕与有界历史、200ms 稳定期后的串行尺寸同步、独立 SSH 终端、持久结构化脚本任务、Agent 工作负载管理 | 平板会话双栏 BP13；完整桌面字段/动作按 BP14 核对 |
| 任务/告警/恢复 / AD08 | 按领域 ID 观察、账号隔离无秘密索引、取消与本机隐藏、诊断导出、前台通知策略、备份创建/清单/只读预检 | Android 恢复提交、卷/数据库适配器、跨安装秘密、事件完整接入与可靠后台通知 |

服务端已有无卷/无秘密定义恢复为新停机实例的路径，Android 未接入提交 UI。定义备份已验证不代表数据卷/数据库可以恢复。

## 2. 已有验证证据

下表压缩自整理前的执行记录，日期与范围保留。构建产物/报告是历史位置，可能已被后续构建覆盖；最新测试总数只描述当次套件，不代表所有领域端到端通过。

| 日期 | 检查与结果 | 证明范围 / 限制 |
| --- | --- | --- |
| 2026-09-30 | 远端提交 `382196f2` 记录离线 `:app:assembleDebug :app:testDebugUnitTest --offline --no-daemon` 成功；61 类 / 522 JVM 用例，0 失败/错误/跳过；`git diff --check` 通过 | 固定活动屏幕/历史、Windows 清屏与光标重绘、缩放与跨帧解析；包含 14 个屏幕回归和 1 个控制器用例，以及键盘动画期间旧 Windows 重绘、尺寸合并/回到原尺寸、会话切换/附加期间变更。尺寸同步的 6 项场景在旧实现失败、修复后通过；真实手机 IME 与远端 Windows PowerShell 联调仍待验证。本轮合并未重新执行产品构建或测试 |
| 2026-09-30 | `:app:assembleDebug :app:testDebugUnitTest :app:assembleDebugAndroidTest --offline --no-daemon` 成功；61 类 / 500 JVM 用例，0 失败/错误/跳过；中英日各 1024 键一致无重复 | 当前登录切换、徽标查询竞态、认证和终端等逻辑；未执行本轮实体设备验收 |
| 2026-09-30 | API 35 平板模拟器 `emulator-5558` 直接 instrumentation：`TerminalKeyboardLayoutTest` 8 项通过 | 实际终端控件、真实系统 IME、手机/平板视口、已消费边距、大字体、草稿；截图 `app/build/reports/terminal-layout/terminal-real-keyboard.png`；不代表厂商 IME/实体设备/真实 PTY |
| 2026-09-30 | 终端连接恢复对应 61 类 / 492 JVM 用例通过，新增控制器与认证用例 | 首次附加、空列表/丢失会话、401/403/503、临期与并发刷新、输入互斥、后台返回与清理；真实 token 到期联调未执行 |
| 2026-09-29 | Android 57 类 / 462 用例通过；Server/Server.Tests 隔离构建成功；备份定向检查通过 | 密钥缺失、篡改、幂等、配额、每应用保留、重启残留清理；Server 3 条既有 CA1416 警告；不是数据恢复演练 |
| 2026-09-29 | Guardian Agent 构建通过（0 警告）；Server 构建通过；终端缓冲 JVM 用例通过 | 分块 UTF-8、VT 重绘、清屏/有界行保留；真实 PTY/SSH/Agent 权限矩阵未执行 |
| 2026-09-29 | Git 代码包含在 Android Debug 构建与全量 JVM 462 用例中，三语键集检查通过 | 尚无 Git 构建专项 Android 测试类，也未配置/验证真实 rootless BuildKit |
| 2026-09-29 | 任务/告警：Android 54 类 / 455 JVM 用例、Server 构建与 `--stack-operations-only` 通过 | 操作索引、回执查询、告警规则、诊断报告；系统文件选择器/通知/真实 SSH 未验收 |
| 2026-09-28 | owner-device keys：`:app:compileDebugKotlin :app:testDebugUnitTest --rerun-tasks` 成功 | Android 编译和 JVM；真实 Windows 10/11 与设备配对/签名/撤销未验收 |
| 2026-09-27 | 应用部署聚焦 `ApplicationDeploymentWireTest`、`DeploymentHttpTest`、`DeploymentBrowserTest`；全量 393 JVM 用例通过 | 四来源、暂存、秘密、生命周期、回滚/保卷和幂等；真实 Docker/SAF 大包/设备未验收 |
| 2026-09-27 | 模板目录：Android 401 JVM 用例通过；Server `--deployment-progress-only` 通过 | 四模板契约、受信任来源、schema/字段/能力/资源阻断；不启动 Docker、不创建真实模板实例 |
| 2026-09-27 | Compose：Server 完整套件、`--stack-operations-only`、`--stack-live-only` 通过；桌面构建通过；Android 42 类 / 406 JVM 用例通过 | 真实 Docker 29.8.0 / Compose v5.5.1：部分失败→成功更新、同键原操作回放、运行/停止引用卷拒删、删 Stack 保卷；认证 HTTP、故障注入、设备仍有缺项 |
| 2026-09-27 | 网站：Android 44 类 / 409 JVM 用例及后续 Debug 构建、Server 隔离构建通过 | 网站 wire、站点/证书/配置检查及发布代码；真实 Nginx/ACME/DNS/外网未验收 |
| 2026-09-24 | 普通上传：续传日志、源暂存、协调器 43 项检查（含当次全量 275 项）通过 | 权威偏移、重试预算、源变化、缓存/取消/身份隔离；SAF 与系统后台行为待设备验证 |
| 2026-09-24 | 缩略图：Server 22 项 `PASS THUMBNAILS` 与全量后端检查通过；Android 24 类 / 232 JVM 用例通过 | 传输/授权、缓存键、降采样计算；BitmapFactory/EXIF/真实过渡和 org.json null 设备路径未覆盖 |
| 2026-09-27 | `:app:lintDebug` 未通过，记录 5 错误 / 75 warning | MediaStore、contentLengthLong、Keystore API、Context→Activity；之后无完整通过证据，保留待核实 |

离线 `connectedDebugAndroidTest` 曾因缺 UTP `gradle-work-action:32.4.1` 缓存无法调度，终端布局改用已编译 APK 与 `adb shell am instrument` 执行；该模拟器实例测试后关闭，未操作实体设备。

## 3. 当前限制与下一步

- 现有能力表不等于全手机首次安装到公网访问闭环通过；首次安装还缺实现，其他领域的真实宿主/设备检查见集中验收清单。
- Server 终端是有界常用 VT 文本实现，复杂全屏程序、CJK 单元格和 IME 组合仍须设备核对；平板会话双栏尚未实现。
- 登录密码被服务端拒绝不会自动删除；管理员提权凭据被明确拒绝会删除，网络/5xx 无结论保留。弱生物识别设备只能通过设备锁解封连接凭据，不能保存提权密码。debug 无锁屏兜底明文且标注未加密，release 不提供。
- 原登录“已知系统不再查询”、没有 SignalR、终端/Docker/守护待实现、创建备份按钮未开放等描述均已被当前实现取代，不保留旧结论。
- 已完成修复不再追加流水账；发生行为变化时修改对应规范和本表，只新增真实验证证据或未关闭事项。构建方式与图标同步见 [开发发布](../development/android-release.md)。
