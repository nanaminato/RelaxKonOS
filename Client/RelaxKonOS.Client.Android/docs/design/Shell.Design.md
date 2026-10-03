# Android Shell、认证与安全规范

> 当前规范：页面、导航、认证与本机凭据安全。功能范围与验证证据统一见 [当前状态](../status/Progress.md)。
>
> 上位约束（冲突时以上位为准）：
> - [`Product.Design.md`](Product.Design.md) — 移动端产品/技术决策、手机与平板自适应、国际化与主题
> - [`RelaxKonOS.Protocol.md`](../../../../docs/architecture/RelaxKonOS.Protocol.md) — REST / Hub wire contract
> - [`RelaxKonOS.Security.md`](../../../../docs/platform/RelaxKonOS.Security.md) — 权限提升、危险操作确认、风险分级
> - [`RelaxKonOS.PrivilegedOperations.Goal.md`](../../../../docs/platform/RelaxKonOS.PrivilegedOperations.Goal.md) — 宿主提权（capability + target + 5 分钟授权）
> - [`RelaxKonOS.Login.md`](../../../../docs/platform/RelaxKonOS.Login.md) — 登录、已保存连接、错误码矩阵
>
> 实现状态不写入本文，统一记入 [`Progress.md`](../status/Progress.md)。

---

## 1. 定位

Android 客户端采用 **原生 Mobile Shell**：能在手机和平板上登录一台服务器，看到主机状态，操作文件，连上终端，做几类受控的管理操作，并且**把"每次都要手输密码"这件事收敛为指纹一次确认**——同时不放松任何服务端校验。

三条底线：

1. **不做桌面缩小版。** 不迁移 Desktop、Taskbar、Start Menu、WindowManager、`RemoteWindow`、桌面内置应用。
2. **不复制服务端规则。** 页面只消费状态与意图；认证、提权、错误码映射、能力判断都在 Kotlin data layer。
3. **指纹只解封本地保险箱，不产生通行证。** 服务端该验的密码照样验，该发的 5 分钟授权照样只覆盖单一 capability + target。

---

## 2. 能力与权限边界

### 2.1 能力门控

能力可见性由**登录后拿到的 `ServerDescriptorDto.capabilities`** 决定：能力缺失时入口不出现，不用灰色占位。

能力是**部署事实**，回答"这台服务器有没有这个域"。它与**登录身份**是否被允许执行普通操作是两件事：`LoginResponse.executionEligibility`
（`ServerExecutionEligibilityDto`）回答后者，同一台服务器对 root 与对普通账户的答案不同。Linux System Mode 的 root 会话普通 worker 不可用，但 `privilegedFilesAvailable=true` 表示受管文件操作直接走 Helper；不能因此把文件入口判为不可用。Terminal 与 Git 仍须分别判断普通执行资格。`server.files` 存在并不代表当前身份能用它——
入口照常出现；当普通执行和特权文件路由都不可用时，首页先显示服务端给的原因（`ExecutionEligibilityNotice`）。
客户端只消费服务端给的稳定原因码（`ExecutionEligibilityReasons`），文案一律取自本工程自己的 `strings.xml`。

实际可用功能见 [当前状态](../status/Progress.md)。新增功能见 [内置应用补齐计划](../plans/BuiltInParity.md)，不再沿用旧 V1 排除清单。

### 2.2 布局目标与实现状态

下文规定交互与安全约束；平板分栏等布局目标需要按页面核对实现，不能仅因定义了断点就视为完成。设备验收统一见 [验收清单](../status/Verification.md)。

### 2.3 一个必须先讲清的密码域问题

登录标识可能是**宿主系统账户名**，也可能是 **Alias**（见 [`RelaxKonOS.AliasLogin.Goal.md`](../../../../docs/platform/RelaxKonOS.AliasLogin.Goal.md)）。这两个不是同一个密码域：

- **登录凭据**：满足 `/auth/login`。Alias 密码可满足。
- **提权凭据**：满足 `POST /privileged/elevation` 的 `elevation-password-required` 挑战，必须是**宿主 OS 账户密码**，且该账户当前必须是宿主管理员。Alias 密码**永远不能满足**提权挑战（AliasLogin 明文规则：「Alias 密码不能通过任何现有 OS 提权复验」）。

因此必须把两者做成**两个独立保险箱**，不能"登录密码顺便当管理员密码"。§5 全部围绕这一点展开。

---

## 3. 页面清单

路由常量集中在 Kotlin `ui/nav/Routes.kt`，与 `AuthApiRoutes` / `PrivilegedApiRoutes` 一样只定义一次。布局列标明该页在各断点下的形态（Compact `<600dp`、Medium `600–839dp`、Expanded `≥840dp`，沿用 [`LayoutState.kt`](../../app/src/main/java/app/relaxkonos/mobile/core/layout/LayoutState.kt) 的既有实现）。

### 3.1 认证入口（Shell 之外）

| 路由 | 页面 | 作用 | 布局差异 |
| --- | --- | --- | --- |
| `connect/list` | 连接档案列表 | 多服务器选择、编辑、删除单条记录；显示该条是否已保存密码/是否受指纹保护 | 单栏列表；平板为列表 + 详情两栏 |
| `connect/login` | 登录 | 服务器地址、登录标识、密码、记住连接、已保存密码状态、**登录** | 单栏卡片；平板居中卡片 + 连接信息侧栏 |

启动裁决（不是独立路由，是 `AuthSession` 的一个状态）：进程启动后先读本地连接档案；无论是否有档案，均进入同一形态的 `connect/login`。有档案时仅默认选中最近使用项并显示该身份的「已保存密码」状态；密码输入框仍为空且始终可用。实际读取保存密码只在用户点击「登录」、且本次未手动输入密码时发生。完整状态模型与决策见 [`LoginCredentials.Design.md`](LoginCredentials.Design.md)。

密码输入框（登录页与提权对话框共用同一个控件）是**两态**的：默认掩码，点击尾部眼睛图标切成明文并**保持在明文**，再点一次回到掩码。它不是"按住才可见"的手势——长密码需要能看清，而不是靠按住按键维持。可见性只是展示状态（`rememberSaveable`），不写入会话、保险箱或任何文件；密码本身仍按 §5.3.2 的规则以 `CharArray` 承载并在请求结束后清零。

### 3.2 Shell 顶级目的地

顶级导航固定五项，对齐 [`Product.Design.md`](Product.Design.md) §5.1：**主页 / 文件 / 终端 / 管理 / 更多**。

| 路由 | 页面 | Compact | Medium | Expanded |
| --- | --- | --- | --- | --- |
| `home` | 首页 | 单栏状态卡 | 单栏 + 侧栏 | 状态卡 + 趋势 + 近期操作三区 |
| `files` | 文件 | 单栏目录列表 | 列表优先 | 位置栏 + 列表 + 详情/预览三栏 |
| `terminal` | 终端 | 单会话全屏 + 扩展键栏 | 会话抽屉 + 终端 | 会话列表 + 终端双栏 |
| `manage` | 管理 | 能力域列表 | rail + 列表 | 能力域列表 + 内容区 |
| `more` | 更多 | 设置项列表 | 列表 | 列表 + 详情两栏 |

### 3.3 嵌套页面

实际路由以 [`Routes.kt`](../../app/src/main/java/app/relaxkonos/mobile/ui/nav/Routes.kt) 为准。资源 ID 和路径由页面状态持有，不放入路由字符串；详情不另建虚构的 `/{id}` 或查询参数路由。

| 路由 | 页面 |
| --- | --- |
| `files/detail` | 文件详情与预览 |
| `manage/monitor`、`manage/processes` | 性能与进程 |
| `manage/deployments`、`manage/deployments/detail` | 应用部署列表与详情 |
| `manage/docker` | Docker 与 Compose |
| `manage/git` | Git 编辑与构建 |
| `manage/websites` | Nginx、站点管理与网站发布 |
| `manage/certificates` | 独立证书管理与任务恢复 |
| `manage/tunnels` | FRP 客户端/frps、运行时与同步请求核实 |
| `manage/guardian`、`manage/scripts` | 进程守护与脚本任务 |
| `manage/operations` | 任务与恢复 |
| `more/connections`、`more/server-information` | 连接与服务器信息 |
| `more/network` | 宿主出站代理；按 `server.docker` 门控，Docker 页面共用入口 |
| `more/account-security` | 账户与安全 |
| `more/appearance`、`more/diagnostics`、`more/about` | 外观、诊断、关于 |

首页不展示服务器能力清单，也不提供与顶级「文件」导航重复的「打开文件」按钮。「更多 → 服务器信息」读取当前会话的服务器描述，以本地化名称展示支持的功能；这些名称只说明服务器上报的支持，不承诺手机端已有操作入口或当前账户有权限。未知能力以附加功能数量提示，所有原始标识仅在用户展开「技术详情」后展示，并可选择复制。该页在 Compact/Medium 作为可返回的独立页面，Expanded 复用更多页的详情栏。

### 3.4 全局叠加层（不属于导航图）

操作反馈通过完整 `UiMessage` 传递文本与 `StatusTone`，全局提示和页面提示共用 `ActionFeedback`；调用处不得先转成字符串再丢弃状态。成功、普通信息使用页面内提示，失败和警告使用可关闭对话框。`OperationMessageDialog` 的标题按状态分别为失败、警告、成功或提示；正文相同但状态改变仍属于新反馈。登录/连接成功但未保存密码属于警告，保存设置成功属于成功，目录版本已经是当前版本属于普通信息。

| 叠加层 | 触发 | 约束 |
| --- | --- | --- |
| 提权对话框 | 收到 `403 elevation-required` / `elevation-password-required` | 显示 capability 与**规范化目标**、账户名、`使用指纹确认` 与 `输入密码`；不做成底部 Sheet（避免误触绕过） |
| 危险操作确认 | 删除、停止/重启服务、部署、关闭终端 | 确认文本必须含具体目标（[`RelaxKonOS.Security.md`](../../../../docs/platform/RelaxKonOS.Security.md) §7） |
| 操作错误／警告弹窗 | 点击操作后产生的拒绝、网络失败或结果未知 | 显示本地化文案、关闭与可用的重试入口；编辑器上方显示，关闭保留草稿和未决事实 |
| 进度 Sheet | 上传/下载/长操作 | 可折叠，不阻塞导航 |

选择控件（Checkbox、RadioButton、Switch）与同一行文字统一垂直居中，多行标签以整段文字的中心对齐，不使用文字顶部 padding 补偿。操作失败与警告使用 `OperationMessageDialog`；相同操作再次失败可再次提示，关闭后普通重组不重复提示。成功反馈保持页内。字段有效性、能力说明、会话限制和操作历史中的诊断属于持续状态，继续在相关内容处展示。

**「不再提醒」的适用范围**（`data/NoticePreferenceStore.kt`）

只有**结论由本机能力决定、且再试一次不会改变**的提醒才提供「不再提醒」：目前是本机结构上无法保存密码（`ReminderKind.LoginCredentialNotSavedDevice` / `ServerCenterCredentialNotSaved`）和本机无法用指纹或锁屏解封（`ReminderKind.SavedPasswordUnavailable`）。勾选框与「知道了」是同一次动作——勾上并关闭即生效，不要求第二次确认。提权对话框里同一句解锁结论提供**同一个**勾选（同一句结论 = 同一条键，所以「账户与安全」里只有一条可恢复项）；提权对话框自己的「管理员密码未保存」那句**不提供**，因为它只由用户自己取消指纹确认产生（§5.4 第 2 条）。

下列情形**一律不提供**，因为那正是用户需要看到、而且往往需要采取行动的内容：

| 情形 | 为什么不静音 |
| --- | --- |
| 用户自己取消了指纹确认 | 下次仍可能想保存 |
| 生物识别锁定 / 密钥失效 / 被篡改 | 用户有补救动作（先解锁设备、重新保存一次） |
| 网络失败、服务端拒绝、结果未知 | 属于本次操作的结果，重试必须能再次显示 |
| debug 构建的明文保存提示 | `LoginCredentials.Design.md` §8.1 要求界面必须讲清未加密；其它四处（勾选框提示、状态行、连接列表、安全页）继续保留，不靠这一句弹窗承担 |
| `ExecutionEligibilityNotice` 会话限制 | 是会话事实，不是可以关掉的通知 |

静音在**源头**生效：`AppContainer.showNotice` 对已静音的消息直接返回，被 ViewModel 持有的消息由 `ActionFeedback` 在拿到偏好时跳过渲染。二者共用同一份 `NoticePreferenceStore`，所以「不再提醒」不是把弹窗闪没了而已。唯一的例外是提权对话框：那里的句子同时是「授权尚未生效，再按一次「授权」可在不保存的前提下继续」的唯一指示（§5.4 第 2 条），所以静音只撤掉勾选、不吞掉句子——否则按下「授权」会看起来毫无反应。

偏好只记**键**，不含文案、账号、服务器地址或密文；只影响本机，随卸载消失，不随系统备份迁移。恢复入口在「更多 → 账户与安全 → 提醒」，已静音的提醒在那里逐条列出并可关闭静音——「不再提醒」必须是可逆的。

---

## 4. 界面流转

### 4.1 导航图

```text
connect/login ─ 认证成功 ─ MobileShell
                            ├─ home：身份与状态
                            ├─ files：目录 → files/detail；上传卡片
                            ├─ terminal：会话/输入/输出（页面内部状态）
                            ├─ manage：docker / deployments / websites / certificates / tunnels / git
                            │          monitor / processes / guardian / scripts / operations
                            └─ more：connections / account-security / server-information
                                      appearance / diagnostics / about
```

图中 manage/more 子项使用各自完整路由（见 §3.3）；会话和上传卡片不是额外路由。

三条流转规则：

1. **顶级目的地互不压栈。** 在 `files` 里进了详情再点 `terminal`，切回 `files` 时应回到该目的地自己的栈顶（`MobileNavigator` 为每个目的地保留独立栈），而不是被重置到列表——平板上尤其明显。
2. **同一目的地在不同断点下用不同承载方式。** Expanded 下 `files/detail` 渲染为右栏而不是入栈页面，系统返回键因此不"返回"到列表，而是直接退出该目的地（与平板预期一致）。
3. **登出是清栈操作。** 登出先 `POST /auth/logout` 吊销 refresh token，再清空整个 back stack 到 `connect/login`，并保留连接档案（对齐桌面「退出远程桌面只注销会话，不清除已保存连接」）。

系统返回键顺序（沿用 Mobile.Design §6）：**关闭叠加层 → 返回详情/列表 → 退出当前导航层 → 交给系统**。

### 4.2 连接失效与重连流转

```text
任意页面
 │
 ├─ 网络错误 ─────► 错误弹窗 + 就地重试；不清会话、不动凭据
 │
 ├─ 401（access 过期）─► 静默刷新一次 → 重试原请求
 │
 ├─ refresh 被明确拒绝 ─► 清会话 ─► connect/login（凭据仍在，可指纹再登录）
 │
 └─ 后台/进程回收 ─► 回前台重建：先刷新 token，再按当前路由重建页面与 Hub 订阅
```

服务端重启会让 refresh token 失效（[`RelaxKonOS.Login.md`](../../../../docs/platform/RelaxKonOS.Login.md) §7），此时属于「refresh 被明确拒绝」，走回登录页但**不删凭据**。

### 4.3 提权流转

```text
用户点击受保护操作（如删除 /etc 下的文件）
        │
        ▼
文件 API → 403 elevation-required
        │
        ▼
ElevationRepository：本 jti 下 (capability, target) 是否已有有效授权？
        │ 否
        ▼
提权对话框（显示 capability + target + 账户名）
        │
        ├─ 指纹确认 ─► 解封管理员密码保险箱 ─┐
        │                                    │
        └─ 手动输入密码 ─────────────────────┤
                                             ▼
                       POST /api/v1.0/privileged/elevation
                       { capability, target, password, administratorUsername }
                                             │
                              elevated: true, expiresAt（5 分钟）
                                             ▼
                              原操作单次重试 → 成功
```

**必须写进实现的三个约束：**

- 授权由服务端绑定到**当前 access token 的 `jti`** + capability + target，TTL 固定 5 分钟。客户端不得把"已提权"当成会话级状态。
- access token 刷新后 `jti` 变化，**旧授权立即失效**。因此提权流程中不要并发触发 token 刷新；若刷新已发生，按授权失效处理并重新走提权（此时可直接复用指纹，用户不必重新输密码）。
- 客户端只做"一次安全重试"，不做循环重试。这与桌面 `HostElevationBroker` 的语义一致，移动端必须复用同一语义而不是另发明一套。

---

## 5. 指纹解锁已保存凭据（核心需求）

> 本章规定凭据保险箱的安全约束与解锁机制。**登录决策、密码输入框与本地凭据状态模型**（何时用手动密码、何时读保存密码、何时允许删记录）另见 [`LoginCredentials.Design.md`](LoginCredentials.Design.md)。两文冲突时，本章 §5.3「不可变安全约束」优先。

### 5.1 需求分解

| 编号 | 需求 | 交付物 |
| --- | --- | --- |
| R1 | 指纹解锁**已保存的服务器密码**，免手输登录 | 连接凭据保险箱 + 登录页指纹按钮 |
| R2 | 指纹解锁**已保存的管理员密码**，免手输完成提权 | 提权凭据保险箱 + 提权对话框指纹按钮 |
| R3 | 指纹不可用/失败时不能把人锁在外面 | 手输回退、记录保留、失败分类 |
| R4 | 指纹能力不支持的设备也不能骗用户 | 能力探测 + 入口不出现 |

### 5.2 两个保险箱

| | 连接保险箱（Connection Vault） | 提权保险箱（Elevation Vault） |
| --- | --- | --- |
| 记录键 | `serviceId + 登录标识` | `serviceId + 宿主管理员账户名` |
| 载荷 | 登录密码 | 管理员密码 |
| 解锁时机 | 登录页点击「登录」且密码框为空时 | 提权对话框点击「使用指纹确认」 |
| 用途 | `POST /auth/login` | `POST /privileged/elevation` |
| 指纹强度要求 | `BIOMETRIC_STRONG` 优先；不满足时可用设备凭据（见 §5.6） | **只接受 `BIOMETRIC_STRONG` 按次授权**；不满足则不保存 |
| 保存前提 | 用户显式勾选「记住此服务器的登录凭据」 | 用户在提权对话框显式勾选「用指纹保存此管理员密码」 |
| 清除时机 | 用户显式「忘记密码」或「删除登录记录」 | 用户显式删除、密码被服务端拒绝 |

**为什么提权保险箱更严格。** 提权密码一行就能改变宿主机状态（文件删除、服务启停、部署、宿主时区/主机名），且服务端只给 5 分钟、单 capability 的窗口。允许它被「PIN 解锁的长期密钥」保护，等于把设备 PIN 的强度降级为宿主管理员强度。宁可让用户每次手输，也不降级。

**密钥永久失效不再删除记录（2026-09-23 修订）。** 用户新录入指纹会令 Keystore 密钥永久失效。两个保险箱的处置统一改为**标记作废、保留记录与密文、禁止读取**：一个保险箱共用一个 Keystore alias，所以 alias 失效时该保险箱的全部记录都必须标记作废。用户手动登录成功并明确选择保存时，客户端删除失效 alias、创建新 alias、再次请求授权，只重新密封当前身份；其他旧记录保持作废。只有用户显式的「忘记密码」或「删除登录记录」才会删掉记录本身。生物识别链路上的任何失败——取消、不匹配、暂时锁定、密钥失效——都不自动丢弃用户保存过的凭据。见 [`LoginCredentials.Design.md`](LoginCredentials.Design.md) §7.4（D5）。

### 5.3 不可变安全约束

1. **指纹不产生任何服务端凭据。** 指理解封的只是本地密文；`/auth/login` 与 `/privileged/elevation` 每次仍由服务端用 `IIdentityProvider` 重新验证密码。
2. **服务器不存储密码**（既有原则，不变）。Android 端明文也只在内存中短暂存在：用 `ByteArray`/`CharArray` 承载，提交后立即清零；不进入 `String` 常量池、不进入日志、崩溃报告、分析事件或诊断导出。
3. **不保存 RefreshToken。** 沿用当前跨端规则：iOS/Android 均只在内存中持有 token。
4. **密文与元数据分离。** 密文写入 `noBackupFilesDir`（`allowBackup="false"` 已设置）；Keystore 只保存密钥，不保存数据。
5. **AAD 绑定记录身份。** AES-GCM 的附加认证数据绑定 `vault | serviceId | account`，防止把 A 服务身份的密文挪到 B 服务身份条目下复用；临时隧道端口不参与 AAD。
6. **不做跨设备迁移。** 不导出、不云同步、不随系统备份恢复。卸载即失效（Keystore 密钥随应用卸载销毁）。
7. **首次保存必须先验证一次指纹。** 保存动作本身要过一次 `BiometricPrompt`，确保密钥确实受用户生物特征保护、且用户当场能通过；不允许"先存着，等用的时候再说"。
8. **标准用户禁止静默认证。** StandardUser 的受保护操作必须由用户在这次交互中确认管理员凭据（指纹或输密码），不做"失败自动弹指纹"的后台循环。Linux HostAdministrator 的文件操作遇到结构化 `AccessDenied` 时可由服务端自动路由 Helper；HostRoot 文件操作直接走 Helper，均不要求客户端保存或发送密码。

### 5.4 Android 实现

```text
Keystore                     →  Keystore 之外
┌──────────────────────┐        ┌────────────────────────────────┐
│ AES-256 密钥          │        │ CredentialVault (noBackupDir)   │
│ alias:                │  加解密 │  connection[]: iv + ciphertext  │
│  rk.connection.vault  │◄──────►│  elevation[]:  iv + ciphertext  │
│  rk.elevation.vault   │        │  记录键: serviceId + account    │
│ setUserAuthentication │        └────────────────────────────────┘
│  Required(true)       │
│ setInvalidatedBy      │        BiometricPrompt(cryptoObject)
│  BiometricEnrollment  │◄──────  解锁只对本次 Cipher 生效
└──────────────────────┘
```

**密钥参数**

| 参数 | 值 | 说明 |
| --- | --- | --- |
| 算法 | `AES/GCM/NoPadding`，256 位 | GCM 自带完整性校验，IV 每条记录随机且随密文存储 |
| `setUserAuthenticationRequired` | `true` | 无用户认证不可使用密钥 |
| `setUserAuthenticationParameters(0, AUTH_BIOMETRIC_STRONG)` | API 30+ | `0` = **每次使用都要求认证**（auth-per-use） |
| `setUserAuthenticationValidityDurationSeconds(-1)` | API 23–29 | 等价的按次授权语义 |
| `setInvalidatedByBiometricEnrollment` | `true` | 用户新录入指纹即作废密钥（安全默认） |
| `setIsStrongBoxBacked` | API 28+，运行时探测 | StrongBox 不可用时回退 TEE，不因硬件差异禁用功能 |
| `setUnlockedDeviceRequired` | API 28+，可选 | 设备锁定时密钥不可用，进一步收窄攻击面 |

**解锁调用**：生成/取得 `Cipher` 后包成 `BiometricPrompt.CryptoObject(cipher)` 交给 `authenticate()`，只有认证成功回调里拿到的 `CryptoObject.cipher` 才能解密该次记录。这是「密钥材料永不出 Keystore、且绑定到单次认证」的标准做法。

**失败分类与处理**

| `BiometricPrompt` 结果 | 处理 |
| --- | --- |
| `ERROR_USER_CANCELED` / `ERROR_NEGATIVE_BUTTON` | 静默回退到密码输入，不报错、不改变记录 |
| `ERROR_LOCKOUT`（暂时锁定） | 提示稍后重试或改用密码输入 |
| `ERROR_LOCKOUT_PERMANENT` | 引导用设备凭据解锁设备后重试，或直接用密码输入 |
| `KeyPermanentlyInvalidatedException` | 把该保险箱中**由同一 alias 保护的全部记录标记为作废并保留密文，不删除记录**，保留服务器与账户，提示重新输入；用户之后手动登录成功且明确保存时轮换 alias，只重新保存当前记录（见 §5.2 的 2026-09-23 修订） |
| 设备无强生物识别 / 未录入 | 入口不出现（R4），走密码输入 |

**排障**：`AndroidKeyStore` 的行为无法在 JVM 单测里复现，而上面这四类拒绝在界面上各只有一句话，因此
`security/VaultDiagnostics.kt` 提供 debug-only 的可过滤日志（tag `RelaxKonVault`，`adb logcat -s RelaxKonVault:D`）：
能力探测的原始 `canAuthenticate` 码、解锁模式裁决、密钥创建与 provider（StrongBox / TEE）选择、alias 存在性、
`BiometricPrompt` 的结果码与返回文本，以及 Keystore 异常被映射前的原始类型与消息。三类失败必须始终可区分——
「设备不满足策略」「alias 不存在或从未创建」「密钥已作废」——因为只有最后一类才允许标记记录作废。日志只含
保险箱种类、provider 决策、异常类与结果枚举，不含密码、账户、服务器地址、`Cipher` 或密钥材料；release 构建
不安装 sink，`security` 包因此从不触碰 `android.util.Log`。

**实现期确认的两处平台约束**

1. **Keystore 没有"弱生物识别"标志位。** `KeyProperties` 只提供 `AUTH_BIOMETRIC_STRONG` 与
   `AUTH_DEVICE_CREDENTIAL`，因此 D3 的时间窗密钥（`DeviceUnlockWindow`）只能声明
   `AUTH_DEVICE_CREDENTIAL or AUTH_BIOMETRIC_STRONG`：强生物识别设备上两者皆可解封，`WEAK_ONLY` 设备上只有
   设备凭据可以。`BiometricPrompt` 提供的认证方式必须与密钥接受的集合同步，否则会出现"提示框成功、解密失败"
   的错配——必须归类为解锁失败并保留用户记录，不能把它当成篡改后自动删除。
   若设备只有弱生物识别且从未设置锁屏，窗口密钥无法创建：`VaultKeyManager` 抛 `VaultKeyUnavailableException`，
   `VaultAccess` 归类为 `UnlockFailure.Unavailable`，界面回退到输入密码，且**不删除**任何记录
   （"无法使用"不是"已失效"的证据）。

2. **保存管理员密码失败不取消提权。** 勾选「在本机保存管理员密码」后若这次保存未成功（用户取消指纹、
   保险箱不可用或密钥失效），提权对话框保持打开、勾选被清除并显示原因；用户再按一次「授权」即在**不保存**的
   前提下继续提权。用户显式提出的保存请求不能被静默丢弃，但一次便利性失败也不应中断他已经发起的操作。
   三个原因的措辞与处置各不相同：「用户取消指纹确认」用警告档、不给「不再提醒」（是他自己的动作；
   与登录、服务器中心同一句话同一档）；「本机无法用指纹或锁屏解封」用失败档并给出
   `ReminderKind.SavedPasswordUnavailable` 勾选（这是设备结论，同一句在登录与服务器中心也能关掉）；
   锁定、密钥失效、被篡改同样用失败档但不给勾选（用户还有补救动作）。

### 5.5 交互流程

**首次保存登录凭据**

```text
登录成功
   │
   ▼
「记住此服务器的登录凭据？」
   ├─ 否 ─► 只保存 serviceId + 登录标识（下次回填账户，仍需输入密码）
   └─ 是 ─► 能力探测
              ├─ 支持强生物识别 ─► BiometricPrompt 验证一次
              │                     ├─ 通过 ─► 写入连接保险箱，标记「指纹保护」
              │                     └─ 取消/失败 ─► 不保存密码，只保存 serviceId + 标识
              └─ 不支持 ────────► 明确告知「此设备不支持指纹，未保存密码」
```

**登录（统一决策）**

```text
connect/login（统一表单；密码框 value 始终只表示本次手动输入）
   用户点击 [登录]
        │
        ├─ PasswordText 非空 ─► 直接使用本次手动密码
        ├─ PasswordText 为空且有 SavedCredential ─► BiometricPrompt → 临时解封密码
        └─ PasswordText 为空且无 SavedCredential ─► 聚焦密码框，不发请求
                                      │
                                      ▼
   POST /auth/login { identifier, password, clientPlatform: "android", deviceName, clientVersion }
                                      │
        ├─ 200 ─► 拉取 ServerDescriptorDto ─► 如勾选保存才写入/更新凭据 ─► MobileShell
        └─ 任意失败 ─► 立即清除本次明文；不删除、不覆盖已保存凭据
```

生物识别取消、失败、锁定或密钥失效时均不读取或删除保存密码；密码框保持为空，用户仍可手动输入或重新尝试。密钥永久失效的记录标记为作废并保留记录，详见 [`LoginCredentials.Design.md`](LoginCredentials.Design.md) §7。

**指纹提权**

```text
提权对话框
   显示：capability 的人类可读名、规范化目标、管理员账户名（可编辑）
   按钮：[使用指纹确认]（有已保存管理员密码时）/ [输入密码]
        │
        ▼ 指纹 → 解出管理员密码
   POST /privileged/elevation { capability, target, password, administratorUsername }
        │
        ├─ elevated: true ─► 原操作单次重试
        ├─ elevation-password-invalid ─► 删除该条密码，提示重新输入
        ├─ elevation-account-not-administrator ─► 同样删除该条密码
        │      提示「该账户当前不是宿主管理员，请修正账户权限后重新输入」
        └─ 网络错误 / 超时 / 5xx ─► 不删除（未取得判定结论，不构成失效证据）
```

**账户与安全页**（`more/account-security`）

- 两个分区：*服务器登录凭据* / *管理员凭据*，各自列出条目（服务器、账户、最后使用时间、是否受指纹保护）。
- 每条：删除；分区级：全部清除。
- 指纹总开关：关闭时清除两个保险箱的密码并禁用指纹入口（保留服务器与账户记录）；重新开启需要验证一次指纹。
- 页面顶部固定说明："指纹只用于解锁保存在本机的密码。服务器仍会独立校验每次登录与管理员认证。"

### 5.6 能力探测与降级

`BiometricCapability` 探测结果只有四种，直接决定 UI 形态，不使用模糊的"可能可用"：

| 探测结果 | 判据 | 连接保险箱 | 提权保险箱 |
| --- | --- | --- | --- |
| `STRONG` | `BiometricManager.canAuthenticate(BIOMETRIC_STRONG)` 通过 | 启用 | 启用 |
| `WEAK_ONLY` | 仅 `BIOMETRIC_WEAK`（如部分 2D 人脸）可用 | 启用，但设置页必须标注强度较低 | 不启用 |
| `DEVICE_CREDENTIAL_ONLY` | 设备有 PIN/图案/密码，无生物识别 | 默认关闭，由用户在设置中显式开启 | 不启用 |
| `NONE` | 无锁屏 | 不启用 | 不启用 |

上方"启用/不启用"为已确认的产品决策（§5.8 D2、D3），不是实现期的可选项。

**API 版本策略。** 当前 `minSdk = 23`。`androidx.biometric` 兼容库可在 API 23+ 提供 `BiometricPrompt`，但「强生物识别 + `CryptoObject` 按次授权」在 API < 28 的可用性依机型而异。因此：

- 安装门槛保持 `minSdk 23`，但**指纹功能按探测结果开启**，不做"设备旧就一定没有"的硬编码假设。
- `DEVICE_CREDENTIAL_ONLY` 一旦启用连接保险箱，密钥必须退化为**时间窗授权**（`setUserAuthenticationValidityDurationSeconds(300)`），因为设备凭据无法与 `CryptoObject` 按次绑定。这个降级**只允许用在连接保险箱**，且必须在 UI 上说明（"设备解锁后 5 分钟内可自动填充登录密码"）。

### 5.7 与现有契约的关系

**本设计不需要修改 Protocol、Server 或数据库。** 指纹是纯客户端本地能力：

| 关注点 | 现状 | 结论 |
| --- | --- | --- |
| 登录请求 | `LoginRequest { identifier, password, clientPlatform, deviceName, clientVersion }` | 已足够；`clientPlatform` 为 `ClientPlatformKind.Android`（M0 已完成语义拆分） |
| 提权请求 | `HostElevationRequest { capability, target, password?, administratorUsername?, includeDescendants }` | 已含 `password` 与 `administratorUsername`，无需扩展 |
| 文件提权请求 | `FileElevationRequest { path, password?, relatedPaths?, includeDescendants, capability?, administratorUsername? }` | 同上 |
| 服务端信任模型 | 密码只在验证瞬间持有，不落库/不日志 | 不因移动端保存密码而改变 |
| 能力门控 | `ServerDescriptorDto.capabilities` + `ServerCapabilities` 常量 | 直接复用，不新增发现端点 |

桌面端与移动端的手动认证均发送 `administratorUsername`；不按平台猜测 root/Administrator，未知候选时账户留空。移动端仅建议当前服务器保存的管理员账户，其密码仍需显式授权释放。Linux 可使用经固定 Helper 的 root sudoers 策略认可的非 root 管理员账户及其自身密码。系统账户认证的管理员无需重复密码，资格由 Server 每次重新检查；Alias 需显式宿主管理员认证。移动端保存管理员账户与密码时继续把账户名作为保险箱记录键。

### 5.8 决策记录

2026-09-22 已确认（对应 PrivilegedOperations 的决策门文化）：

| 编号 | 决策 | 结论 | 落点 |
| --- | --- | --- | --- |
| D1 | 是否允许在客户端保存宿主管理员密码 | **允许** | 见下方 §5.8.1；已在 [`RelaxKonOS.Security.md`](../../../../docs/platform/RelaxKonOS.Security.md) §5.1 登记为受限例外 |
| D2 | `WEAK_ONLY` 设备是否允许开启连接保险箱 | **允许**，仅限连接保险箱；设置页必须标注强度较低 | §5.6 |
| D3 | `DEVICE_CREDENTIAL_ONLY` 的时间窗降级 | **接受**，仅限连接保险箱，5 分钟窗口 | §5.6 |
| D4 | 服务端拒绝提权时如何处置已保存的密码 | **一律删除**，不按拒绝原因区分 | §5.5、§5.8.2 |

#### 5.8.1 D1：客户端保存管理员密码的受限例外

桌面端**不保存**管理员密码——每次提权都现场输入。移动端引入"指纹保存管理员密码"是**新增能力**，因此它不是实现细节，而是一条显式的、有边界的例外。允许的理由：移动端键盘输入长密码体验差、易被肩窥；配合 Keystore + 强生物识别按次授权 + 记录级可删除 + 不跨设备迁移，风险增量可控。

例外边界（全部为硬约束，违反任一条即视为实现缺陷）：

- 只允许保存在 Android Keystore 保护的客户端保险箱内；**服务端**仍不落库、不日志、不审计、不缓存，密码只在验证瞬间持有。
- 只允许由 `BIOMETRIC_STRONG` + `CryptoObject` 按次授权解封。`WEAK_ONLY` 与 `DEVICE_CREDENTIAL_ONLY` 设备**不得**保存管理员密码（只允许保存登录密码）。
- 保存必须由用户在提权对话框中显式勾选，并当场通过一次指纹验证；不得默认勾选、不得静默保存。
- 记录必须可按条删除；账户与安全页可见、可清空。任何一次服务端拒绝提权的响应都立即丢弃对应记录（§5.8.2）。
- 不得随系统备份、云同步或跨设备迁移；`allowBackup="false"` 保持不动。
- 不得因"已保存"而跳过任何服务端校验或危险操作确认。

#### 5.8.2 D4：提权失败一律丢弃已保存的密码

规则只有一条：**服务端没有返回 `elevated: true`，就删除该条保存的管理员密码。** 不按错误码分支。

| 服务端响应 | 实际含义 | 处理 |
| --- | --- | --- |
| `elevation-password-invalid` | 密码本身是错的（用户改了密码，或记录已过期） | 删除该条保存的密码，保留服务器与账户，提示重新输入 |
| `elevation-account-not-administrator` | 密码**可能完全正确**，是该账户当前不具备宿主管理员资格 | **同样删除**该条保存的密码，提示"该账户当前不是宿主管理员，请修正账户权限后重新输入" |
| 网络错误 / 超时 / 5xx | 未取得判定结论 | **不删除**——服务端并未拒绝这次凭据，不构成失效证据 |

已知代价：若管理员只是把账户从 sudoers / Administrators 移出，用户修好权限后仍需重新输入一次密码；而且这个提示必须写清楚原因，否则用户会误判为"密码记错了"。

接受该代价换取的性质：**凭据只要被服务端拒绝过一次，就不再留在设备上**，且实现上不需要按错误码分支，也就不存在"某条拒绝路径忘了删"的漏点。唯一不触发删除的是"没有结论"的错误（网络失败、超时、5xx）——把"未取得结论"也当作失效证据，会让一次离线或服务端抖动直接清掉用户保存的密码。

---

## 6. 实现职责

`AppContainer` 组合应用级会话、保险箱、上传及领域仓库；`AuthSession` 管理登录、刷新与注销；`MobileNavigator` 保存各顶级目的地的导航栈。平台安全由 `security/` 实现，SSH 工作区使用独立 `servercenter/` 域，页面由 `ui/` 内对应 ViewModel 和 Compose 控件承载。

不再维护逐阶段的“待新增文件”清单。具体领域行为见 [文档目录](../README.md)，依赖和 Manifest 以源码为准。`allowBackup="false"`：凭据密文不得随系统备份跨设备迁移。

## 7. 验证

认证、凭据、授权和导航的自动化证据见 [当前状态](../status/Progress.md)。指纹、锁屏窗口、软键盘、旋转与后台恢复的设备矩阵见 [验收清单](../status/Verification.md)。

### 操作与运行状态反馈

- 通用刷新、重试、保存、删除按钮通过 `ActionLabel` 展示图标与本地化文字，继续使用 Material 按钮的触控区域；图标为装饰，不重复朗读文字。
- 请求加载通过 `ActivityIndicator` 展示转圈与当前加载说明，保留现有内容。已有阶段/字节进度条继续显示实际进度，不伪造百分比。
- 任务通过 `ExecutionStatusChip` 展示状态：排队、运行、取消中等进行态显示转圈；成功、失败、中断分别用勾、叉、警告图标。只有已核实的状态才用于展示任务进度，未知状态不显示为成功。
- 持续运行的守护服务、代理、FRP 和 SSH 转发使用静态播放标记，启动/停止过程使用转圈，避免把正常驻留服务画成持续加载。
- `StatusChip` 默认提供与状态色调一致的图标，调用方可提供更具体的图标。状态始终保留文字，兼顾色觉差异、深色主题与屏幕阅读器。
- 脚本页面请求期间禁用刷新、新建与取消按钮，防止重复提交。图标不替代既有操作确认、权限判断及核实流程。

## 8. AI Agent 实施规则

**必须**

- 新功能先确认 Protocol 契约是否已存在；本设计不假设任何需要新增的端点。
- 指纹只做"解封本地密文"，不得演化为任何形式的服务端免验证凭据。
- 提权流程严格保持 `capability + target + jti + 5 分钟` 语义，客户端只做一次安全重试。
- 页面通过 ViewModel 消费状态与意图；认证、提权、HTTP、能力判断放在 data layer。
- 密码明文在内存中用可变字节序列承载并在提交后清零。
- 文本走 Android resource，不出现用户可见字符串字面量；使用逻辑方向 `start`/`end`。

**禁止**

- 把管理员密码与登录密码存进同一个保险箱、共用同一条记录或互相回填。
- 在 UI 中显示 capability 原始枚举名、`type` URI 或服务端英文 detail。
- 把已保存密码写入日志、诊断导出、崩溃报告、分析事件或 `String` 常量。
- 保存 RefreshToken 或任何可替代密码的令牌。
- 在提权流程中并发触发 token 刷新。
- 用 `DEVICE_CREDENTIAL` 的长期密钥保护提权保险箱。
- 因桌面端已有某个应用就为移动端补一个不可用入口。
