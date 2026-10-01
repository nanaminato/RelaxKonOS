# Android 测试进度与验收清单

> 更新：2026-09-30。统一维护测试进度、已有执行证据和未关闭检查，区分自动化、模拟器、真实宿主和实体设备；实现进度见 [Progress](Progress.md#2-bp-实现进度)。未执行测试不阻止下一步实现，未测结果不得写成通过。本轮新增验证范围按 BP 编号记录，不将历史验证覆盖到新代码。AD 表为真实宿主/设备检查，已有 JVM 或构建证据不代表整项通过；依赖尚未实现能力的项目先完成 [部署后续工作](../plans/Deployment.md)。

## 1. 共同设备与发布检查

2026-10-01 自签名证书信任：登录探测显示地址、证书主题/签发者、有效期与新旧 SHA-256 指纹；确认记录仅限应用内地址、端口、叶证书。API、上传与 SignalR 终端复用该记录，保留主机名与有效期校验并禁用跨地址重定向。`ServerCertificateTrustTest` 和 `assembleDebug --offline` 通过，覆盖确认前拒绝、记录重载、主机/端口隔离、证书更换及过期拒绝。实体手机仍需验证首次确认/取消、应用重启、证书更换、错误主机名及终端连接；构建和 JVM 证据不代表设备检查通过。

2026-10-01 SSH 部署表单键盘避让：滚动容器使用 IME 内边距，并消费 Scaffold 已应用的边距；`assembleDebug -Offline` 通过。实体手机仍需确认证书名称、sudo 密码与证书密码在键盘展开、收起及切换输入框时保持可见，底部操作可滚动到达。

2026-10-01 sudo 安装验证：`ServerCenterDeploymentClientTest` 与 `assembleDebug` 通过，覆盖标准输入传密、免密提权入口和原 SSH 用户操作回执查询；共享启动器的 `Tests/Deployment/SudoElevationChecks.ps1` 通过密码/免密策略、引擎输入隔离、受保护状态读取、错误密码与权限拒绝的模拟检查。尚未执行真实 Ubuntu 26.04 管理账户的系统安装、升级、独立健康核验及实体 Android 设备检查；模拟结果不代表这些宿主检查通过。

最小矩阵：一台手机竖/横屏、约 8 英寸与约 11 英寸平板；逐项覆盖中/英/日、浅/深/高对比、大字体、TalkBack、分屏、旋转、后台返回与进程回收。布局目标按具体页面检查，不以 Shell 断点计算通过替代页面分栏验收。

| 范围 | 未关闭检查 |
| --- | --- |
| 登录与凭据 | 强指纹成功/取消/失败/锁定，新录指纹保留失效记录，忘记密码/删除仅作用于选中身份；弱生物识别/锁屏五分钟窗口及过期，提权按次强指纹与 token 变化，debug 无锁屏兜底在 release 不可用 |
| 切换与徽标 | 登录选择、更多和连接页切换账号，密码保留、实际指纹提示、系统切换后的徽标、长地址/长名称与三语视觉 |
| Windows owner-device keys | 真 Windows 10/11 配对、邀请码过期/取消、QR 扫描/图片/粘贴确认、nonce 签名、锁屏窗口、密钥失效与撤销；Windows Server 不走工作站授权 |
| 文件下载/预览 | MediaStore 下载目录与重名文案；缩略图→详细图、EXIF 方向、大图耗时、缓存预算/淘汰、显式授权才预览；Android org.json null 显示与 executionEligibility 解析 |
| Server 终端布局 | API 35 平板模拟器已验证 8 项 IME/边距/大字体/草稿检查；厂商输入法、实体手机/平板、真实 PTY 和中文组合/复杂 VT 仍需执行 |
| SSH 终端 | 平板模拟器已验证 6 项输入可见性、IME 发送、边距、大字体、草稿及离页返回/多会话控件检查；实体手机/厂商输入法、真实多 SSH 主机连续命令、后台保活与远端退出仍需联调 |
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
| AD01-T7 | 勾选「在本机保存 SSH 密码」后重新打开服务器中心（含重启应用），再点该主机 | 用一次指纹/锁屏确认即可连接，不再需要输入密码；取消勾选后已有记录仍在，只有「忘记已保存密码」才删除它 |
| AD01-T8 | 同一设备保存两台以上主机，在工作区**系统页**点「切换主机」 | 已保存密码的主机就地切换工作区并显示新主机名；未保存密码的主机退回服务器中心表单，不出现第二个密码输入位置；文件、终端与部署页都没有主机条，也没有第二个切换入口 |
| AD01-T9 | 新增一台连不上的主机（密码错、TCP 超时、端口无监听、主机名写错各一次） | 四种情况给出四句不同的可执行文案，不再共用「无法验证 SSH 连接」；`adb logcat -s RelaxKonSsh:D` 里的分类名与界面结论一致，且日志与界面都不出现端点、用户名或异常原文 |
| AD01-T10 | 主机的 SSH 主机密钥在设备上已固定后发生变化（重装/重建密钥，或 DHCP 把该地址分给另一台机器），再点这台主机 | 弹出标题为「SSH 主机密钥已变更」的核对对话框，**并排展示旧指纹与它的确认日期、以及本次收到的新指纹**；确认后立刻用同一份密码重新握手进入工作区，且该端点的固定记录只剩新指纹一条。关闭对话框只留下「核对并接受后才能继续」的提示，不存在任何跳过核对的入口 |
| AD01-T11 | 点开一台已保存密码的主机，进入工作区后看系统页 | 指纹/锁屏确认**只出现一次**（解封那一次），不再紧接着弹第二次加密保存的确认；系统页顶部是当前主机卡片（主机名 + `用户@地址:端口` + 「当前」标签）与「切换主机」，四个页面的内容与标题都不与状态栏、时钟重叠 |

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
| AD07-T6a | 实体手机连接真实 SSH 主机，键盘显隐、字号调整后连续发送命令 | 输入/光标始终可见；沿用同一 shell、不误报关闭；远端退出或断网时准确禁用输入 |
| AD07-T6b | 同宿主两个 SSH 终端与不同宿主并行；切换部署/文件/系统、返回服务器中心及旋转 | 沿用每个原 shell；输出/草稿/隐藏状态隔离；关闭一个不影响其它连接；后台输出继续入有界缓冲 |
| AD07-T6c | 重启/系统回收应用进程后重新打开 SSH 终端 | 旧会话/选中状态/草稿/输出清空，不显示待重连记录或重放命令；进入宿主终端建立新 shell；宿主资料和用户保存的凭据仍保留 |

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
| 2026-09-30 | 工作区「切换主机」收进系统页：离线 `:app:assembleDebug :app:testDebugUnitTest :app:assembleDebugAndroidTest` 成功，96 类 / 691 JVM 用例，0 失败/错误/跳过；三语未增删键（各 1595 键一致）；`git diff --check` 通过 | `SshWorkspaceHeader` 与工作区的 `topBar` 一并删除，主机身份卡片（主机名 + `用户@地址:端口` + 「当前」）与「切换主机」移到 `SshSystemScreen` 顶部；文件、终端与部署页不再有主机条，也就没有第二个切换入口。少了 `topBar` 之后状态栏内边距由顶层 `Scaffold` 的 `contentWindowInsets` 提供。切换本身的判定与凭据路径未改（仍是 `planSshHostOpen` + `SshHostSwitcherDialog`），因此只需按 AD01-T8 复测入口位置；系统页新增卡片后可滚动，真机观感未在本轮复测 |
| 2026-09-30 | SSH 连接不再重复请求指纹保存 + 工作区顶栏让出状态栏：离线 `:app:assembleDebug :app:testDebugUnitTest :app:assembleDebugAndroidTest` 成功，96 类 / 691 JVM 用例，0 失败/错误/跳过；`git diff --check` 通过 | 真机截图里「192.168.1.8:22 / codexdev@… / 切换主机」与状态栏时钟同高：`SshWorkspaceHeader` 作为 `Scaffold` 顶栏没有让出 `statusBarsPadding`，而 `Scaffold` 只按它量出的高度给下方内容留位。另：`beginVerification` 在每次握手成功后都按勾选框写保险箱，从保险箱解封来的密码因此被要求再加密一次（`VaultAccess.save` 会弹确认）。现在 `shouldSaveSshPassword` 增加密码来源这一个条件（`SshPasswordOrigin`），只有用户本次输入的密码才问；2 项新 JVM 用例覆盖「解封/会话内存来的都不再问」与「恰好一个来源值得问」。真机上的实际观感与指纹只弹一次未在本轮重测 |
| 2026-09-30 | 主机密钥变更不再是无出口的阻断：离线 `:app:assembleDebug :app:testDebugUnitTest :app:assembleDebugAndroidTest` 成功，96 类 / 689 JVM 用例，0 失败/错误/跳过；中英日各 1595 键一致无重复；`git diff --check` 通过 | 改动前 `KeyChanged` 只有一行红字、没有任何动作，且密码/target 在返回时被清掉，用户无法接受新指纹；现在与首次固定共用一次显式核对，并排展示旧指纹与确认日期。新增 4 项 JVM 用例覆盖「首次见面不展示旧指纹」「变更必须携带被取代的固定记录」「Trusted/Failed 一律不弹指纹对话框」「两种确认共用同一条替换规则」。变更路径的真实触发（改主机密钥后重新握手）未在实体设备执行，需按 AD01-T10 联调；本轮仍未验证指纹/锁屏解封与保险箱实际写入 |
| 2026-09-30 | SSH 握手失败归因：真机 `SM-S9380` 上新增主机报「无法验证 SSH 连接」，`adb logcat -s RelaxKonSsh:D` 里该次只有一个 `connect.begin` 加 `connect.failed: classification=connect_timed_out types=JSchException>SocketTimeoutException frames=Util.createSocket:387`，全程没有 `host_key.observed`；能连上的主机则先出现 `host_key.observed: trust=Trusted` 再 `connect.authenticated`。改动后离线 `:app:assembleDebug :app:testDebugUnitTest :app:assembleDebugAndroidTest` 成功，95 类 / 685 JVM 用例，0 失败/错误/跳过；中英日各 1592 键一致无重复；`git diff --check` 通过 | 失败发生在 TCP 建连阶段、主机密钥尚未交换，因此与指纹确认无关；同一手机 `toybox nc` 连该主机另一地址的 22 端口立即收到 SSH banner，而报错地址超时，主机侧 `ss`/防火墙未在本轮核对。12 项新用例覆盖九类原因、网络层原因优先于库消息、诊断名唯一稳定；实体设备上的四类失败文案（AD01-T9）与指纹解封、保险箱实际写入未在本轮重测 |
| 2026-09-30 | SSH 凭据保存与主机切换：离线 `:app:assembleDebug :app:testDebugUnitTest :app:assembleDebugAndroidTest` 成功，94 类 / 673 JVM 用例，0 失败/错误/跳过；中英日各 1585 键一致无重复；`git diff --check` 通过 | 新增 4 项 JVM 用例覆盖「只有可用保存凭据才免输入直连」「三种索要密码的原因两两可区分」「未勾选或本机无法保护时不写入保险箱」；界面改动为服务器中心列表点开直连、保存密码勾选与状态行、忘记已保存密码、工作区「切换主机」。`VaultAccess` 指纹/锁屏解封、保险箱实际写入与真实 SSH 握手未执行，需按 AD01-T7/T8 在实体设备联调 |
| 2026-09-30 | SSH 多会话与重启清空：离线 Debug 应用/测试 APK 构建成功，93 类 / 669 JVM 用例，0 失败/错误/跳过；`SshTerminalSessionsLayoutTest` 在 `emulator-5558` 1 项通过；中英日 1555 键一致无重复；`git diff --check` 通过 | 6 项 SSH 会话 JVM 用例覆盖重进页面、宿主/终端输入输出尺寸隔离、单独关闭、新应用实例从空列表开始且不重放旧输入、EOF/写失败及连接关闭竞态；持久保存接口与待重连恢复状态已移除。Compose 用例实际移除页面后返回，验证后台输出/草稿保留、新建/切换/发送/确认结束及其它会话存活。此前 5 项键盘布局检查通过，本次仅编译而未重跑。使用假传输，真实宿主/实体设备/长时间后台保活未执行 |
| 2026-09-30 | SSH 输入修复：离线 `:app:assembleDebug :app:testDebugUnitTest :app:assembleDebugAndroidTest` 成功，92 类 / 663 JVM 用例，0 失败/错误/跳过；模拟器 `emulator-5558` instrumentation 的 `SshTerminalKeyboardLayoutTest` 5 项通过；`git diff --check` 通过 | 新增 3 项 JSch 终端用例验证尺寸请求离开调用线程、与输入互斥、尺寸变化后的连续写入/UTF-8 输出；5 项 Compose 用例覆盖手机/平板视口、真实系统 IME 和发送、大字体、已消费边距及草稿。传输用例使用记录请求的 JSch shell，未连接真实 SSH 主机；未操作实体手机 |
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
| BP06-M1 | 未开始 | 未执行 | 未执行 | 未执行 | 未执行 | Mihomo 安装/修复/升级/回滚、订阅/配置/节点与失败诊断 |
| BP06-M2 | 未开始 | 未执行 | 未执行 | 未执行 | 未执行 | 系统代理/TUN、能力门控、管理路径保护、紧急恢复、DNS/GeoData |
| BP02-M2 | 未开始 | 未执行 | 未执行 | 未执行 | 未执行 | 受管 Mihomo 缺失/停止/运行与地址更新；Docker/下载真实代理行为 |
| BP17-M1 | 未开始 | 未执行 | 未执行 | 未执行 | 未执行 | 新领域观察/取消/修复入口、离页/旋转/重连、三语与归属 |

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

- 服务器中心安装：仍需在真机验证文档提供者 ZIP/PFX/PEM 选择、Linux x64/arm64 与 Windows SSH 安装、官网描述符发布、断线恢复及自定义 TLS。Linux 服务端需预装 Python 3。
