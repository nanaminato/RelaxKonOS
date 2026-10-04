# RelaxKonOS Android 登录与本地凭据设计

> **状态：规范。** 本文规定 Android 端「登录身份 + 本地保存凭据」的状态模型与决策规则；实现不得以旧的简洁模式、隐藏密码框或自动删除凭据绕开这些规则。
>
> 上位约束（冲突时以上位为准）：
> - [`Shell.Design.md`](Shell.Design.md) — Shell 能力门控、页面清单、两个凭据保险箱的安全约束（§5.3 不可变）
> - [`Product.Design.md`](Product.Design.md) — 移动端产品与技术决策
> - [`RelaxKonOS.Login.md`](../../../../docs/platform/RelaxKonOS.Login.md) — 登录、已保存连接、错误码矩阵
> - [`RelaxKonOS.Security.md`](../../../../docs/platform/RelaxKonOS.Security.md) — 提权、危险操作确认
>
> 实现状态不写入本文，统一记入 [`Progress.md`](../status/Progress.md)。

---

## 1. 范围

本文只回答一个问题：**用户站在登录页上，点击「连接」的那一刻，应该发生什么。**

覆盖：

- 登录身份的定义与唯一键；
- 本地保存了什么、没保存什么；
- 密码输入框、「已保存密码」、手动密码三者之间的关系；
- 生物识别在这条链路里的职责边界；
- 切换账号、密码被服务端拒绝、密钥失效、忘记密码、删除登录记录各自做什么；
- 明文密码从解封到释放的完整生命周期。

不覆盖：`/auth/*` 的 wire contract（见 [`RelaxKonOS.Protocol.md`](../../../../docs/architecture/RelaxKonOS.Protocol.md)）、token 刷新与重连状态机（见 Shell §4.2）、提权凭据的产品规则（见 Shell §5.8）；提权保险箱仅在 §7.5 沿用同一处置规则。

---

## 2. 概念模型

整个登录系统由**四个互相独立的概念**组成。它们各自有独立的真源，**不允许互相推导或合并**。

| 概念 | 回答的问题 | 真源 | 生命周期 |
| --- | --- | --- | --- |
| **SelectedLogin** | 现在准备以哪个身份登录？ | 界面状态 + 连接档案 | 用户改字段 / 选档案 |
| **SavedCredential** | 这个身份在本机保存过一个可用的密码吗？ | `CredentialVault`（`noBackupFilesDir` 密文） | 登录成功并勾选保存 / 用户显式忘记密码或删除登录记录 / 用户显式关闭凭据保存 |
| **Biometric** | 现在有权限读取那个密码吗？ | `BiometricPrompt` + Keystore 密钥策略 | 每次读取时重新裁定 |
| **PasswordText** | 用户本次是否明确输入了一个新密码？ | `OutlinedTextField` 的 `value` | 用户键入 / 提交后立即清空 |

### 2.1 身份唯一键

身份键是 **`(serviceId, 登录标识)`**。`serviceId` 是本机稳定服务器身份，不等同于每次请求所用的地址。

```text
连接A + root      ─┐
连接A + nanami     ├─ 三条完全独立的登录配置，各自拥有独立的凭据状态
连接B + nanami    ─┘
```

- `Username` 是协议里的 **`identifier`**（[`LoginRequest`](../../../../Shared/RelaxKonOS.Protocol/Identity/LoginRequest.cs) 的字段名）。`ServiceId` 在直连时是规范化的持久服务器 URL，在服务器中心管理的 SSH 隧道连接中则是经过核实的安装 ID；登录页普通 SSH 隧道使用独立的 `ssh-tunnel:` 配置身份，详见 [登录页 SSH 隧道](../features/LoginSshTunnel.md)。
- 本机稳定身份：`loginId(serviceId, identifier)`。保险箱和登录档案只使用这个身份；临时 `http://127.0.0.1:<port>` 不得写入其中。
- 直连 URL 归一化会小写 scheme 与 host、去除默认端口和结尾斜杠、丢弃 query / fragment，同时保留可能区分大小写的 path。`identifier` 只去首尾空白，**不折叠大小写**。
- 本次请求地址是 `effectiveBaseUrl`。直连时它等于 URL 型 `serviceId`；隧道时它可以随重连换端口，但 `serviceId` 不变。
- 与保存凭据的连接键：`CredentialKey` = `recordId(VaultKind.Connection, serviceId, identifier)`，即 [`CredentialVault.kt`](../../app/src/main/java/app/relaxkonos/mobile/security/CredentialVault.kt) 里已有的 `recordId(...)`。

> **同一服务身份上的两个账号是两条记录，不是一个记录的两个字段。** 任何按 `serviceId` 单独删除的操作都是缺陷，见 §3 和 §6.3。

### 2.2 `SavedLogin` 与 `SavedCredential` 分离

`SavedLogin` 只保存非敏感登录资料；其逻辑唯一键是 `(ServiceId, Username)`。在 Android 中，`ServiceId` 是直连规范化 URL、受管安装 ID 或普通 SSH 隧道配置身份，`Username` 是协议中的 `identifier`。

| `SavedLogin` 字段 | 用途 |
| --- | --- |
| `Id` | 稳定的本地登录记录标识 |
| `ServiceId` | 直连规范化 URL、受管连接的安装 ID，或普通 SSH 隧道配置身份；不保存临时隧道端口 |
| `Username` | 登录标识 |
| `DisplayName` | 可选的用户可读标签；无来源时不展示空值 |
| `HasSavedCredential` | 供列表和登录页显示的非敏感状态投影 |
| `CredentialKey` | 关联保险箱记录的内部键，不含密码 |

`HasSavedCredential` 不是授权依据，也不能据此读取密码；真正能否读取由 `CredentialVault` 的记录、Keystore 和 `BiometricPrompt` 共同决定。写入、忘记、作废凭据时必须与该投影同步更新；启动时以保险箱记录复核并修正投影，避免状态漂移。

### 2.3 本地保存什么、不保存什么

| 内容 | 保存位置 | 说明 |
| --- | --- | --- |
| `SavedLogin`：稳定 `serviceId`、登录标识、显示名（如有）、`HasSavedCredential`、`CredentialKey`、最后使用时间 | `noBackupFilesDir/connections.bin` | 非敏感；不含任何秘密或临时隧道地址 |
| 登录密码 | `noBackupFilesDir/connection-vault.bin`，AES-256-GCM + Keystore | 密文；明文永不落盘 |
| AccessToken / RefreshToken | **仅内存** | Shell §5.3.3；进程结束即失效 |

**不保存**：明文密码、`PasswordText`、RefreshToken、凭据的明文副本、任何形式的「已解锁明文缓存」。

---

## 3. 登录交互的不变量

- 手动密码优先，其次读取可用保存凭据，否则要求输入；密码框始终显示且不回填保存密码。
- 保存凭据状态独立于输入框，读取之前必须重新判断记录与设备能力。
- 生物识别取消、失败或不可用不删除记录；永久密钥失效标记作废并保留密文。
- 认证成功后且用户明确勾选才保存；账号切换清空本次密码与临时授权。
- 忘记密码保留连接；删除登录记录仅删除选中身份，不影响同服务其他账号或 SSH 宿主。
- 提权用户名是独立的非秘密使用记忆：按当前服务和登录账户保存最后成功提权的用户名，不能从平台名称推断；失败或取消不覆盖。该用户名不代表已保存密码或有效授权，清理入口与 [使用记忆](../features/Settings.md#使用记忆) 一致。

这些规则已纳入当前实现，旧 G1–G10 差异快照和修复任务表不再维护。执行证据见 [当前状态](../status/Progress.md)。

---

## 4. 状态模型

状态集中在一个**不依赖 Android 类型**的纯 Kotlin 状态里，因此决策表可以被 JVM 单测完整覆盖（沿用 `LayoutState` / `unlockModeFor` 的既有做法）。

```kotlin
/** 现在准备以哪个身份、通过哪个当前地址登录。 */
data class SelectedLogin(
    val kind: ServerServiceIdKind,
    val serviceId: String,
    val effectiveBaseUrl: String,
    val identifier: String,
) {
    val id: String get() = loginId(serviceId, identifier)
    val credentialKey: String get() = recordId(VaultKind.Connection, serviceId, identifier)
    val isComplete: Boolean
        get() = serviceId.isNotBlank() && effectiveBaseUrl.isNotBlank() && identifier.isNotBlank()
}

/** 该身份的保存凭据处于什么状态。四态互斥，且必须能把「不可用」与「不存在」分开。 */
sealed interface SavedCredentialState {
    /** 本机没有这个身份的凭据。 */
    data object Absent : SavedCredentialState

    /** 记录存在，但本机此刻无法解封：指纹总开关关闭、设备已无可用认证方式、或密钥当前不可用。 */
    data object Unavailable : SavedCredentialState

    /** 记录存在且可解封。 */
    data object Available : SavedCredentialState

    /** 记录曾存在，但密钥已永久失效。保留密文与记录，禁止读取（D5）。 */
    data object Invalidated : SavedCredentialState
}

/** 由保险箱记录与设备解锁能力派生，纯函数。 */
fun credentialState(record: VaultRecord?, unlockMode: VaultUnlockMode?): SavedCredentialState
```

| ViewModel 状态（§9） | 落点 | 说明 |
| --- | --- | --- |
| `SelectedLogin` | `SelectedLogin` | 直连时由 `serverUrl` + `identifier` 输入派生；受管连接由已验证安装 ID、当前隧道地址和 `identifier` 构造；普通 SSH 隧道由稳定配置身份、当前隧道地址和 `identifier` 构造 |
| `HasSavedCredential` | `SavedLogin.HasSavedCredential` + `credentialState` 复核 | 持久化的非敏感显示投影，不构成安全边界，见 §4.2 |
| `PasswordText` | `OutlinedTextField.value` | 提交后立即置空 |
| `UseBiometricProtection` | `AppearanceState.fingerprintEnabled` | 已有的全局指纹总开关（Shell §5.5） |
| `CredentialUnlocked` | `unlockedIdentities: Set<String>` | 见 §4.3 |
| `IsLoggingIn` | `isLoggingIn` | 并发闸门 |

### 4.1 为什么要区分 `Unavailable` 与 `Invalidated`

两种情况对用户的意义完全不同，合并就会撒谎：

| 状态 | 用户看到 | 可恢复性 |
| --- | --- | --- |
| `Unavailable` | 「本机当前无法解封保存的密码，请手动输入」 | 可恢复：重新录入指纹、重新开启总开关后该条记录**仍然有效** |
| `Invalidated` | 「设备指纹已变更，保存的密码当前不可用，请重新输入并在成功后重新保存」 | 当前不可读取；保留原记录与密文，只有用户显式删除才会移除 |

`Unavailable` 时**不能**提示「已失效」——那会诱导用户白白重新保存一次。这也是 Shell §5.4「无法使用不是已失效的证据」那条实现的延续。

### 4.2 `HasSavedCredential` 的边界

`HasSavedCredential` 是 `SavedLogin` 的非敏感显示投影，解决列表不必解密即可标出「已保存密码」的需求；它不是密码存在性或解锁权限的安全真源。登录决策始终复核 `CredentialVault`，并在两者不一致时以保险箱为准、修正投影。这样既保留了可读的 `SavedLogin` 状态，也不会把一个布尔值变成安全边界。

### 4.3 `CredentialUnlocked` 的确切含义

```kotlin
/**
 * 本进程内，该身份是否已经过一次成功的保存凭据授权。
 *
 * 它不缓存明文，也不跳过 VaultAccess.load。含义严格限定为：
 * - 设备解锁窗口模式（D3）：授权后 Keystore 密钥在 5 分钟窗口内可再次解封，因此「已授权」真实成立；
 * - 按次强指纹模式（D1/D2）：每一次解封都要重新过指纹，所以该状态**恒为 false**，不显示任何「已授权」提示。
 *
 * 清零时机：切换到另一个身份、写入或删除该身份的凭据、退出登录、进程结束。
 */
```

它唯一的用途是：在窗口模式下，让「刷新被拒绝 → 回到登录页」这条路径不必重复一次锁屏确认就能再次登录。**它不参与安全边界**——安全边界始终是 `VaultAccess.load` 里的 `BiometricPrompt`。

---

## 5. 登录决策

### 5.1 决策表

输入：`SelectedLogin`、`PasswordText`、`SavedCredentialState`、`IsLoggingIn`。

| # | 字段完整 | `PasswordText` | 保存凭据状态 | 结果 | 按钮文案 |
| --- | --- | --- | --- | --- | --- |
| 1 | 否 | — | — | `MissingFields` | 连接 |
| 2 | 是 | 正在登录 | — | 忽略点击 | 连接中… |
| 3 | 是 | 非空 | 任意（含 `Available`） | `ManualPassword` | 连接 |
| 4 | 是 | 空 | `Available` | `UnlockSavedCredential` | 登录 |
| 5 | 是 | 空 | `Unavailable` | `RequirePassword(CredentialUnavailable)` | 连接 |
| 6 | 是 | 空 | `Invalidated` | `RequirePassword(CredentialInvalidated)` | 连接 |
| 7 | 是 | 空 | `Absent` | `RequirePassword(CredentialAbsent)` | 连接 |

要点：

- **提交时去除首尾空白。** 登录标识和手动输入的密码在提交时 trim，保留大小写与内部空格；下表的密码「空 / 非空」以 trim 后的值为准。保存的凭据解封后直接使用。
- **第 3 行优先于第 4 行。** 只要 `PasswordText` trim 后非空，本次就明确使用用户手动输入的密码，旧的 `SavedCredential` 本次忽略——**但不删除、不覆盖**。
- **只有一个「登录」按钮。** 按钮不区分「指纹登录」与「密码登录」两条分支；点击后统一按本表决策，避免两条路径互不回落（G1）。
- **不允许空密码登录。** 资料 §4 的例外「除非目标服务本身明确支持空密码」在 RelaxKonOS 不成立：[`LoginRequest.Password`](../../../../Shared/RelaxKonOS.Protocol/Identity/LoginRequest.cs) 是必填 `string`，服务端空字段返回 `400 invalid-input`（[`RelaxKonOS.Login.md`](../../../../docs/platform/RelaxKonOS.Login.md) §4.5）。因此不引入空密码分支。

```kotlin
sealed interface LoginDecision {
    /** 使用本次手动输入的密码；调用方在使用后立即清零 [password]。 */
    class ManualPassword(val password: CharArray) : LoginDecision

    /** 先用生物识别解封保存的凭据，成功后再登录。 */
    data object UnlockSavedCredential : LoginDecision

    /** 必须先输入密码；[gap] 决定提示文案与焦点落点。 */
    data class RequirePassword(val gap: CredentialGap) : LoginDecision

    /** 服务器地址或登录标识尚未填写。 */
    data class MissingFields(val server: Boolean, val identifier: Boolean) : LoginDecision
}

enum class CredentialGap { Absent, Unavailable, Invalidated }

/** 返回 `null` 表示本次点击应被忽略（正在登录）。 */
fun decideLogin(
    selected: SelectedLogin,
    passwordText: String,
    credential: SavedCredentialState,
    isLoggingIn: Boolean,
): LoginDecision?
```

### 5.2 两条执行路径

```text
点击「连接」
   │
   ├─ MissingFields ─────────► 焦点移到缺失字段，不发请求
   │
   ├─ RequirePassword(gap) ──► 按 gap 给出提示，焦点移到密码框，不发请求
   │
   ├─ ManualPassword(p) ─────► PasswordText := ""
   │                           POST /auth/login(p)     ← 认证失败不动任何凭据（§7.3）
   │                           p.fill('\0')
   │
   └─ UnlockSavedCredential ─► BiometricPrompt
                                 ├─ 取消 ──► 静默返回；PasswordText 保持空、凭据不动
                                 ├─ 失败 ──► 提示原因（不含密码）；凭据不动、可重试
                                 └─ 授权成功
                                      ▼
                                  tmp := vault.open(record)   ← 明文只在内存中
                                       │
                                  POST /auth/login(tmp)
                                       │
                                  tmp.fill('\0')              ← 无论成败立即释放
```

**保存发生在哪一步。** 两条路径成功之后是同一段收尾：

```text
认证成功
   ├─ 连接档案 upsert(serviceId, identifier, now)          ← 回填稳定身份，不含秘密/临时端口
   └─ 用户勾选了「在本机保存密码」？
        ├─ 是 ──► 生物识别确认一次（Shell §5.3.7）──► 写入 / 覆盖该身份的凭据
        │            └─ 取消或失败 ──► 不写；提示「已登录，但密码没有保存」（不视为登录失败）
        └─ 否 ──► 不写；**已有的旧凭据保持不变**（不是删除，见 §7.3）
```

---

## 6. 界面规格

### 6.1 登录页（单形态）

登录页始终采用统一表单：密码框可见、可编辑；是否保存密码由独立状态行表达，不能通过隐藏表单表达。

```text
                    RelaxKonOS        ← 品牌标记（登录卡片之外）
              添加 Windows 10/11 设备  ← 文字链接
                  安装或管理服务器      ← 文字链接（同款、同侧）
┌────────────────────────────────────────────┐
│ 登录                                        │
│ 连接到 RelaxKonOS 服务器。密码只用于在本机  │
│ 建立会话，不会发送到其他任何地方。          │
│                                            │
│ （操作失败时显示错误弹窗）                    │
│                                            │
│ [连接管理]（始终显示）                       │
│ 服务器地址  [ office.example.com        ]   │
│ 登录标识    [ root                      ]   │
│ 密码        [                           ]   │
│             🔒 已保存密码 · 留空即可用指纹登录  ← 状态行（supportingText）
│ ☑ 在本机保存密码，用指纹解封                 │
│ [ 登录 ]                                    │
└────────────────────────────────────────────┘
```

品牌标记下方的“添加 Windows 10/11 设备”是滚动内容的一部分，不能固定在安全区右上角；小高度设备上固定按钮会与标记重叠。“安装或管理服务器”紧随其后；连接管理入口始终保留在登录卡片内、服务器地址之前，没有保存记录或配对设备时打开空状态列表。它们要么创建可填入的服务器，要么选择已有身份，都是地址输入的前置动作。

两条入口是**同类动作**，因此画成同一种形状：品牌标记下方的两个文字链接，同侧对齐，谁也不占一整行。这条界面里唯一的主动作是「登录」，把「安装或管理服务器」画成整宽描边按钮会把它误报成第二个主按钮；文字链接既保持它可达，也保持它与「添加 Windows 10/11 设备」的并列关系。两者上下排列而不是并排，是因为英文/日文文案在小屏上一行放不下两条链接。

「已登录，但密码没有保存」这类提示有两条分支，只有其中一条能关掉（`Shell.Design.md` §3.4）：

| 分支 | 原因 | 「不再提醒」 |
| --- | --- | --- |
| `unlockMode` 为 `null` 或解封失败为 `Unavailable` | 本机没有可用的保险箱或指纹/锁屏 | 提供（`LoginCredentialNotSavedDevice` / `SavedPasswordUnavailable`） |
| 用户取消保存、锁定、密钥失效、被篡改 | 本次操作的结果，用户有补救动作 | 不提供，每次都要说 |

### 6.2 「已保存密码」怎么表达

- **`PasswordText` 永远是纯粹的本次输入**，为空时输入框就是空的。
- 「存在保存凭据」表达在**输入框之外**：`supportingText` 一行状态文案 + 锁形图标。
- 文案随 `SavedCredentialState` 变化：

| 状态 | 状态行 | 语义颜色 |
| --- | --- | --- |
| `Absent` | （不显示） | — |
| `Available`（按次强指纹） | 已保存密码 · 留空即可用指纹登录 | `onSurfaceVariant` |
| `Available`（窗口模式） | 已保存密码 · 留空即可用锁屏解锁 | `onSurfaceVariant` |
| `Unavailable` | 本机当前无法解封保存的密码 | `error` |
| `Invalidated` | 保存的密码已失效，请重新输入并重新保存 | `error` |

- 资料 §3 允许的另一种形态——输入框内显示 `••••••••` 占位——只有写成 `placeholder`（从不写入 `value`）时才不违反不变量。本设计**不采用**：占位与真实输入在视觉上无法区分，容易在后续改动中被误写成 `value`，而 `supportingText` 没有这个风险。

### 6.3 连接管理（登录页与 Shell 内共用）

每条密码登录记录展示：服务器类型（直连或受管）、可读的服务器身份、登录标识、凭据状态、最后使用时间；点击有可用保存密码的直连记录会立即按 §5 的既有决策表连接。没有保存密码、凭据无法解封，或连接失败时，仍保留刚选记录的服务器和登录标识在表单中，并把焦点交给密码输入，而不是丢失用户的选择。

连接管理还列出已配对的 **Windows 10/11 设备**，与密码登录记录分开呈现，明确标为“Windows 10/11 · 已配对的设备密钥登录”，并显示对应服务器身份。点击此项直接发起 nonce 签名登录；它不伪装成 Android 设备，也不使用或显示密码。

连接管理在首部显示 Ubuntu、Windows Server、Windows 10、Windows 11 的平台标记，并在相邻商标声明中注明 Ubuntu 归 Canonical、Windows 系列归 Microsoft。图例与每条记录的标记来自**同一份映射**，所以图例不会宣传一条记录永远显示不出的标记，也不会漏掉记录能显示的标记。

登录选择列表、Shell 内连接管理和已保存密码列表只有在**服务器自己说过它运行什么系统**时才显示对应标记：答案来自 `GET /api/v1.0/server/host-operating-system`（匿名、只回答系统类别，见 [架构文档](../../../../docs/architecture/RelaxKonOS.Protocol.md)）。保存的值是最后一次观测结果；每次打开列表和登录成功时重新查询，包括已有标记及 `Unknown`，因为同一 IP/URL 可以换系统。同一服务的多个账号共享一次查询与实时标记，更新档案中的系统字段但不改密码或最后使用时间。并发最多四个查询，旧请求迟到不能覆盖更新请求的结果。网络或契约失败保留最后结果，下次打开继续尝试；从未成功或系统未知时显示通用连接标记。仅有密码记录、没有连接档案时也可查询和显示，但不重建档案。受管安装只有当前登录会话存在已核实隧道时才能通过其 `effectiveBaseUrl` 查询，不主动开 SSH，不落盘临时端口。已配对设备行也使用当前系统标记，文字仍明确密钥登录的 Windows 10/11 兼容范围，不从配对关系推断当前系统。

已登录时，「更多 → 切换登录」先登出旧会话并打开登录选择列表；「连接管理 → 某条记录 → 切换登录」先登出再选择该身份，并按既有登录决策使用保存密码或要求输入密码。切换清空导航栈、密码输入、配对代码与窗口授权标记，保留所有连接与密码记录。关闭选择列表或取消指纹后停留登录页；不会恢复旧会话。重复点击切换/登出期间只执行一次会话结束。远端登出请求失败也必须清除本地 token，再允许下一次登录。

密码登录记录左滑显示管理入口，其中的两个**互不替代**动作为：

| 动作 | 对凭据 | 对档案 | 确认文案要点 |
| --- | --- | --- | --- |
| **忘记密码** | 删除该身份的凭据 | 保留 | 「下次登录需要重新输入密码；服务器与账户记录会保留。」 |
| **删除登录记录** | 删除（若存在） | 删除该条 `(服务器, 标识)` | 「同时删除为它保存的密码。」并给出具体服务器 + 账户 |

两个动作都只作用于**选中的那一条**。`ConnectionProfileStore.remove` 必须按 `(serviceId, identifier)` 成对删除（G7/G8 缺陷修复）。受管登录被选择时须先由服务器中心恢复并核实隧道；不能把安装 ID 当作 HTTP URL 请求，也不能回填旧的临时端口。

---

## 7. 场景规格

### 7.1 切换账号

```text
用户选择另一条登录记录
   → PasswordText := ""
   → 清零并丢弃仍在内存中的临时 Credential（正常情况下它只存在于一次登录调用内）
   → 丢弃该会话内属于旧身份的 CredentialUnlocked 标记
   → SelectedLogin := 新身份
   → 查询新身份的凭据状态，更新状态行
   → 不触发任何生物识别
```

「切换账号」不是凭据事件：不读明文、不删凭据、不要求指纹。**也不提前验证生物识别**——验证只在真正要用保存密码时发生（即点击登录、且 `PasswordText` 为空）。

### 7.2 生物识别失败与取消

| 情形 | 密码框 | 保存的凭据 | 用户可做什么 |
| --- | --- | --- | --- |
| 用户取消 | 保持空 | 不动 | 重新点按钮，或手动输入密码 |
| 指纹不匹配 / 暂时锁定 | 保持空 | 不动 | 稍后重试，或手动输入密码 |
| 永久锁定 | 保持空 | 不动 | 先解锁设备再重试，或手动输入密码 |
| 密钥永久失效 | 保持空 | **保留记录与密文；同一 `VaultKind` 的共享 Keystore alias 保护的全部记录都标记为 `Invalidated`**（D5），禁止读取 | 手动输入密码并勾选保存；客户端轮换失效 alias、再次请求本次明确的授权，仅把当前身份重新密封 |
| 本机无可用认证方式 | 保持空 | 不动，状态降为 `Unavailable` | 手动输入密码 |

**取消是静默的**（Shell §5.4）：`ERROR_USER_CANCELED` / `ERROR_NEGATIVE_BUTTON` 不弹错误、不改记录。上表除「取消」外的情形都要给出原因，但绝不给出任何与密码内容有关的信息。

### 7.3 认证失败不修改保存凭据

无论本次使用的是手动密码还是临时解封的保存密码，认证失败都**不修改、不删除、不覆盖**原有 `SavedCredential`。认证失败只报告登录结果；用户可重试生物识别，或输入新密码后再次登录。只有认证成功且用户勾选保存时，才允许写入或覆盖凭据。

| 结果 | 对 `SavedCredential` |
| --- | --- |
| `401 invalid-credential` | 不动 |
| 网络错误 / 超时 / 5xx | 不动 |
| 429 / 403（限流、账户状态类） | 不动 |

删除保存凭据是用户显式的「忘记密码」或「删除登录记录」操作（§6.3），不是任何一次登录失败的副作用。

### 7.4 密钥永久失效改为「标记」而非「删除」（D5）

本机密钥永久失效时，保留记录和密文、标记 `Invalidated` 并禁止读取。一个 `VaultKind` 共用一个 Keystore alias，alias 失效时该保险箱全部记录都需标记失效。

理由：

1. 本规范要求生物识别相关的失败**绝不**自动丢弃用户数据。密钥永久失效是生物识别链路里唯一会走到「删除」的路径，正好是这条要求的靶子。
2. 「删除」会让用户失去「这个身份曾经保存过密码」这条信息。实际后果是：列表说「未保存密码」，用户不知道发生过什么，也没有任何东西提示他「新录入的指纹作废了旧凭据」。
3. 「标记」保留了这条信息，并且能给出准确的原因。它同时避免了一个真实的坑：如果自动删除，用户在安全页里看不到任何痕迹，会反复重新保存、反复失效。

实现：

- `VaultRecord` 增加 `state: VaultRecordState { Sealed, Invalidated }`；转为 `Invalidated` 时保留 `iv` / `ciphertext`，但任何读取路径都必须拒绝解封。
- 新增 `CredentialVault.markInvalidated(record)` 与 `markAllInvalidated(kind)`；`open()` 对 `Invalidated` 记录直接抛 `VaultRecordInvalidatedException`。后者用于共享 alias 失效，避免同保险箱的兄弟记录显示成可用。
- `VaultAccess.load` 把该异常映射为 `UnlockFailure.KeyInvalidated`（与 Keystore 的 `KeyPermanentlyInvalidatedException` 同一出口），调用方只做「标记」，不再做「删除」。用户随后用手动密码成功登录并明确勾选保存时，`VaultAccess.save` 仅轮换一次失效 alias、重新请求生物识别授权，并重新密封当前身份；不会无限重试，也不会复活其他旧记录。
- 保险箱文件格式 `MAGIC` 升版并新增 `state` 字节。按 [AGENTS.md](../../../../AGENTS.md) 的 API 演进策略，**不写迁移适配**：旧文件按版本不匹配降级为「无已保存凭据」，用户重新保存一次即可。

### 7.5 同一规则适用于提权保险箱

`ElevationDialog` 目前在 `UnlockFailure.KeyInvalidated` 时同样 `vault.delete(record)`。同一个 D5 规则一并适用：**标记作废、保留记录**。提权对话框在选择管理员凭据时把 `Invalidated` 记录排除在「使用指纹确认」之外，只提供「输入密码」。

注意这与 Shell §5.8.2（D4：服务端拒绝提权一律删除该条密码）**不冲突**：D4 是服务端明确判定，D5 是本机密钥状态，两者处置不同且都有依据。

提权对话框里「本机无法用指纹或锁屏解封」与 §6.1 的登录页共用 `ReminderKind.SavedPasswordUnavailable`：同一句结论只对应一条可恢复项，「账户与安全 → 提醒」里不会因此出现两条。但那条提示**不能**像浮动通知一样被静音吞掉——它同时是「授权尚未生效、再按一次可在不保存的前提下继续」的唯一指示（Shell §5.4 第 2 条），所以已静音时只撤掉勾选、句子照常显示；而提权对话框自己的「管理员密码未保存」只由用户取消指纹确认产生，按 §6.1 的表第二行处理，不给勾选。

### 7.6 登出与退出登录

- 登出（`POST /auth/logout` + 清空 back stack）：不清凭据、不清连接档案；清空 `CredentialUnlocked` 标记与内存 token（Shell §4.1 规则 3）。
- 「关闭指纹保存」总开关（安全页）：清空两个保险箱并销毁 Keystore 密钥（现状保留）。会删除凭据，但这是用户在安全页里的**显式**操作，不属于「失败自动删除」。

### 7.7 服务器中心的 SSH 保险箱

`VaultKind.Ssh` 是第三个独立保险箱（`ssh-vault.bin` / `rk.ssh.vault`，AAD 按 `Ssh|host:port|user` 绑定），规则与连接凭据一致，不新立一套：

- 保存同样需要用户**显式勾选**（默认勾选、可取消），且只在一次成功握手之后写入；写入失败只读作「已连接，但密码没保存」，绝不冒充连接失败（§8）。
- 只有**用户本次输入**的密码才问这一次保存。从保险箱解封得来的密码（本次点击刚刚用指纹/锁屏授权过）和本会话内存里上一次已验证的副本都不再问：前者等于让同一次授权做两遍，后者来自一次已经问过的验证。这条由纯函数 `shouldSaveSshPassword` 的第三个条件表达（`SshPasswordOrigin`），不靠界面各写一遍。
- **取消勾选不删除**已有记录；删除只能由「忘记已保存密码」这个显式动作发起，并且与「删除主机记录」互不替代（§6.3 同一条规则）。
- Keystore alias 永久失效时同样**标记作废、保留记录**，并在同域记录上一致生效（§7.4）。
- 解锁方式由 `unlockModeFor` 给出与连接凭据相同的策略：强生物识别按次确认，弱生物识别退到锁屏五分钟窗口（D2、D3）。

与登录凭据的唯一差别是：SSH 没有 debug 明文兜底（[ServerCenter.md §3](../features/ServerCenter.md)）。本机既无指纹也无锁屏时，勾选框直接禁用并说明原因，不做降级。「关闭指纹保存」总开关目前只清空连接与提权两域，SSH 记录会随该开关失去可解封性但不会被删除——这是**未关闭的缺口**，见 [Progress](../status/Progress.md) 的 AD01 行。

---

## 8. 明文密码的生命周期

```text
CredentialStore(密文)
   → 生物识别授权
   → 临时解封为 CharArray          ← 明文生命周期起点
   → 构造登录请求
   → 请求结束（成功 / 失败 / 异常 / 取消）
   → fill('\u0000')               ← 生命周期终点
```

| 禁止项 | 本设计的对应保障 |
| --- | --- |
| 日志输出密码 | 登录链路不写日志；`loginProblemMessage` 只带 `status` / `problem code` / `traceId` |
| Exception 携带密码 | 异常消息只描述密钥与保险箱状态，从不拼接凭据内容 |
| 遥测 / 分析上传密码 | 不存在遥测通道；诊断报告只含服务器地址、能力名、记录条数与自检结果 |
| ViewModel 长期持有明文 | ViewModel **不持有**明文：`ManualPassword.password` 与解封结果都在一次调用的作用域内，`finally` 中清零；`CredentialUnlocked` 是布尔状态，不含明文 |
| 数据库 / SharedPreferences 保存明文 | 明文只存在于 `CharArray`；持久层只有密文与非敏感元数据 |
| 为了显示 `••••••••` 而提前读明文 | 状态行由 `CredentialVault.record(...)` 的**存在性**派生，不涉及解密 |
| 用 `bool IsFingerprintVerified` 当安全边界 | 边界是 Keystore 密钥策略 + `BiometricPrompt.CryptoObject`；`CredentialUnlocked` 明确声明不参与安全边界（§4.3） |

### 8.1 debug 构建的明文兜底（D10）

上面这张表的立场是「明文不落盘」。有一类设备让这条立场无法执行到底：**完全没有锁屏**的机器。
`AndroidKeyStore` 的 `setUserAuthenticationParameters` 只接受 `AUTH_BIOMETRIC_STRONG` 与 `AUTH_DEVICE_CREDENTIAL`，
不存在「弱生物识别」标志位。设备不设锁屏时，连接保险箱在结构上无法创建窗口密钥，`unlockMode()` 恒为 `null`。
此时「保存密码」整体不可用——不是提示不够清楚，而是底层没有可用的密钥策略，任何界面文案都改变不了这一点。

为了让调试构建在这种情况下仍能跑通登录闭环，新增 `security/DebugCredentialStore.kt`：debug 构建下，
当设备没有任何可用锁屏时，把**一条**凭据以明文写入 `noBackupFilesDir/debug-credential.bin`。
它是刻意的例外，因此边界用**代码结构**锁死，而不是靠文档约定：

| 约束 | 实现 |
| --- | --- |
| release 构建永远拿不到明文 | `AppContainer.debugCredentials` 只在 `BuildConfig.DEBUG` 时创建实例，release 下恒为 `null`；所有调用点都以可空接收者访问，release 里根本不存在这条写入路径 |
| 只在「确实没有锁屏」时启用 | 三个条件必须同时成立：实例存在、用户开启了指纹保存总开关、`biometricCapability() == BiometricCapability.None`（`LoginViewModel.debugFallbackAvailable`） |
| 只保存一条 | 单文件单记录；`save` 即以新记录**替换**旧记录，不累积（`DebugCredentialStoreTest` 断言连续保存两次后文件里有且只有一个身份） |
| 能读回来 | `reveal(serviceId, identifier)` 返回新分配的 `CharArray` 副本，调用方用后清零；身份不匹配返回 `null` |
| 界面必须说明「未加密」 | 勾选框提示（`login_remember_hint_debug` / `login_no_lock_screen_debug`）、保存后提示（`login_credential_saved_debug`）、状态行（`login_saved_password_debug`）、连接列表（`connections_saved_password_debug`）、安全页（`account_security_debug_record_note`）全部写明未加密 |
| 删除路径必须接通 | 忘记密码、删除登录记录、关闭指纹总开关、安全页「清空全部」都调用 `DebugCredentialStore.delete` / `clear`；安全页单独列出这条记录并标红 |

文件格式 `RKD1`（`magic(i32) | serviceId(UTF) | account(UTF) | len(i32) | secret(bytes)`）。magic 不匹配或文件被截断时
解码为「无记录」——与本仓库其它二进制格式的演进策略一致（不写迁移，版本不匹配即降级为空）。

安全页与诊断导出都**显式列出**这条记录：`AccountSecurityScreen` 单列一项并注明未加密，`DiagnosticsScreen` 导出报告含
`debugCredentialRecord=` 一行。若某个界面漏报这条明文密码，它就是这个屏幕上唯一不诚实的地方。

---

## 9. 当前实现职责

| 文件 / 目录 | 职责 |
| --- | --- |
| `core/auth/SelectedLogin.kt`、`LoginDecision.kt`、`SavedCredentialState.kt` | 身份键、登录裁决、凭据四态 |
| `ui/connect/LoginViewModel.kt` | 密码与保存凭据两条登录执行路径 |
| `data/ConnectionProfileStore.kt`、`HostOperatingSystemLookup.kt` | 连接档案、每次打开列表/登录成功后的系统徽标重新核实；失败保留最后观测，迟到响应不能覆盖新结果 |
| `security/model/SavedLogin.kt`、`security/CredentialVault.kt` | 保存身份、保险箱、失效标记与按身份删除 |
| `core/auth/AuthSession.kt` | 稳定 serviceId、当前 effectiveBaseUrl、刷新与注销 |
| `data/NoticePreferenceStore.kt` | 本机「不再提醒」偏好；只存键，可读回、可恢复 |
| `ui/icons/ServerPlatformBadge.kt` | 登录选择、连接和密码列表共用系统徽标 |

## 10. 验证归属

当前实现与自动化验证见 [当前状态](../status/Progress.md)。指纹变更、锁定、设备窗口、账号切换和真机视觉的未关闭检查集中在 [验收清单](../status/Verification.md)，不再保留已完成的新增/修改任务表。

## 11. 决策记录

编号接续 Shell §5.8 的 D1–D4（凭据保存例外、弱生物识别、时间窗降级、提权拒绝即删除），不重开已确认项。

| 编号 | 决策 | 结论 | 落点 |
| --- | --- | --- | --- |
| D5 | 本机密钥永久失效时如何处置已保存的密码 | **标记作废、保留记录与密文、禁止读取**；只有用户显式的「忘记密码」或「删除登录记录」才删记录 | §7.4、§7.5 |
| D6 | `HasSavedCredential` 是否写入 `SavedLogin` | **写入非敏感显示投影**；登录时复核保险箱，布尔值不构成安全边界 | §2.2、§4.2 |
| D7 | 登录页是否保留「简洁模式」 | **取消**，单形态；「有保存密码」用状态行表达 | §6.1 |
| D8 | 空密码登录 | **不允许**，不引入分支（服务端不支持） | §5.1 |
| D9 | 认证成功但用户未勾选保存 | **不动**已有旧凭据；删除是独立动作 | §5.2、§7.3 |
| D10 | 无锁屏设备（结构上无法建窗口密钥）能否保存密码 | **仅 debug 构建可以**，且需同时满足「设备无任何可用锁屏」与「用户开启指纹保存总开关」；明文落盘、只存一条、界面标注未加密、四条删除路径全部接通 | §8.1 |

## 12. 实施约束

- 使用当前 `SavedLogin` 模型；不保留旧名称、别名或格式兼容分支。
- `DisplayName` 是可选非敏感字段。服务端尚未提供时保持缺省，不显示空占位；日后确定来源时再补编辑入口。
- `CredentialKey` 仅用于关联 `SavedLogin` 和保险箱记录，不在常规界面展示，也不写入日志或诊断导出；如需排障，只能显示脱敏值。
- §7.5 的提权保险箱同样遵守「生物识别失败不自动删除记录」；它与服务端明确拒绝提权时的既有 D4 规则分别处理。
