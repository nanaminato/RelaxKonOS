# Android 测试进度与验收清单

> 更新：2026-09-30。统一维护测试进度、已有执行证据和未关闭检查，区分自动化、模拟器、真实宿主和实体设备；实现进度见 [Progress](Progress.md#2-bp-实现进度)。未执行测试不阻止下一步实现，未测结果不得写成通过。本轮新增验证范围按 BP 编号记录，不将历史验证覆盖到新代码。AD 表为真实宿主/设备检查，已有 JVM 或构建证据不代表整项通过；依赖尚未实现能力的项目先完成 [部署后续工作](../plans/Deployment.md)。

## 1. 共同设备与发布检查

最小矩阵：一台手机竖/横屏、约 8 英寸与约 11 英寸平板；逐项覆盖中/英/日、浅/深/高对比、大字体、TalkBack、分屏、旋转、后台返回与进程回收。布局目标按具体页面检查，不以 Shell 断点计算通过替代页面分栏验收。

| 范围 | 未关闭检查 |
| --- | --- |
| 登录与凭据 | 强指纹成功/取消/失败/锁定，新录指纹保留失效记录，忘记密码/删除仅作用于选中身份；弱生物识别/锁屏五分钟窗口及过期，提权按次强指纹与 token 变化，debug 无锁屏兜底在 release 不可用 |
| 切换与徽标 | 登录选择、更多和连接页切换账号，密码保留、实际指纹提示、系统切换后的徽标、长地址/长名称与三语视觉 |
| Windows owner-device keys | 真 Windows 10/11 配对、邀请码过期/取消、QR 扫描/图片/粘贴确认、nonce 签名、锁屏窗口、密钥失效与撤销；Windows Server 不走工作站授权 |
| 文件下载/预览 | MediaStore 下载目录与重名文案；缩略图→详细图、EXIF 方向、大图耗时、缓存预算/淘汰、显式授权才预览；Android org.json null 显示与 executionEligibility 解析 |
| Server 终端布局 | API 35 平板模拟器已验证 8 项 IME/边距/大字体/草稿检查；厂商输入法、实体手机/平板、真实 PTY 和中文组合/复杂 VT 仍需执行 |
| 发布 | Release 签名/渠道证书、HTTPS 网络策略、诊断脱敏、后台恢复与危险操作确认；按 [发布说明](../development/android-release.md) 执行 |
| Lint | 2026-09-27 记录 5 错误/75 warning，之后无完整成功证据；重新运行并核实/修复，不能当作已通过 |

## 2. 文件上传与跨端一致性

普通文件传输已经实现；下面是设备检查，不是新的实现计划。

### 2.1 大文件与恢复

| 场景 | 通过标准 |
| --- | --- |
| 3 GB 视频（本地存储，可寻址源） | 后台运行、锁屏后完成，通知进度与界面一致 |
| 3 GB 文件（云盘源，不可寻址） | 先"准备中"落盘，再上传；成功后缓存被删除 |
| 传到 40% 切 Wi-Fi → LTE | 自动续传，偏移不归零 |
| 传到 40% 强杀应用，重进 | 显示"可继续 40%"，继续后从权威偏移前进 |
| 传到 40% 点取消 | 通知消失、目标目录无残留、缓存被删除、服务端会话在 TTL 后消失 |
| 受保护目录 + 1 GB 文件 | 弹一次管理员授权，随后完成（验证 12 MiB 天花板消失） |
| 1 GB 文件传两遍（第二遍同名） | 覆盖语义与桌面端/单发一致 |
| 分片连续超时（弱网模拟） | 分片降级后仍能完成，界面不出现"假进度" |


### 2.2 跨端一致性

同一文件分别由 Android/桌面上传，最终大小与原文件哈希一致；偏移只以服务端结果为准。设备上的流式分派与 BitmapFactory 解码不能由 JVM stub 检查替代。

## 3. AD01：Android 服务器初始化

| ID | 场景 | 预期 |
| --- | --- | --- |
| AD01-T1 | 干净的受支持 Linux 主机，只使用手机 | 预检、安装、健康确认、登录全部完成 |
| AD01-T2 | 无 sudo 的 User Mode | 不触发系统安装或开放端口，不声称支持系统管理域 |
| AD01-T3 | 未信任/变化的 SSH 密钥、无效摘要或错误架构 | 写操作被阻断，原因可理解 |
| AD01-T4 | 上传断网；执行后断网；旋转与进程回收 | 可核实原操作，不重复安装、不误报成功 |
| AD01-T5 | 升级失败、修复、卸载保留数据 | 真实结果可查；卸载后仍可进入服务器中心 |
| AD01-T6 | 隧道端口变化或主机重装 | 同安装保留稳定身份；新安装不复用旧安装信任与登录绑定 |

## 4. AD02：Android 应用部署向导

| ID | 场景 | 预期 |
| --- | --- | --- |
| AD02-T1 | 镜像部署、应用停止/启动、查看日志 | 真实 Linux Engine 上成功，端口和健康结果与实际一致 |
| AD02-T2 | Java、.NET、Python 正反样例 | 合法样例运行；入口、平台、依赖错误可定位 |
| AD02-T3 | 重复点击、请求超时、断网和 App 回收 | 同一提交不重复建资源，可回到原操作 |
| AD02-T4 | 镜像不存在、仓库拒绝、磁盘不足、端口冲突、Engine 失联 | 失败阶段与下一步清晰，不用统一“网络错误”掩盖 |
| AD02-T5 | 新版不就绪、回滚、旧镜像缺失 | 不误激活失败版本，真实恢复结果可查，数据卷保留 |
| AD02-T6 | 大包、过期文件引用、切账号、错误归属 | 有界传输、失效可恢复、跨身份引用被拒绝 |

## 5. AD03：Android 模板应用库

| ID | 场景 | 预期 |
| --- | --- | --- |
| AD03-T1 | 选模板、填必填项、安装、打开服务 | 全程无需命令和配置文件编辑 |
| AD03-T2 | 架构/能力/资源不匹配、模板来源不可验证 | 提交前明确阻断并指出缺失条件 |
| AD03-T3 | 非法端口、缺秘密、不支持字段或 schema | 客户端提示且服务端独立拒绝 |
| AD03-T4 | 模板更新、撤回与安装期间目录变化 | 实例绑定的版本可追踪，不发生静默替换 |
| AD03-T5 | 每个首批模板更新、卸载保留数据、恢复 | 数据行为符合模板说明；升级失败可诊断 |

## 6. AD04：Android Docker 与 Compose 管理

原表的“已验收”仅指记录中的服务端真实 Compose 宿主层；认证 HTTP 往返和 Android 设备尚未执行。部分通过不升级为整项通过。

| ID | 场景 | 预期 | 状态 |
| --- | --- | --- | --- |
| AD04-T1 | 双服务应用部署与更新 | 服务状态准确，数据库数据保留 | **已验收**（真实宿主）：两服务项目部署后观察结果准确，更新同一项目得到 `succeeded`；命名卷在更新与删除项目后都仍在。数据保留靠的就是这条卷语义，未额外拉数据库镜像 |
| AD04-T2 | 不支持条目、缺变量、路径越界或危险挂载 | 执行前拒绝，不静默改写定义 | **已验收**（真实宿主 + 无 Docker）：`build`/特权/设备/外部资源/bind mount/Docker socket 与**未设置变量**都在准入阶段被拒（400），引擎未被调用 |
| AD04-T3 | 一项服务失败、端口冲突、Engine 失联 | 展示部分结果及可执行的恢复措施 | **部分验收**：一服务立刻退出时 `up` 仍 exit 0，分类为 `partialFailed` 并记录引擎实际观察结果（已验收）；端口冲突与引擎失联未注入 |
| AD04-T4 | 超时重试、App 回收、服务端重启 | 核实原 Stack 操作，不重复创建项目 | **部分验收**：同键同文档在真实宿主回放同一操作（已验收）；重启核对不重放由 `PASS DOCKER STACK` 覆盖；Android 设备矩阵未执行 |
| AD04-T5 | 删除已用卷、停止项目、卸载保留数据 | 引用检查和数据保留语义正确 | **已验收**（真实宿主）：运行中与已停止的容器都算引用并拒绝删除，删除项目保留命名卷，无引用后才释放成功 |
| AD04-T6 | AD02 或外部创建的容器 | 归属明确，不跨管理域覆盖定义 | 未验收 |

## 7. AD05：Android 网站发布与网络配置

| ID | 场景 | 预期 |
| --- | --- | --- |
| AD05-T1 | 已就绪应用、可控域名与可达宿主 | 手机完成 HTTPS 发布并实际打开 |
| AD05-T2 | DNS 错误/未传播、端口不通、挑战失败 | 展示具体失败与重查入口，不误报应用停止 |
| AD05-T3 | 已占用域名、共享证书、外部站点 | 不覆盖非本次资源，不误删共享依赖 |
| AD05-T4 | 配置测试/重载失败、手机断网 | 可核实原任务，旧配置保留或明确报告恢复失败 |
| AD05-T5 | 内外网观测结果不同 | 显示观察位置和时间，不把局部可达当全网可达 |
| AD05-T6 | User Mode 或缺少管理权限 | 不提交宿主修改，提供可行连接方式说明 |

## 8. AD06：Android Git 部署与轻量编辑

| ID | 场景 | 预期 |
| --- | --- | --- |
| AD06-T1 | 公共/私有仓库 Dockerfile 项目 | 手机选仓库到服务运行，记录精确提交和产物身份 |
| AD06-T2 | 分支移动、重复提交、断网及进程回收 | 构建输入不漂移，恢复原任务，不重复发布 |
| AD06-T3 | 构建失败、超时、磁盘不足、私有依赖拒绝 | 失败定位准确，旧应用保持原发布状态 |
| AD06-T4 | 越界上下文、秘密泄露、超配额构建 | 受限执行，日志和镜像产物不包含提供的秘密 |
| AD06-T5 | 并发编辑、脏工作区、提交/推送冲突 | 不静默覆盖或强制推送，给出可恢复路径 |
| AD06-T6 | 回滚至既有修订 | 使用原产物，不重新编译或声称撤销数据迁移 |

## 9. AD07：Android 终端、脚本与进程守护

| ID | 场景 | 预期 |
| --- | --- | --- |
| AD07-T1 | 手机软键盘、中文 IME、扩展键、粘贴及大输出 | 输入/光标正确、渲染有界、主操作可达 |
| AD07-T2 | 旋转、页面切换、断网、App 回收 | 可重附加服务端会话，不重复创建或执行 |
| AD07-T2c | Android 真机 IME 动画与远端 Windows PowerShell 清屏、光标定位和窗口重绘 | 不残留重复提示符或大块空白；尺寸变化稳定后只同步最终行列，缩小/恢复不误滚入历史；保留复杂全屏程序、颜色和 CJK 单元格宽度的能力限制 |
| AD07-T2a | access token 到期、401 协商、并发 REST 刷新、403 拒绝 | 更新令牌后恢复同一会话；一次刷新/重试；拒绝不无限重连 |
| AD07-T2b | 首次列表、关闭当前会话、快速重复新建、切换中输入、进程退出 | 自动选中存活会话；关闭有具体目标确认；操作串行且输入门控正确 |
| AD07-T3 | 一次性脚本成功、失败、取消与超时 | 退出码、结果和取消阶段可查 |
| AD07-T4 | 守护进程崩溃、重启与健康失败 | 策略在远端生效，手机只观察和提交操作 |
| AD07-T5 | User Mode、错误 run-as 或权限不足 | 不跨账号执行，不因客户端输入绕过服务端限制 |
| AD07-T6 | SSH 与 Server 终端互切、SSH 断线 | 身份清晰，准确说明任务是否仍存活 |

## 10. AD08：Android 运维与恢复中心

| ID | 场景 | 预期 |
| --- | --- | --- |
| AD08-T1 | 活动任务中离开页面、锁屏、旋转与回收进程 | 重开后定位同一领域操作并读取真实结果 |
| AD08-T2 | 断网、服务端重启、操作记录过期 | 结果未知与失败分开，不重复提交副作用 |
| AD08-T3 | 切账号、切服务器、隧道换端口、宿主重装 | 记录归属正确，不向其他身份展示任务或凭据 |
| AD08-T4 | 取消竞争、重复重试、最终结果迟到 | 以远端终态为准，取消按钮不伪造取消完成 |
| AD08-T5 | 拒绝通知权限、App 被终止 | 应用内可查；未实现可靠推送时不作即时到达承诺 |
| AD08-T6 | 活跃数据库备份、备份损坏、空间不足及密钥丢失 | 按一致性方法执行，明确失败，不生成虚假可恢复状态 |
| AD08-T7 | 新实例恢复及升级后数据格式不兼容 | 校验数据和版本，阻止不兼容覆盖，记录演练结果 |
| AD08-T8 | 重复故障、恢复、确认和敏感输出 | 不重复打扰，确认不关闭故障，通知无秘密 |

## 11. 已有验证证据

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

## 12. BP 测试进度

> BP24-M1 对应第一批测试追踪；本表于 2026-09-30 建立；BP01-M1 在提交 `89114399` 上完成静态核对、Debug 主代码与单元测试代码编译，24 个专项 JVM 用例通过，后续提交尚未复测该组用例。BP02-M1 在提交 `02b54f19` 上完成 Debug 主代码与单元测试代码编译，12 个专项 JVM 用例通过；设备/宿主验收仍未执行。第 11 节历史证据不自动覆盖尚未实现的新功能。

实现是否完成只在 Progress 更新；用例准备使用“未开始 / 进行中 / 已准备”，测试代码与场景清单作为准备证据，准备缺失不阻止下一项实现。各执行列按“未执行 / 进行中 / 部分通过 / 通过 / 失败 / 环境受限 / 不适用”记录每类测试状态。尚无功能代码不记为失败；没有尝试执行不记为环境受限。新增后续批次时沿用 BP 编号分行，不把测试完成设为下一实现步骤的前置条件。

| 关联实现 | 用例准备 | 自动化构建/测试 | 手机/平板交互 | Ubuntu 宿主 | Windows 宿主 | 重点范围 / 待补证据 |
| --- | --- | --- | --- | --- | --- | --- |
| BP01-M1 | 进行中 | 通过：合并后 Debug APK/全量 JVM 与 .NET 构建；专项范围见 BP03-BP05-MERGE-V1 | 未执行 | 未执行 | 未执行 | wire/HTTP/Repository/Journal 24 个用例已执行；取消竞争、完整认证刷新、设备 SAF 与 OperationCenter 端到端用例待补；执行证据 BP01-M1-V1 见下文，本次 APK/JVM 已验证；仪器测试/lint 未执行 |
| BP02-M1 | 进行中 | 通过：合并后 Debug APK/全量 JVM 与 .NET 构建；专项范围见 BP03-BP05-MERGE-V1 | 未执行 | 未执行 | 未执行 | wire/HTTP/Editor 12 个用例已执行；实际认证刷新、真实消费者网络、手机/平板交互待补；执行证据 BP02-M1-V1 见下文，本次 APK/JVM 已验证；仪器测试/lint 未执行 |
| BP03-M1 | 进行中 | 通过：合并后 Debug APK/全量 JVM 与 .NET 构建；专项范围见 BP03-BP05-MERGE-V1 | 未执行 | 未执行 | 未执行 | 已执行 wire/HTTP/Repository 12 个用例；全新 Ubuntu/Windows 安装、手机包/服务器引用、接管、生命周期/卸载、设备交互及断线/取消验收待执行 |
| BP03-M2 | 进行中 | 通过：合并后 Debug APK/全量 JVM 与 .NET 构建；专项范围见 BP03-BP05-MERGE-V1 | 未执行 | 未执行 | 未执行 | 已执行 wire/HTTP/Draft/Repository 18 个 Android 用例；静态/反代、配置错误、端口冲突、并发更新与断线核实及真机交互待执行；执行证据见下文 |
| BP04-M1 | 进行中 | 通过：合并后 Debug APK/全量 JVM 与 .NET 构建；专项范围见 BP03-BP05-MERGE-V1 | 未执行 | 未执行 | 未执行 | 已执行 wire/HTTP/Draft/Repository/Journal 21 个 Android 用例；真实预检/ACME/自签名/续期/撤销/删除/取消与秘密脱敏、设备交互待执行 |
| BP04-M2 | 进行中 | 通过：合并后 Debug APK/全量 JVM 与 .NET 构建；专项范围见 BP03-BP05-MERGE-V1 | 未执行 | 未执行 | 未执行 | 新增 12 个 Android 用例；真实 Nginx/SNI/信任及管理连接中断恢复待验证 |
| BP05-M1 | 已准备 | 通过：合并后 Debug APK/全量 JVM 与 .NET 构建；专项范围见 BP03-BP05-MERGE-V1 | 未执行 | 未执行 | 未执行 | 新增 21 个 Android 用例；真实 FRP 安装/连接、Windows Helper、手机/平板/IME 与断线恢复待验收 |
| BP05-M2 | 已准备 | 通过：合并后 Debug APK/全量 JVM 与 .NET 构建；专项范围见 BP03-BP05-MERGE-V1 | 未执行 | 未执行 | 未执行 | 新增 18 个 Android 用例；真实 frps 网络、Windows Helper、秘密/会话/手机/平板/IME 待验收 |
| BP06-M1 | 进行中 | 通过：Debug APK、94 类/669 个全量 JVM、Server 构建与配置激活/回滚专项 | 未执行 | 未执行 | 未执行 | 新增 wire/HTTP/Repository 9 例；真实 Mihomo 安装/修复/升级/回滚、订阅联网、设备与故障诊断待验收；证据 BP06-M1-V1 |
| BP06-M2 | 进行中 | 通过：Debug APK 与全量 JVM；新增 7 例 | 未执行 | 未执行 | 未执行 | 完整 TUN/Windows 字段保留、有界日志、原键紧急恢复保留先前待核实提交、控制器事实门控与 HTTP TUN 提交通过；真实管理路径/设备/DNS/GeoData 待验收 |
| BP02-M2 | 已准备并执行 3 例 | JVM/APK 及 Server 本地 HTTP 消费通过 | 未执行 | 未执行 | 未执行 | 受管缺失/停止/地址变更、四消费范围、真实 Docker/外网与设备待验收 |
| BP09-M2 | 已执行 18 例 | JVM/APK 全量 754 例、Server 资源 HTTP/失败读取/日志及桌面编译通过 | 未执行 | 未执行 | 未执行 | 实际 Docker CRUD/引用/受管归属、stdout/stderr 日志、断线/并发、手机平板/IME/大字体 |
| BP09-M1 | 已执行 17 例 | JVM/APK 全量 736 例、Server 引擎和镜像源 HTTP 专项通过 | 未执行 | 未执行 | 未执行 | Linux 安装/Helper 与全容器影响、Windows Desktop CLI、真实镜像源消费、断线/并发、手机平板/IME/大字体 |
| BP08-M1 | 已执行 20 例 | JVM/APK 全量 719 例及 Server SMB 专项通过 | 未执行 | 未执行 | 未执行 | Samba 包/服务/凭据、共享访问、Windows ACL/安全/漂移、断线/并发、手机平板/IME/大字体 |
| BP07-M1 | 已准备 11 例 | JVM/APK 11 例及 Server 只读专项通过 | 未执行 | 未执行 | 未执行 | UFW 实际启停/规则/授权、管理路径中断、并发编号、手机平板/IME/大字体 |
| BP10 | 已执行 20 例 | 全量 JVM 784 例/Debug APK、Server/Helper 编译与 Git 89 项专项、桌面编译通过；证据 BP10-V1 | 未执行 | 未执行 | 未执行 | Git 安装/真实 Helper 身份、真实私有远端/并发/断网/原键、手机平板/旋转/IME/大字体/三语视觉 |
| BP11 | 已执行 10 例 | 全量 JVM 764 例/Debug APK、Server/Helper 编译与文本专项、桌面编译通过；证据 BP11-V1 | 未执行 | 未执行 | 未执行 | 编码/真实 Helper 身份、外部并发/断网、手机平板/旋转/IME/大字体/三语视觉 |
| BP12 | 已执行 21 例 | 全量 JVM 805 例/Debug APK、三语/链接/差异静态检查通过；证据 BP12-V1 | 未执行 | 未执行 | 未执行 | 真机/平板多选/剪贴板/属性与图片缩放旋转；真实 Linux/Windows 权限、部分目录错误、断网/停止/并发、真实授权与进程回收 |
| BP17-M1 | 已准备并执行 9 例 | JVM/APK 通过，原任务/归属/能力/告警专项通过 | 未执行 | 未执行 | 未执行 | 新领域观察/取消/修复入口、离页/旋转/重连、三语与归属 |

用例准备列只追踪该 BP 新增测试代码和可执行场景，不把本文的场景概述当作已准备；自动化列分别注明编译/构建和测试结果；两者结论不同时写“部分通过”并列出范围。设备列细分手机/平板、IME/大字体和三语；宿主列细分版本/模式/服务能力与联网条件，宿主明确不支持的动作可写“不适用”并说明依据。新增证据按编号写日期、提交、命令/人工步骤、环境、结果和未覆盖项，再更新对应单元格；BP01-M1 的静态检查证据见下文，不代替 Android 执行结果。

### BP01-M1 静态证据与待补范围（2026-09-30）

- `InstallationWireTest`（6）、`InstallationHttpTest`（4）、`InstallationRequestJournalTest`（4）、`InstallationRepositoryTest`（10），共 24 个用例。覆盖当前枚举/路由/包字段、异常 wire、限时引用、专用 multipart/128 MiB、明确提权、稳定提交/取消键、响应丢失后的显式重试、原 ID 只读恢复、账户/提示期间切换、损坏日志 fail-closed 等；初次交付仅准备用例，后续执行结果见 BP01-M1-V1。
- `git diff --check` 通过；Python XML 解析检查通过三语文件有效、无重复资源键、键集一致、新增运维页面资源引用存在。该检查不执行 Compose 或 Android 资源编译。
- 初次交付时，.NET 10.0.400 临时控制台直接编入共享 `InstallationContracts.cs`、`RelaxKonOSJsonOptions.cs` 与 `RelaxKonOSEndpoints.cs`；`DOTNET_CLI_HOME=/tmp/relaxkon-dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --project /tmp/relaxkon-installation-wire/Wire.csproj --verbosity quiet` 通过：确认 `service=nginx`、`stage=updatingPackageLists`、camelCase 字段与 `/api/v1.0/installations/Nginx/Install` 路由。临时项目不属于发布产物；检查仅验证共享序列化约定，不是 Android/Server 集成测试。
- 初次交付按用户要求跳过 Android 编译/测试；后续用户明确授权执行 24 个 JVM 用例，暂不做设备测试。该次没有执行 APK 打包、仪器测试、lint、设备交互或真实 Ubuntu/Windows 安装、上传、提权、取消与重启恢复；第 11 节既有 lint 未通过记录仍待核实。
- 待补可执行检查：401 刷新与提权两次授权组合、同意提权后的拒绝、取消阶段切换/资源竞争、服务响应丢失后任务已结束、索引写失败/进程杀死、隐蔽记录与活动发现、User Mode 仅 Git 能力、SAF 未知长度/源重新打开/断网、三语/IME/大字体/手机平板、真实六类服务支持矩阵。当前 API 没有按幂等键只读查询，完全丢失首个终态响应的“结果待核实”必须保留，不能用空活动列表替代终态。

### BP01-M1-V1 初步 JVM 执行证据（2026-09-30）

- 被测提交：`89114399`；执行前工作树干净，该次仅更新验证文档，没有修改实现或测试代码。本条记录恢复合入 `0a59bb52` 的文档，执行结果不扩展为该提交或 BP02-M1 的验证证据。
- 环境：Windows 11 amd64；本机 Gradle 9.7.1、缓存的 JetBrains JDK 25、Android SDK platform 36；AGP 9.4.1。使用 `--offline`，未下载依赖。
- 在 `Client/RelaxKonOS.Client.Android/` 执行以下 PowerShell 命令；`:app:compileDebugKotlin`、`:app:compileDebugUnitTestKotlin` 与 `:app:testDebugUnitTest` 成功，Gradle 返回 `BUILD SUCCESSFUL`（22 秒）。这证明被测提交的 Debug 主代码、资源和 JVM 用例可以编译并执行，不代表 APK 打包或设备运行通过。

```powershell
$bp01Gradle = 'C:/Users/betha/.gradle/wrapper/dists/gradle-9.7.1-all/6yde0y3ecw7psqwo4h66kup3z/gradle-9.7.1/bin/gradle.bat'
& $bp01Gradle :app:testDebugUnitTest `
  --tests 'app.relaxkonos.mobile.core.net.InstallationWireTest' `
  --tests 'app.relaxkonos.mobile.core.net.InstallationHttpTest' `
  --tests 'app.relaxkonos.mobile.data.InstallationRequestJournalTest' `
  --tests 'app.relaxkonos.mobile.data.InstallationRepositoryTest' `
  --offline --console=plain
```

| 测试类 | 用例 | 失败 / 错误 / 跳过 |
| --- | --- | --- |
| InstallationWireTest | 6 | 0 / 0 / 0 |
| InstallationHttpTest | 4 | 0 / 0 / 0 |
| InstallationRequestJournalTest | 4 | 0 / 0 / 0 |
| InstallationRepositoryTest | 10 | 0 / 0 / 0 |
| 合计 | 24 | 0 / 0 / 0 |

- Python XML 解析核对了 `app/build/test-results/testDebugUnitTest/TEST-*Installation*Test.xml` 的四份报告；本机 HTML 报告为 `app/build/reports/tests/testDebugUnitTest/index.html`。这些是忽略的本机构建产物，不提交到仓库；本次恢复记录时仍可从四份 XML 确认 24 个用例、零失败、零错误、零跳过。
- 在 `89114399` 上复查三语 XML：各 1,081 个字符串键，无重复且键集一致；运维页面 134 个 `R.string` 引用全部存在。静态比对共享契约：6 个服务、4 个动作、6 个状态、17 个阶段及其 camelCase wire 值一致，15 个共享安装问题码均有 Android 定义。后续新增代理资源的静态证据另见 BP02-M1。
- 编译仅报告 `OperationsScreen.kt:253` 的 Material 3 `TabRow` 弃用警告，无编译错误。已有缺口仍见上面的待补范围；该次未执行其他 JVM 用例、设备测试或宿主集成验收。

### BP02-M1 静态证据与待补范围（2026-09-30）

- `OutboundProxyWireTest`（4）、`OutboundProxyHttpTest`（2）、`OutboundProxyEditorTest`（6），共 12 个用例。覆盖 URL 凭据原样回读/提交、HTTPS 空值/NO_PROXY、独立范围、Desktop 上游、严格枚举/必需字段、当前 GET/PUT/DELETE 与认证头、明确拒绝/损坏响应、确认取消零写入、固定确认草稿、结果不明确先刷新、编辑放弃、确认期间切身份、旧响应隔离、离页秘密状态清理。初次交付仅准备用例，基本测试结果见 BP02-M1-V1；401 刷新/并发设置等可执行集成用例待补。
- `git diff --check` 通过。Python ElementTree 解析三语资源：各 1147 个唯一键、键集一致，新增代理页面/映射及设置/Docker 入口资源引用均存在。该检查不执行 Android 资源或 Compose 编译。
- 初次交付时，.NET 临时控制台直接编入共享 `DockerProxyContracts.cs`、`RelaxKonOSJsonOptions.cs`、`RelaxKonOSEndpoints.cs`；`DOTNET_CLI_HOME=/tmp/relaxkon-dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet run --project /tmp/relaxkon-proxy-wire/Wire.csproj --verbosity quiet` 通过。确认当前路由、十字段保存请求、camelCase 枚举/字段、确认字段，并用 Python 比较实际 .NET 状态 JSON 与 Android `PROXY_STATUS` 用例 fixture 完全一致（包括 Desktop 上游）。仅为共享契约格式核对，不是 Android 或服务端集成测试。
- 初次交付按用户要求跳过 Android 编译/测试；本次用户要求基本测试，沿用 JVM 验证范围执行。未执行 APK 打包、仪器测试、lint、手机/平板验收或真实 Ubuntu/Windows 宿主读写、Docker 重启、代理网络验收。
- 待宿主/设备验证：新设置与无 Docker daemon、引擎应用/替换/退役失败、Desktop 手动/系统上游差异、loopback 构建不可达、带认证代理、HTTPS 空值、NO_PROXY 域名/地址绕过、各范围独立消费、禁用/清除后的实际直连、401 刷新及拒绝、外部并发修改、断网写入后刷新核实、手机/平板三语/IME/大字体与离页/旋转。镜像查询/运行时下载只显示消费策略，当前 DTO 没有实际请求结果，必须以真实消费者请求验证，不能把选中范围写成联网通过。

### BP02-M1-V1 基本 JVM 执行证据（2026-09-30）

- 被测提交：`02b54f19`；执行前工作树干净。本次仅更新验证与状态文档，没有修改实现或测试代码，BP01-M1 的已有执行证据保留。
- 环境：Windows 11 amd64；本机 Gradle 9.7.1、缓存的 JetBrains JDK 25、Android SDK platform 36；AGP 9.4.1。使用 `--offline`，未下载依赖。
- 在 `Client/RelaxKonOS.Client.Android/` 执行以下 PowerShell 命令；`:app:compileDebugKotlin`、`:app:compileDebugUnitTestKotlin` 与 `:app:testDebugUnitTest` 成功，Gradle 返回 `BUILD SUCCESSFUL`（5 秒）。Debug 主代码、资源和单元测试代码编译通过，未进行 APK 打包或设备运行。

```powershell
$bp02Gradle = 'C:/Users/betha/.gradle/wrapper/dists/gradle-9.7.1-all/6yde0y3ecw7psqwo4h66kup3z/gradle-9.7.1/bin/gradle.bat'
& $bp02Gradle :app:testDebugUnitTest `
  --tests 'app.relaxkonos.mobile.core.net.OutboundProxyWireTest' `
  --tests 'app.relaxkonos.mobile.core.net.OutboundProxyHttpTest' `
  --tests 'app.relaxkonos.mobile.ui.more.OutboundProxyEditorTest' `
  --offline --console=plain
```

| 测试类 | 用例 | 失败 / 错误 / 跳过 |
| --- | --- | --- |
| OutboundProxyWireTest | 4 | 0 / 0 / 0 |
| OutboundProxyHttpTest | 2 | 0 / 0 / 0 |
| OutboundProxyEditorTest | 6 | 0 / 0 / 0 |
| 合计 | 12 | 0 / 0 / 0 |

- Python XML 解析核对了 `app/build/test-results/testDebugUnitTest/TEST-*OutboundProxy*Test.xml` 的三份报告；本机 HTML 报告为 `app/build/reports/tests/testDebugUnitTest/index.html`。这些是忽略的本机构建产物，不提交到仓库。本次报告只覆盖选择的 BP02-M1 用例，不是全量 JVM 回归。
- 复查三语 XML：各 1,147 个字符串键，无重复且键集一致；`OutboundProxy*.kt` 中 72 个 `R.string` 引用全部存在。静态比对 `DockerProxyContracts.cs`：2 个来源、2 个目标、5 个层状态及其 camelCase wire 值一致，保存请求的 10 个字段和 `/api/v1.0/docker/proxy` 路由一致。
- 基本用例未发现失败，无需修复实现。HTTP 用例调用本机测试服务器，Editor 用例使用 FakeGateway；不证明真实宿主的代理连通性、Docker 重启、401 刷新或手机/平板交互，待补范围继续保留。

## 13. 记录方式

实现交付只更新 Progress 的状态、动作、文件/提交与剩余代码；测试执行后在本文记录日期、提交、命令或人工步骤、环境、结果与检查范围，并关闭对应检查。未测平台、跳过和部分通过明确保留；无测试时仍可按实现依赖继续下一项。测试发现真实缺陷时关联 Progress 的代码待办，不将缺测试本身标为实现阻塞。

秘密、token、私钥与实际宿主密码不进入记录。AD/BP 编号用于关联领域与实现项，不要求保留已完成计划文件；测试证据不在 Progress 或计划中重复维护。


### BP03-M1 静态证据与未执行范围（2026-09-30）

- 代码核对：WebServerContracts/ApiRoutes 与现有 Server/桌面调用对照，接入当前 discovery/candidate/catalog/lifecycle/operation/cancel 路由；无旧路由或双格式兼容。ACME 路由动作是 `enableacmehttp01`，返回任务 kind 是 `enable-acme-http01`，分别映射。
- 静态检查：Python XML 解析与三语键集合/格式占位符核对、Kotlin `R.string` 引用扫描；`git diff --check`。结果通过。上述结果仅是源文件/资源检查，未验证 Kotlin/Compose 编译、HTTP 执行或实际安装。
- 用例准备：`WebServerManagementWireTest` 4 个（段编码、ACME action/kind、严格 ID/状态、能力字段及目录/候选）；`WebServerManagementHttpTest` 3 个（方法/路由/Bearer/幂等键/确认、原 ID 查询/取消、畸形响应与问题码）；`WebServerRepositoryTest` 5 个（未知结果原键/事实查询、响应归属、换会话、首次明确拒绝、未决动作锁）。总计 12 个，未执行。
- 自动化未执行：本机无 Android 环境，依用户授权跳过 Android 编译、单元测试和仪器测试；未安装 SDK/Gradle 或更改 Windows 本地 Gradle 分发设置。
- 设备待验收：手机列表/详情、平板 600 dp 分栏、IME/大字体/三语、SAF 上传与远端路径选择；离页停止观察、旋转/进程回收恢复、相同资料重新登录及切宿主隔离。
- Ubuntu 待验收：APT 新装、已有系统 Nginx 接管、配置备份/校验、能力允许的启停/重启/重载、ACME include、受管卸载、提权 Helper 不可用及权限拒绝。
- Windows 待验收：官方目录/指定版本、服务器 ZIP 引用/手机 ZIP 上传、限时引用过期、ZIP/版本不匹配、新装和已有实例接管、生命周期/卸载。
- 故障待验收：HTTP 401 刷新/提权后的原键复用、提交/取消响应丢失、已知 ID 查询与运维中心恢复、索引写入失败、任务取消与配置事实再核实。当前接管端点候选消失后可能无法原键重放，未知 ID 保留待核实，不按 404/候选消失判定成功。

### BP03-M2 执行证据与未执行范围（2026-09-30）

- 提交：当前工作区未提交改动。环境：Linux，本机 .NET SDK 10.0.400；没有 Android SDK，未安装 Android 环境。
- Server：`~/.dotnet/dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj --no-restore -v minimal` 通过，0 错误、3 条既有 CA1416 平台告警。Protocol 随依赖构建通过。
- 契约检查：`~/.dotnet/dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -p:UsePrebuiltServerAssembly=true -v minimal` 通过（0 告警/错误），随后 `~/.dotnet/dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --webserver-sites-only` 通过。验证创建/版本匹配/过期/删除后冲突、亚毫秒版本精度、必需删除版本及拒绝未知请求字段；未连接真实 Nginx，不代表整套 Server 测试通过。
- 桌面：`~/.dotnet/dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -v minimal` 未通过，停在 Framework 项目的 `ResolveFrameworkReferences/GetPackageDirectory`，没有进入桌面 C# 编译。调用方已同步，不声称桌面编译通过；未修改无关框架构建配置。
- Android 用例已准备但未执行：`WebServerSiteWireTest` 3 个、`WebServerSiteHttpTest` 4 个、`WebSiteDraftTest` 6 个、`WebSiteRepositoryTest` 5 个，共 18 个；包含完整字段/精确版本、DELETE 请求体、拒绝伪操作响应、编辑校验、未知结果/并发冲突/事实确认及账号隔离。
- 静态检查：三语 XML 可解析、键集一致、无重复键、Kotlin `R.string` 引用可解析、格式占位符一致、文档本地链接存在；`git diff --check` 通过。静态检查不代替 Kotlin 编译或行为测试。
- Android Gradle 编译、JVM/仪器测试和 lint 按用户要求跳过。手机/平板、IME/大字体、真实 Ubuntu/Windows Nginx、权限授予、TLS、端口冲突、配置失败恢复与并发/断线验收均未执行，继续保留待验收。

### BP04-M1 执行证据与未执行范围（2026-09-30）

- 提交：当前工作区未提交改动。环境：Linux，.NET SDK 10.0.400；本机没有 Android 环境，未安装 SDK/Gradle。
- `~/.dotnet/dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj --no-restore -v minimal` 通过，0 错误、3 条既有 CA1416 平台告警。Protocol 依赖构建通过。
- `~/.dotnet/dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -p:UsePrebuiltServerAssembly=true -v minimal` 通过，0 告警/错误；`~/.dotnet/dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --certificate-replay-only` 通过。实际执行文件账本，验证签发/自签名重放保留原证书与操作 ID、同账号同键不重复动作、账号/动作/目标隔离、重新打开账本后仍复用原记录。测试使用文件模式，不连接 SQLite、ACME、Nginx 或真实证书服务，不等同整套 Server 验收。
- 已准备 Android 用例但未执行：CertificateWireTest 4 个、CertificateHttpTest 4 个、CertificateDraftTest 5 个、CertificateRepositoryTest 6 个、CertificateRequestJournalTest 2 个，共 21 个；包括完整元数据/枚举、预检未知端口、真实路由/键/DELETE body、输入与 DNS 不可用、原键重试、摘要无正文、错误归属、先持久 ID 后索引、会话隔离、损坏/写入失败阻止提交。
- 静态检查：三语 XML/键集、重复键、Kotlin 字符串引用、新增占位符/转义、文档本地链接及 `git diff --check` 通过，不代替 Kotlin 编译或行为验证。
- Android 编译、JVM/仪器测试、lint、手机/平板、IME/大字体按用户要求未执行。Ubuntu/Windows 的真实 ACME、DNS、HTTP-01、自签名材料权限、关联部署、撤销/删除副作用、取消竞争、断线/重启恢复均未执行。Kestrel 与完整站点联动的后续执行证据见 BP04-M2。

### BP04-M2 执行证据与未执行范围（2026-09-30）

- Server `--no-restore` 构建通过：0 错误、3 条既有 CA1416；Server.Tests 使用 `UsePrebuiltServerAssembly=true` 构建通过：0 告警/错误。
- 实际执行 `--certificate-binding-only` 通过：真实 X509 材料、文件存储/账本、有效期边界、撤销状态、IDN/IP/单层通配符、精确 SNI 优先、实际运行时指纹/默认选择、元数据存在与监听配置区分、无 HTTPS 拒绝、配置监听后的部署、删除解除选择、事实变化后的原请求重放。使用测试 IServer 地址和权限服务，不启动真实 TLS 监听。生产 `HostPrivilegeService.IsAdministrator` 固定为 false，现有证书写入路径被拒绝；需服务端迁移至受授权 Helper，不能以 root/Admin 进程或模拟权限测试宣称生产写入可用。
- 重新执行 `--certificate-replay-only` 和 `--webserver-sites-only` 通过；未执行整套 Server.Tests、真实 ACME/Nginx/Windows/Ubuntu 监听或外网访问。
- 新增 12 个 Android 测试代码：SAN/有效期/指纹 4、选择缺失/未核实/域名变化 3、部署 wire 2、GET HTTP 1、丢失部署响应/错误目标 Repository 2。Android 编译、测试、lint 按用户要求跳过，不宣称通过。
- 三语 XML、键集合、Kotlin 资源引用、占位符/转义、Android 文档链接及 `git diff --check` 静态检查通过。桌面客户端只读 DTO 调用已同步；既有桌面框架引用解析阻碍仍未关闭。
- 尚待目标环境验证：站点证书共享/过期/缺失、真实 Nginx 配置拒绝与失败回退、管理主机名/SNI/default 切换、客户端信任和自签名握手、原管理连接中断后的原操作查询/原键重放、重启后的健康版本恢复、取消竞争、手机/平板与 IME/大字体。

### BP05-M1 执行证据与未执行范围（2026-09-30）

- `~/.dotnet/dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj --no-restore -v minimal` 通过，0 错误、3 个既有 CA1416 平台告警；`~/.dotnet/dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -p:UsePrebuiltServerAssembly=true -v minimal` 通过，0 告警/错误。
- `~/.dotnet/dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --frpc-state-only` 通过：revision/隧道集合/Token 变更、重启后未知指纹、禁用与停止投影、现有协议校验与 TOML 秘密边界。
- 同一测试程序集 `--frpc-lifecycle-only` 通过：本地 tar.gz/HTTP 夹具、实际 SQLite/Data Protection、shell frpc 子进程，覆盖可信安装、当前/上一版本、哈希/异常归档拒绝、应用/连接、编辑后尚未应用、重新应用、停止、脱敏日志、回滚、卸载与秘密生命周期。最后一次子进程事件归属修改后已重建并重跑以上专项检查。没有互联网下载、真实 frps、proxy 注册、公网可达或 Windows Helper 验收；本地子进程结果不能覆盖这些范围。
- 已准备 Android 用例：TunnelWire 4、TunnelHttp 3、TunnelDraft 4、TunnelRepository 6、TunnelMutationJournal 2，以及 InstallationRepository 原 ID 识别 2，共 21 个；涉及 current routes/revision、Token 仅写入、协议字段、外部路径、错误 ID/版本、同步未知结果、进程回收、身份隔离、存储损坏和删除前停止顺序。Android 编译、JVM/仪器测试和 lint 按用户要求未执行，用例准备不代表通过。
- 三语 XML/资源引用/占位符、Android 文档本地链接与 `git diff --check` 静态检查通过。尚待目标环境执行：真实 FRP 发布归档/平台兼容、frpc/frps 认证与每 proxy 连接、外部检测、Windows Helper 授权/运行、管理路径中断、并发写入/原键或原 ID 恢复、Token 脱敏、手机/平板/旋转/IME/大字体。

### BP05-M2 执行证据与未执行范围（2026-09-30）

- Server `--no-restore` 构建通过，0 错误、3 个既有 CA1416 平台告警；Server.Tests 使用 `-p:UsePrebuiltServerAssembly=true --no-restore` 构建通过，0 告警/错误。桌面调用与视图已按当前 DTO/CAS 同步，本轮未重复执行已知失败的框架引用解析构建；不宣称桌面已构建。
- `~/.dotnet/dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --frps-only` 通过。初次执行因沙箱拒绝本机 socket 未能进入生命周期检查；获准放宽沙箱限制后在本机回环监听、SQLite/Data Protection、实际 shell 子进程夹具执行通过。覆盖安全 PUT/编辑 Token 审计、缺少 expectedRevision 拒绝、revision 冲突、不反射秘密、受保护文件、单行秘密限制、保存/应用版本区分、拒绝隐式应用、缺失进程归属时 Unknown/停止拒绝、停止 disconnected、重新应用、dashboard 凭据保留、实际端口占用稳定失败与脱敏日志。不下载互联网资产、不执行真实 frps 协议、不开启公网端口，不代表真实 FRP 或 Windows Helper 验收。
- 最后一次进程句柄、启动错误与配置读取修改后已重建并重跑 `--frps-only`；同时重跑 `--frpc-state-only` 和 `--frpc-lifecycle-only`，均通过，覆盖本次共享 FRP 改动的客户端回归范围。
- 已准备 Android ManagedFrpsWire 4、HTTP 2、Draft 4、Repository 8，共 18 个用例；涉及安全/编辑读取分离、当前 revision/appliedRevision、缺字段/错误 proof、有界审计、PUT 路由、IP/范围/秘密/监听冲突、未知请求身份隔离、匹配 revision 才返回 Token、停止丢失/异常/失败阻止启动、每阶段独立提权与精确 frps 授权、切会话丢弃并清零秘密响应、未知写入结束后清零请求凭据。Android 编译、JVM/仪器测试和 lint 按用户要求未执行。
- 新增三语资源、桌面 JSON 键/占位符、XML、Android 文档链接与 `git diff --check` 静态检查通过。待目标环境执行：真实 frps/frpc 认证/隧道/vhost/Dashboard、防火墙/公网/TLS 信任、Windows Helper 管理进程与授权、Linux 重启遗留进程核实、并发修改/管理路径中断/未知请求恢复、手机/平板/旋转/IME/大字体。

### BP03-BP05-MERGE-V1 合并基本测试（2026-09-30）

- 合并提交 `ccdf8478`：将 `5f6f37be`（bp03_05）合入 `4c8685b6`（master）。四份 Android 文档冲突保留 BP01/BP02 的既有测试证据，同时采用 BP03～BP05 的当前实现与剩余计划。以下结果覆盖该合并代码及同批提交的两处 Kotlin 修正。
- 环境：Windows 11 amd64、.NET SDK 10.0.300、Gradle 9.7.1、Temurin JDK 21.0.12.1、Android SDK 36；Android 使用离线缓存。未安装或升级工具链。
- 首次 Android 主代码编译发现 `WebSiteDraft` 的可空 certificateId 在 lambda 内无法收窄，改用 `let` 验证已选 ID；随后单元测试代码编译发现 `WebPublishingWireTest` 仍引用已删除的证书路由常量，直接更新为当前 `CertificateRoutes.ROOT`。修正后重跑以下命令成功（最终 Gradle `BUILD SUCCESSFUL`，退出码 0）：

```powershell
# 在 Client/RelaxKonOS.Client.Android/ 执行
$bpMergeGradle = 'C:/Users/betha/.gradle/wrapper/dists/gradle-9.7.1-all/6yde0y3ecw7psqwo4h66kup3z/gradle-9.7.1/bin/gradle.bat'
& $bpMergeGradle :app:assembleDebug :app:testDebugUnitTest --offline --console=plain
```

- XML 报告合计 91 个测试类 / 660 个用例，失败 0、错误 0、跳过 0。BP03 实例管理 12、站点管理 18、BP04 证书管理/绑定 33、BP05 frpc 19 与 frps 18 个用例全部通过；InstallationRepository 的 12 个用例也通过（含 BP05 新增 2 个原 ID 识别用例）。BP01/BP02 已有测试包含在此次全量回归中。
- Debug APK 已生成：`app/build/outputs/apk/debug/app-debug.apk`；JVM XML 报告：`app/build/test-results/testDebugUnitTest/TEST-*.xml`；HTML 报告：`app/build/reports/tests/testDebugUnitTest/index.html`。这些是忽略的本机构建产物，不提交。
- .NET 首次默认并行构建因共享中间产物被占用失败（CS2012）；采用串行构建后成功，0 错误、5 条警告（CS8604、MVVMTK0034、AVLN5001 及两条 AVLN3001）。Server.Tests 正常项目引用构建成功，0 警告/错误；本次未使用预构建程序集选项。最终命令及四项专项均退出码 0：

```powershell
# 在仓库根目录执行
dotnet build RelaxKonOS.sln -c Debug --no-restore -v minimal -m:1 -p:UseSharedCompilation=false
dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj -c Debug --no-restore -v minimal -m:1 -p:UseSharedCompilation=false
dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --webserver-sites-only
dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --certificate-binding-only
dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --certificate-replay-only
dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --frpc-state-only
```

- 站点并发/CAS 与严格契约、证书绑定/实时选择器事实/部署重放、创建重放、FRP applied-state/协议/TOML 安全检查通过。三语 XML 各 1554 个唯一字符串键、无重复且键集一致；`git diff --check` 通过。
- 未执行整套 Server.Tests、Android lint/仪器测试/手机平板交互，也未执行真实 Nginx/ACME/FRP 网络、安装或 Windows Helper 验收。`--frps-only` 的 shell 夹具要求 Linux；frpc 生命周期夹具在 Windows 跳过，所以本轮未将其计为通过。原有 Linux 夹具历史证据继续保留。

### BP06-M1-V1（2026-09-30，当前工作树）

- Android：`JAVA_HOME=C:/Program Files/Android/openjdk/jdk-21.0.8`，`E:/environments/gradle-9.7.1-all/gradle-9.7.1/bin/gradle.bat :app:testDebugUnitTest :app:assembleDebug --offline --no-daemon -p Client/RelaxKonOS.Client.Android` 通过；94 类/669 JVM 用例零失败，新增 ProxyWireTest 4、ProxyHttpTest 1、ProxyRepositoryTest 4。生成 Debug APK，未部署设备。覆盖路由编码、严格当前格式、原键显式重试、接受 ID 后查询失败、秘密不落日志、会话/观察者隔离、损坏标记拒绝写入。
- Server：`dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -v minimal` 通过；`dotnet run --project RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -p:UseAppHost=false -- --proxy-configuration-only` 通过，证明存储配置加载后才更新活动元数据、重载失败回滚且保留旧标记、随后激活成功。NU1900（离线漏洞数据不可用）及既有平台分析警告保留。
- 原 `--proxy-geodata-only` 在执行代理检查前因既有 CertificateBindingChecks 临时目录缺失失败，未算代理专项通过。新增独立配置专项入口；首次清理遇到 Windows SQLite 连接池文件占用，入口已在删除前清空连接池，最终命令通过。
- 三语 XML 有效、无重复键、键集一致与 `git diff --check` 通过。`python Tools/Mobile/sync-desktop-icons.py --check` 因此 Python 缺 Pillow 未执行；没有修改图标资产。
- 未执行：设备/模拟器交互、IME/大字体/三语视觉、真实 Ubuntu/Windows Mihomo 安装与 Helper、SAF/取消竞争/重启恢复、真实订阅/Docker 网络、lint 和仪器测试。成功构建不替代这些验收。

### BP06-M2-V1（2026-09-30，当前工作树）

- 使用 BP06-M1-V1 的同一 Gradle 命令，`:app:testDebugUnitTest :app:assembleDebug --offline --no-daemon` 通过。新增 ProxyDiagnosticsWireTest 4、ProxyHttpTest 增加 1、ProxyRepositoryTest 增加 2，共 7 例；证明修改一个设置字段保留完整 TUN/Windows 嵌套选项、恢复标记保持、负计数/超限日志拒绝、TUN HTTP 请求正确、紧急恢复不依赖概览且保留先前未知写入、选择变更须控制器事实后清除标记。
- 设备和真实 Linux/Windows TUN/系统代理、断线管理路径保护、紧急恢复、GeoData 文件与 DNS 实际行为未执行；已有 Server 网络安全专项不自动替代本次真实宿主验收。

### BP02-M2-V1（2026-09-30，当前工作树）

- Android 全量 JVM/APK 通过，新增 OutboundProxyEditorTest 3 例：受管来源清除 URL/凭据并保留消费范围、未保存保护确认后到达请求的目标、观察者不修改设置。
- `dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -p:UseAppHost=false -p:OutDir=E:/riderprojects/RelaxKon/RelaxKonOS/RelaxKonOS.Server.Tests/bin/bp06-validation/ -v quiet` 通过；运行该目录 `RelaxKonOS.Server.Tests.dll --managed-outbound-proxy-only` 通过。使用本地 TCP 代理/源站真实 HTTP 请求，覆盖镜像标签及运行时下载、NO_PROXY、未选择范围直连、即时解析新监听地址、监听器停止后拒绝所选请求及不向源站直连。包含既有 Docker proxy 结构/凭据/引擎夹具；未执行真实 Docker Engine 或外网请求。
- 首次夹具执行暴露既有 DataProtectionProvider 字符串重载将目录当应用名；改用 DirectoryInfo 保证测试密钥留在临时目录。Windows 未处理异常进程暂占默认测试产物，最终使用上述独立输出目录重建及执行，均退出码 0。没有更改生产密钥位置。
- 真实 Ubuntu/Windows Docker 重启、Mihomo 安装/监听、外网下载和手机/平板跳转仍待验收。

### BP17-M1-V1（2026-09-30，当前工作树）

- 使用 BP06-M1-V1 的 Gradle 命令，JVM/APK 通过。新增 OperationDestinationsTest 4、OperationCenterTest 3、EventAlertRepositoryTest 2，共 9 例；覆盖首批已实现安装表单、缺失 capability/跨领域目标、原 Proxy ID 失联后核实、错误 kind/404、未伪造未知任务、固定告警目标及详情/确认响应归属。再次刷新清空旧诊断，异步诊断结果按观察代次隔离。
- 手机/平板、离页/旋转/进程回收与真实修复入口未执行；后续领域交付继续同步 BP17。

### BP07-M1-V1（2026-09-30，当前工作树）

- Android 同一离线 Gradle 命令的全量 JVM/APK 通过。新增 FirewallWireTest 3、FirewallHttpTest 1、FirewallRepositoryTest 7，共 11 例；覆盖完整状态/配对地址族/编号、IP/CIDR/端口与命令文本校验、五类实际 HTTP 方法/路由/DELETE 密码正文、密码不反射/可变数组清零（含等待锁时取消）、未知请求无重放/显式采用、状态变化拒绝、精确授权和提权后再读、不可用平台及观察者/会话/损坏存储边界。首次编译因两处通用资源名不存在失败，已改为独立三语防火墙资源，随后完整命令通过。
- `dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -p:UseAppHost=false -p:OutDir=E:/riderprojects/RelaxKon/RelaxKonOS/RelaxKonOS.Server.Tests/bin/bp06-validation/ -v quiet` 通过；该程序集 `--firewall-read-only` 通过，验证 Helper 拒绝不返回空集合、成功空集合、IPv4/IPv6 逻辑配对及非法字段不会调用 Helper。使用内存 transport，不执行宿主 UFW 命令。首次测试编译引用了错误的 Output 参数，已更新为当前 OutputBase64 并重建通过。
- Server 及 Android 均未执行真实 UFW/Helper 写入、管理网络断线或多客户端并发。现有规则 API 没有原子 revision/CAS，提交前快照比较不能代替并发原子性。设备/模拟器、IME/大字体、三語视觉、lint/仪器测试未执行。

### BP06–BP07-FINAL-V1（2026-09-30，当前工作树）

- 最后一次密码等待取消保护后执行同一离线 Gradle 全量命令成功：101 个测试类、699 个用例，失败/错误/跳过均为 0；Debug APK 已生成。相对合并基线新增 39 例，覆盖 BP06-M1/M2、BP02-M2、BP17-M1 与 BP07-M1。
- 三语 XML 各 1734 个唯一键，键集一致、Kotlin 资源引用有效；本轮功能/状态/计划文件的本地链接存在；`git diff --check` 通过。图标校验仍受缺 Pillow 限制，没有图标改动。
- Server 配置激活/回滚、受管出站实际本地 HTTP 消费、防火墙失败读取/逻辑配对专项分别通过。未运行整套 Server.Tests、真实网络/安装/Helper 副作用、设备/模拟器、lint 或仪器测试；不将这些标为通过。

### BP08-M1-V1

- 2026-09-30，当前未提交工作树；Windows，本地 Gradle 9.7.1 / JDK 21.0.8。使用 BP06-M1-V1 的离线 Gradle 命令，全量 JVM/APK 通过：105 类、719 例，失败/错误/跳过均为 0。新增 SmbWireTest 4、SmbHttpTest 1、SmbDraftTest 4、SmbRepositoryTest 9，OperationCenterTest 和 OperationDestinationsTest 各增加 1，共 20 例。实际本地 HTTP 验证九种同步请求方法/编码/完整正文与无伪幂等键；覆盖权限/访客/root 暴露边界、精确提权后复核、未知写入不重放/显式采用、回执后失联、无凭据持久化、会话/观察者/损坏存储及密码等待取消清零。
- 首次 Android 编译发现三处资源键拼写及 ListRow 位置参数错误，已修正；随后完整命令通过。仅有既有 TabRow 弃用警告；Debug APK 已生成，未部署设备。
- Server 当前 C# DTO 使用 RelaxKonOSJsonOptions.Default 的实际序列化验证 camelCase；内存 Helper transport 验证 Linux/Windows 失败、缺少输出、无效 base64/JSON 不返回空集合，显式成功 [] 有效；Linux 创建共享读取失败时禁止 apply。包含现有文件服务 provider、验证器、锁、Windows ACL/ledger/drift、安全快照及安装 capability 检查。以下命令均退出码 0：

```powershell
dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -p:UseAppHost=false -p:OutDir=E:/riderprojects/RelaxKon/RelaxKonOS/RelaxKonOS.Server.Tests/bin/bp08-wire-validation/ -v quiet
dotnet RelaxKonOS.Server.Tests/bin/bp08-wire-validation/RelaxKonOS.Server.Tests.dll --file-services-only
```

- 首次服务端 fixture 错误预期 PascalCase，已改为当前 camelCase 并重建验证通过；最终使用独立输出目录避开 Windows 未处理异常进程暂占旧 DLL。--file-services-only 分支提前到初始化后执行，避开无关证书临时目录夹具。NU1900 离线漏洞数据及既有平台分析警告保留。
- 未执行真实 Samba 包安装、服务启停、TCP 445/第三方 SMB 文件传输、Samba 凭据后端、Windows SMB Server/ACL/安全基线/回滚、Helper 提权副作用、断线/多客户端并发、手机平板/IME/大字体/三语视觉、lint 或仪器测试。快照预读没有原子 revision/CAS；客户端检查不能替代宿主并发验收。

- BP08 静态收尾检查通过：三语 XML 各 1807 个唯一键、键集一致、无重复；Kotlin 资源引用全部存在，当前文档本地链接有效，`git diff --check` 通过。复用既有文件服务图标，无资产修改；图标同步校验仍受 Python 缺 Pillow 限制。

### BP09-M1-V1

- 2026-09-30，当前未提交工作树；Windows，沿用本地 Gradle 9.7.1 / JDK 21.0.8 及 BP06-M1-V1 离线命令。全量 JVM/APK 通过：108 类、736 例，失败/错误/跳过均为 0。新增 DockerControlWireTest 4、DockerControlHttpTest 1、DockerControlRepositoryTest 10；InstallationRepositoryTest 和 OperationCenterTest 各增加 1，共 17 例。DockerWireTest 原 2 例改为当前 logLines/logTruncated，不继续读取 messages；原安装跳转用例同步支持 Docker 原生表单。
- 实际本地 HTTP 验证三类 engine action 的 confirmed、镜像源 CRUD/默认选择 null/Bearer/无伪幂等键与当前 201/204；strict 状态/默认源/target/选择一致性及 URL 校验、未知写入无正文/不重放/显式采用、引擎 Stop 成功但不可达、成功后失联、错误镜像源响应、观察者/会话/损坏存储均通过。401 原请求明确拒绝后重试复核事实，事实变化时不发送第二次写入；Docker 安装精确 dockerInstall/docker 授权后复用原键、清零密码、拒绝 Upgrade。
- Server 以下命令通过，0 错误，NU1900 离线漏洞数据及既有 CA1416 平台分析警告保留：

```powershell
dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -p:UseAppHost=false -p:OutDir=E:/riderprojects/RelaxKon/RelaxKonOS/RelaxKonOS.Server.Tests/bin/bp09-validation/ -v quiet
dotnet RelaxKonOS.Server.Tests/bin/bp09-validation/RelaxKonOS.Server.Tests.dll --docker-control-only
```

- 引擎专项使用 recording host controller/daemon，验证固定 action、Stop/Restart 未确认零宿主派发、平台拒绝及操作后状态。DockerMirrorChecks 使用内存仓库、真实本地 Kestrel/HttpClient 和测试身份验证当前 REST、地址规范化、账户隔离、选择/更新/删除/default、Docker Hub resolver 和显式其他 registry 保留；不访问真实 registry、不修改实际 host Engine。新增早期专项入口不依赖无关证书夹具。
- 未执行真实 Linux Docker 安装、Helper 授权/服务控制及对实际容器的中断影响、Windows Docker Desktop CLI、镜像源 TLS/实际拉取、外部并发/断网/进程回收、手机平板/三语视觉/IME/大字体、lint 或仪器测试。预读没有原子 revision/CAS；当前证据不支持宿主验收通过。该批证据只覆盖 BP09-M1，不替代后续 BP09-M2 的独立验证。

- BP09-M1 静态收尾通过：三语 XML 各 1842 个唯一键、键集与格式占位符一致，Kotlin 资源引用全部有效；当前功能/计划/状态文档的本地链接存在；`git diff --check` 通过。复用既有 Docker 图标，无资产改动。

### BP09-M2-V1

- 2026-09-30，当前未提交工作树；Windows，沿用 Gradle 9.7.1 / JDK 21.0.8 离线命令。全量 JVM/APK 通过：112 类、754 例，失败/错误/跳过均为 0。新增 DockerResourceWireTest 3、DockerResourceHttpTest 1、DockerResourceRepositoryTest 12、DockerResourceDraftTest 1，OperationCenterTest 增加 1，共 18 例；Debug APK 已生成。
- HTTP 使用本地 HttpServer/RelaxKonApi，验证创建/名称 PUT、容器 delete POST/force/confirmed、镜像 DELETE 正文/编码 sha256 ID、network/volume DELETE query 且无正文、完整标签/类型化资源参数、Bearer 和无伪任务键。用例覆盖所有权标签必读、详情/统计身份与十六进制前缀、资源/数值/标签校验、未知创建不保存正文/不重放/显式采纳、不可达 Engine 不清除资源未知记录、摘要未变但详情变化、受管资源/内置网络/停止容器卷引用保护、控制/资源/Compose 共用阻断、安装不可读阻断、成功后回读、失败/失联保留、401 后详情再查、观察者/会话/损坏存储、等待共享锁时取消及运维无伪任务。
- Server 以下命令通过，0 错误；NU1900 离线漏洞数据和既有 CA1416 警告保留：

```powershell
dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -p:UseAppHost=false -p:OutDir=E:/riderprojects/RelaxKon/RelaxKonOS/RelaxKonOS.Server.Tests/bin/bp09-resource-validation/ -v quiet
dotnet RelaxKonOS.Server.Tests/bin/bp09-resource-validation/RelaxKonOS.Server.Tests.dll --docker-resources-only
dotnet RelaxKonOS.Server.Tests/bin/bp09-resource-validation/RelaxKonOS.Server.Tests.dll --docker-control-only
```

- DockerResourceChecks 使用指定的必不存在 Docker CLI 路径、真实本地 Kestrel/HttpClient、测试身份及 host-mode fixture，不派发真实 Docker 命令。验证四种列表/统计失败返回 503、卷引用读取失败禁止删除、完整镜像 ID 通过校验并到达必失败 CLI、错误表格/所有权标签拒绝、实际 null 标签合法、网络 labels 当前序列化、容器 stdout/stderr 合并及尾部/512 字符截断。引擎/账户镜像源既有专项回归通过；未执行整套 Server.Tests。
- 桌面共享 DTO 调用方编译通过：`dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -p:UseAppHost=false -p:UsedAvaloniaProducts= -m:1 -v quiet`，0 错误、5 个既有警告。最初正常编译被 Avalonia 外部遥测日志目录写入权限阻止；读取已有 BuildServices targets 后，使用该参数跳过遥测任务，未修改项目文件或安装依赖。
- 三语 XML 各 1890 个唯一键，键集/占位符一致、Kotlin 字符串资源引用有效；当前文档本地链接存在，`git diff --check` 通过。无图标资产修改。
- 未执行实际 Docker Engine 的创建/拉取/生命周期/强制操作、网络/卷数据及引用竞争、真实归属/镜像源/代理消费、进程回收/多客户端并发、手机平板/三语视觉/IME/大字体、lint 或仪器测试。快照复核没有服务端原子 CAS；当前记录不标记这些宿主/设备验收通过。

### BP11-V1（2026-10-01，当前未提交工作树）

- Windows，Gradle 9.7.1/JDK 21.0.8，沿用 BP06-M1-V1 离线命令 `:app:testDebugUnitTest :app:assembleDebug --offline --no-daemon -p Client/RelaxKonOS.Client.Android --console=plain`。115 类、764 例，失败/错误/跳过均为 0，Debug APK 生成。新增 TextEditorTest 3、TextEditorRepositoryTest 3、TextEditingTest 4，共 10 例，验证当前严格格式/编码字节限制、实际本地 HTTP 的 GET/PUT/POST/Git PUT、完整条件格式/路径编码/Bearer/无伪任务键、错误回执/失联不重放、会话变化与晚到响应、CRLF/CR 输入/粘贴/光标及混合换行保留。
- Server/Helper 构建：`dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -p:UseAppHost=false -p:OutDir=E:/riderprojects/RelaxKon/RelaxKonOS/RelaxKonOS.Server.Tests/bin/bp11-text-validation/ -v quiet`；执行 `dotnet RelaxKonOS.Server.Tests/bin/bp11-text-validation/RelaxKonOS.Server.Tests.dll --text-editor-only`。24 条专项检查通过：五编码精确 BOM 字节往返、混合换行、非法 Unicode/二进制/编码大小、有界文件读取、新建与已有目标保护/暂存清理、CAS/冲突保留、另存编码、当前 DTO/Helper 闭合请求。使用隔离临时目录及 LocalFileService，不派发真实 Helper 提权。
- 桌面：`dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -p:UseAppHost=false -p:UsedAvaloniaProducts= -m:1 -v quiet`，0 错误，5 个既有警告。Server 构建保留 NU1900 离线漏洞数据及既有平台警告。初次 Android 编译修正了类型/Compose 捕获和旧引用；文本专项发现新建暂存名不符合文件服务命名规则，修正后重建与专项通过。初次失败的 Windows 崩溃处理进程已按本次测试命令精确清理。
- 未执行真机/模拟器、仪器测试或 lint、真实 Linux/Windows Helper 的用户身份与权限、文件系统外部并发/崩溃/磁盘满/符号链接竞争、进程回收后的未知写入、手机平板/旋转/IME/大字体/三语视觉。文本读取和条件保存没有 root/临时提权兜底；这类保护路径编辑不标为支持。客户端 HTTP 测试不替代真实 Server HTTP/授权集成验收。既有内容接口及桌面通用编辑器不自动获得 text 路由的 CAS 行为。

- BP11 静态检查：三语各 1908 个唯一字符串键，键集一致、Kotlin 字符串引用有效，Android 文档本地文件链接存在，`git diff --check` 通过。无图标资产变更。

### BP10-V1（2026-10-01，当前未提交工作树）

- Windows，Gradle 9.7.1/JDK 21.0.8，沿用 BP06-M1-V1 的离线 `:app:testDebugUnitTest :app:assembleDebug` 命令。118 类、784 例，失败/错误/跳过均为 0，Debug APK 生成。新增 GitWorkspaceTest 4、GitWorkspaceRepositoryTest 11、GitWorkspaceJournalTest 2，InstallationRepositoryTest、OperationCenterTest、OperationDestinationsTest 各增加 1，共 20 例。真实本地 HttpServer 验证 12 种修改请求的方法/路径/正文/Bearer、索引提交空 paths、非强制/非 amend、冲突 revision/选择/继续/中止，且没有伪任务键或凭据正文。
- JVM 用例覆盖严格当前 configVersion/分支 SHA/diff version、相对路径/分支规则、编辑冲突标记/编码大小、丢失响应不重放、账户隔离/损坏存储拒绝写、显式事实采用、401 重试前复核、二进制/截断内容版本改变、分支尖端/远端配置改变、安装阻断、错误回执/回读失联、普通身份门禁、冲突操作/版本变化、晚到响应和原账号标记。Git 安装验证精确 `gitPackageInstall/git`、原请求键、密码清零及不支持 Upgrade；任务中心没有伪造同步操作 ID。
- 以下命令通过，0 错误；构建保留 NU1900 离线漏洞数据与既有 CA1416 警告：

```powershell
dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -p:UseAppHost=false -p:OutDir=E:/riderprojects/RelaxKon/RelaxKonOS/RelaxKonOS.Server.Tests/bin/bp10-validation/ -v quiet
dotnet RelaxKonOS.Server.Tests/bin/bp10-validation/RelaxKonOS.Server.Tests.dll --git-conflicts-only
dotnet RelaxKonOS.Server.Tests/bin/bp10-validation/RelaxKonOS.Server.Tests.dll --text-editor-only
```

- Git 专项 89 项通过：使用隔离临时 SQLite/工作树及实际本机 Git CLI，验证 merge/rebase/revert/cherry-pick/squash、修改/删除/二进制/嵌套缺失目标、旧 revision 拒绝、继续/中止、工作树 `.git` 文件、SHA/CAS/无覆盖、Unicode/重命名两端/前导空格、索引提交保留未暂存内容、多行历史/分页/提交详情、必需字段缺失拒绝、二进制/截断全量版本、全配置与多 push URL 指纹/无凭据 HTTP DTO、坏配置/差异读取失败拒绝、unborn 空历史，以及 Helper 1.5 封闭请求/旧版本拒绝。没有访问外部 Git 远端；Pull 使用临时本机 `.` 远端，不修改真实仓库。BP11 文本 24 项回归通过。
- 桌面当前共享 DTO 调用方构建：`dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -p:UseAppHost=false -p:UsedAvaloniaProducts= -m:1 -v quiet`，0 错误、5 个既有警告。最初 Git 专项入口经过无关证书夹具失败，已前移隔离入口；Windows 临时 Git 对象只读属性清理已处理。新增安装测试首次错误使用 Docker capability，修正为 Git 后全量通过。
- 静态检查：三语 XML 各 1989 个唯一键、键集/占位符一致，Kotlin 字符串引用有效，Android 文档本地文件链接存在，`git diff --check` 通过。无图标资产改动。
- 未执行真实 Linux Git 包安装/取消/断网/原键回执、Linux 降权或 Windows impersonation 的真实身份/权限、SSH/HTTPS 私有远端认证和推送、真实 Server HTTP 授权集成、外部多客户端/文件系统/符号链接竞争、崩溃/磁盘满/进程回收恢复、手机平板/配置重建/未保存关闭/IME/大字体/三语视觉、lint 或仪器测试。工作区复核不是服务端仓库事务 CAS；普通文件条件写入不保证外部 Git 状态与文件一起原子更新。既有隔离构建/发布未在本轮重新执行。

### BP12-V1（2026-10-01，当前未提交工作树）

- Windows，Gradle 9.7.1/JDK 21.0.8，离线执行完整 JVM 回归与 Debug APK。122 类、805 例，失败/错误/跳过均为 0。新增 FileBrowserPolicyTest 4、FileBatchRunnerTest 5、ViewerTransformTest 3、FileBrowserWireTest 2，FilesRepositoryTest 增加 2、ImagePreviewCacheTest 增加 5，共 21 例。

```powershell
$env:JAVA_HOME = 'C:/Program Files/Android/openjdk/jdk-21.0.8'
& 'E:/environments/gradle-9.7.1-all/gradle-9.7.1/bin/gradle.bat' `
  :app:testDebugUnitTest :app:assembleDebug --offline --no-daemon `
  -p Client/RelaxKonOS.Client.Android --console=plain
```

- 实际本地 HttpServer 验证当前列表/属性/权限接口的方法、路径、Bearer、Unicode/原始空格路径、模式特殊位、属性与访问时间、隐藏/系统/驱动器字段和畸形字段拒绝。未启动真实 RelaxKonOS Server HTTP、Helper 或修改宿主权限；消费现有 Protocol 字段和权限路由，BP12 没有改动共享线协议或增加替代路由。
- Repository/BatchRunner 验证逐项成功与 4xx 未完成、Transport/5xx 未知停止且不重发、未执行余项、授权被取消后一次提示即停止、等当前项目返回再停止、旧账户不派发/不发布结果，以及显式路径 `write` 授权范围和特殊位保持。新增授权期间切账户回归复现文件提权入口的旧请求问题，修复后通过；旧管理员答案在发送前拒绝且清零。
- 纯策略验证宿主隐藏标志、目录优先/排序/过滤、500 项上限/根保护、目录后代和重复选择归并、Windows 分隔符/大小写、绝对目标与点段/自身后代拒绝、原始名称空格、八进制。图片变换验证旋转后适配、偏移边界、重置及异常手势值；没有在 JVM 中运行真实 BitmapFactory/Compose 布局。
- 真实临时文件验证独立预览暂存、未确认完整文件不可命中、长度检查、迟到清理不删除新缓存、宿主同账户/跨账户命名空间、实际写入 64 MiB 上限、清除进程遗留暂存且保留活动/非所属文件。文件操作的 ViewModel 与预览/下载绑定会话和请求代次，防止迟到响应覆盖新选择；旋转/离页实际 UI 行为仍需要设备验收。
- 静态检查：中/英/日 XML 各 2048 个唯一键，键集与占位符多重集一致，Kotlin 字符串引用有效，Android 文档本地文件链接存在，`git diff --check` 通过；沿用桌面图标，没有新增或更改图标资产。
- 构建生成 `app/build/outputs/apk/debug/app-debug.apk`；报告 `app/build/test-results/testDebugUnitTest/`。未执行 lint、instrumentation、模拟器或真机视觉；未执行真实 Linux/Windows 文件系统、POSIX chmod/Windows ACL、Helper 提权、源/目标并发变化、磁盘满、复制部分失败、真实网络中断与应用进程回收。BP10/BP11 的 .NET 证据保留原范围，本次未重新运行 .NET 套件。

未关闭验收：

| ID | 场景 | 预期 |
| --- | --- | --- |
| BP12-T1 | 手机/平板、横竖屏、大字体、三语、目录属性、多选筛选/排序/刷新/改名 | 操作入口可达，选择和详情不跳到其他文件，隐藏选择计数准确 |
| BP12-T2 | 多目录复制/移动/删除，目标重名、磁盘满、子项权限失败、网络中断、停止竞争 | 不覆盖目标；完整/部分/未知与未执行分开；当前项目事实准确；无自动重发或虚假回滚 |
| BP12-T3 | Linux 0000/4755/7777、特殊位、目录非递归；Windows ACL、受限身份、显式授权及弹窗中切账户 | 平台门控和实际模式正确；不扩大路径授权，不向新会话发送旧请求/密码 |
| BP12-T4 | EXIF/长宽图、大图、64 MiB/未知长度、快速切图、失败/授权、缩放拖动/旋转/适配/关闭 | 真实 Compose 旋转后完整适配，不拖出画布；读取和暂存有界；旧图不覆盖新图，无重复传输 |
| BP12-T5 | 批量、授权或图片下载期间旋转/离页/回收进程，再登录相同或不同账户 | 内存状态在配置变更中保留；进程回收不自动恢复副作用；缓存隔离且遗留暂存清理；分块续传原链路不回归 |
