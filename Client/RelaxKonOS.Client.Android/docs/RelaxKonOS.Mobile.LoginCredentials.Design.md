# RelaxKonOS Android 登录与本地凭据设计

> **状态：规范。** 本文规定 Android 端「登录身份 + 本地保存凭据」的状态模型与决策规则；实现不得以旧的简洁模式、隐藏密码框或自动删除凭据绕开这些规则。
>
> 上位约束（冲突时以上位为准）：
> - [`RelaxKonOS.Mobile.V1.Design.md`](./RelaxKonOS.Mobile.V1.Design.md) — V1 功能集、页面清单、两个凭据保险箱的安全约束（§5.3 不可变）
> - [`RelaxKonOS.Mobile.Design.md`](./RelaxKonOS.Mobile.Design.md) — 移动端产品与技术决策
> - [`RelaxKonOS.Login.md`](../../../docs/platform/RelaxKonOS.Login.md) — 登录、已保存连接、错误码矩阵
> - [`RelaxKonOS.Security.md`](../../../docs/platform/RelaxKonOS.Security.md) — 提权、危险操作确认
>
> 实现状态不写入本文，统一记入 [`RelaxKonOS.Mobile.Progress.md`](./RelaxKonOS.Mobile.Progress.md)。

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

不覆盖：`/auth/*` 的 wire contract（见 [`RelaxKonOS.Protocol.md`](../../../docs/architecture/RelaxKonOS.Protocol.md)）、token 刷新与重连状态机（见 V1 §4.2）、提权凭据的产品规则（见 V1 §5.8）；提权保险箱仅在 §7.5 沿用同一处置规则。

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

身份键是 **`(服务器地址, 登录标识)`**。

```text
连接A + root      ─┐
连接A + nanami     ├─ 三条完全独立的登录配置，各自拥有独立的凭据状态
连接B + nanami    ─┘
```

- 资料中的 `Service` 在 RelaxKonOS 里就是**服务端地址**（`serverUrl`）；`Username` 就是协议里的 **`identifier`**（[`LoginRequest`](../../../Shared/RelaxKonOS.Protocol/Identity/LoginRequest.cs) 的字段名）。因此不与仓库既有词汇（桌面端 `SavedLoginProfile(ServerUrl, Username, …)`、保险箱记录键 `kind|serverUrl|account`）产生第二套叫法。
- 本机稳定身份：`loginId(serverUrl, identifier)`。
- 归一化只有两条：去掉首尾空白、去掉地址结尾的斜杠。**不折叠大小写**——地址可能带大小写敏感的路径，标识可能是服务端要区分的两个账户，折叠任一者都会把两条独立记录并成一条，一次保存就会覆盖另一条凭据。
- 与保存凭据的连接键：`CredentialKey` = `recordId(VaultKind.Connection, serverUrl, identifier)`，即 [`CredentialVault.kt`](../app/src/main/java/app/relaxkonos/mobile/security/CredentialVault.kt) 里已有的 `recordId(...)`。

> **同一台服务器上的两个账号是两条记录，不是一个记录的两个字段。** 任何按 `serverUrl` 单独删除的操作都是缺陷，见 §3 的 G7。

### 2.2 `SavedLogin` 与 `SavedCredential` 分离

`SavedLogin` 只保存非敏感登录资料；其逻辑唯一键是 `(ServiceId, Username)`。在 Android 中，`ServiceId` 是经规范化的 `serverUrl`，`Username` 是协议中的 `identifier`。

| `SavedLogin` 字段 | 用途 |
| --- | --- |
| `Id` | 稳定的本地登录记录标识 |
| `ServiceId` | 经规范化的服务端地址 |
| `Username` | 登录标识 |
| `DisplayName` | 可选的用户可读标签；无来源时不展示空值 |
| `HasSavedCredential` | 供列表和登录页显示的非敏感状态投影 |
| `CredentialKey` | 关联保险箱记录的内部键，不含密码 |

`HasSavedCredential` 不是授权依据，也不能据此读取密码；真正能否读取由 `CredentialVault` 的记录、Keystore 和 `BiometricPrompt` 共同决定。写入、忘记、作废凭据时必须与该投影同步更新；启动时以保险箱记录复核并修正投影，避免状态漂移。

### 2.3 本地保存什么、不保存什么

| 内容 | 保存位置 | 说明 |
| --- | --- | --- |
| `SavedLogin`：服务器地址、登录标识、显示名（如有）、`HasSavedCredential`、`CredentialKey`、最后使用时间 | `noBackupFilesDir/connections.bin` | 非敏感；不含任何秘密 |
| 登录密码 | `noBackupFilesDir/connection-vault.bin`，AES-256-GCM + Keystore | 密文；明文永不落盘 |
| AccessToken / RefreshToken | **仅内存** | V1 §5.3.3；进程结束即失效 |

**不保存**：明文密码、`PasswordText`、RefreshToken、凭据的明文副本、任何形式的「已解锁明文缓存」。

---

## 3. 与当前实现的差异清单

> 本节是**设计时的快照**：它记录了本文各条规则相对当时实现的落差，也是每条规则存在的理由。哪些已经落地，看 [`RelaxKonOS.Mobile.Progress.md`](./RelaxKonOS.Mobile.Progress.md)。

现状来自 `app/src/main/java/app/relaxkonos/mobile/` 的实际代码（非文档声明）。判定沿用仓库规则：**注释与实现矛盾时以实现为准**。

| 编号 | 资料要求 | 当前实现 | 判定 | 优先级 |
| --- | --- | --- | --- | --- |
| G1 | 登录决策统一为「手动密码 > 保存密码 > 要求输入」（§5、§12） | `LoginViewModel.signIn` 在 `password.isEmpty()` 时直接返回 `login_missing_fields`；使用保存密码是**另一个按钮**（`使用指纹登录`），两条路径互不回落 | **不符合** | P0 |
| G2 | 密码框只表示「本次手动输入的密码」（§3） | 密码框本身从不回填，符合；但存在保存凭据时整张表单被折叠成「简洁模式」，密码框直接消失 | **部分** | P0 |
| G3 | 「存在保存凭据」必须是独立于 `PasswordText` 的状态（§3、§9） | 目前由「表单是否显示」隐式表达，没有可读状态 | **不符合** | P0 |
| G4 | 生物识别失败 / 取消 / 无法完成，绝不自动删除保存的凭据（§5、§11） | `LoginScreen.signInWithFingerprint`、`ElevationDialog` 在 `UnlockFailure.KeyInvalidated` 时执行 `vault.delete(record)` | **不符合**（且这是 V1 §5.4 已登记的既有决策，需按 D5 修订） | P0 |
| G5 | 切换账号要清空 `PasswordText` 与内存中的临时凭据（§6） | `select()` 只清 `password`；没有内存凭据概念 | 部分 | P1 |
| G6 | 保存动作必须在认证成功之后（§8） | `AuthSession.login` 的 `afterSuccessfulLogin` 回调保证写保险箱发生在认证成功之后；认证失败不改动任何凭据 | **已符合**（保持，不重做） | — |
| G7 | 「忘记密码」与「删除登录记录」是两个动作（§11） | 只有一个「删除」，两件事一起做；且 `ConnectionProfileStore.remove(serverUrl)` 会删掉该服务器下**所有**账号的档案 | **不符合**（含真实缺陷） | P0 |
| G8 | 唯一键 `(服务器, 标识)`（§1） | 保险箱记录键已按对；档案删除按 `serverUrl`（同 G7） | 部分 | P0 |
| G9 | 显式状态模型（§9） | `serverUrl/identifier/password/rememberCredential/busy` 散落在 ViewModel；决策逻辑与 Composable 同文件、无纯函数单测 | **不符合** | P1 |
| G10 | `SavedLogin` 字段齐全（§2） | `SavedConnection(serverUrl, identifier, lastUsedEpochMillis)`，缺 `Id` / `CredentialKey` / `DisplayName` / `HasSavedCredential` | 部分 | 见 §11 |

**已经在正确一侧、本轮只做保留的**：保险箱 AAD 绑定（§5.3.5）、明文以 `CharArray` 承载并及时清零（§5.3.2）、RefreshToken 不落盘（§5.3.3）、首次保存必须先过指纹（§5.3.7）、指纹不产生服务端凭据（§5.3.1）。

---

## 4. 目标状态模型

状态集中在一个**不依赖 Android 类型**的纯 Kotlin 状态里，因此决策表可以被 JVM 单测完整覆盖（沿用 `LayoutState` / `unlockModeFor` 的既有做法）。

```kotlin
/** 现在准备以哪个身份登录。资料中的 (Service, Username)。 */
data class SelectedLogin(val serverUrl: String, val identifier: String) {
    val id: String get() = loginId(serverUrl, identifier)
    val credentialKey: String get() = recordId(VaultKind.Connection, serverUrl, identifier)
    val isComplete: Boolean get() = serverUrl.isNotBlank() && identifier.isNotBlank()
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
| `SelectedLogin` | `SelectedLogin` | 由 `serverUrl` + `identifier` 两个输入框派生 |
| `HasSavedCredential` | `SavedLogin.HasSavedCredential` + `credentialState` 复核 | 持久化的非敏感显示投影，不构成安全边界，见 §4.2 |
| `PasswordText` | `OutlinedTextField.value` | 提交后立即置空 |
| `UseBiometricProtection` | `AppearanceState.fingerprintEnabled` | 已有的全局指纹总开关（V1 §5.5） |
| `CredentialUnlocked` | `unlockedIdentities: Set<String>` | 见 §4.3 |
| `IsLoggingIn` | `isLoggingIn` | 并发闸门 |

### 4.1 为什么要区分 `Unavailable` 与 `Invalidated`

两种情况对用户的意义完全不同，合并就会撒谎：

| 状态 | 用户看到 | 可恢复性 |
| --- | --- | --- |
| `Unavailable` | 「本机当前无法解封保存的密码，请手动输入」 | 可恢复：重新录入指纹、重新开启总开关后该条记录**仍然有效** |
| `Invalidated` | 「设备指纹已变更，保存的密码当前不可用，请重新输入并在成功后重新保存」 | 当前不可读取；保留原记录与密文，只有用户显式删除才会移除 |

`Unavailable` 时**不能**提示「已失效」——那会诱导用户白白重新保存一次。这也是 V1 §5.4「无法使用不是已失效的证据」那条实现的延续。

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

- **第 3 行优先于第 4 行。** 只要 `PasswordText` 非空，本次就明确使用用户手动输入的密码，旧的 `SavedCredential` 本次忽略——**但不删除、不覆盖**（资料 §7）。
- **只有一个「登录」按钮。** 按钮不区分「指纹登录」与「密码登录」两条分支；点击后统一按本表决策，避免两条路径互不回落（G1）。
- **不允许空密码登录。** 资料 §4 的例外「除非目标服务本身明确支持空密码」在 RelaxKonOS 不成立：[`LoginRequest.Password`](../../../Shared/RelaxKonOS.Protocol/Identity/LoginRequest.cs) 是必填 `string`，服务端空字段返回 `400 invalid-input`（[`RelaxKonOS.Login.md`](../../../docs/platform/RelaxKonOS.Login.md) §4.5）。因此不引入空密码分支。

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
   ├─ 连接档案 upsert(serverUrl, identifier, now)          ← 回填账户，不含秘密
   └─ 用户勾选了「在本机保存密码」？
        ├─ 是 ──► 生物识别确认一次（V1 §5.3.7）──► 写入 / 覆盖该身份的凭据
        │            └─ 取消或失败 ──► 不写；提示「已登录，但密码没有保存」（不视为登录失败）
        └─ 否 ──► 不写；**已有的旧凭据保持不变**（不是删除，见 §7.3）
```

---

## 6. 界面规格

### 6.1 登录页（单形态）

**取消「简洁模式」。** V1 §3.1 的启动裁决「有档案 → 简洁模式（隐藏密码框）」被本设计取代：密码框**始终可见**，因为隐藏它正是 G2/G3 的病根——「有没有保存密码」被编码成了「表单在不在」，而不是一个可读的状态。

```text
┌────────────────────────────────────────────┐
│ 登录                                        │
│ 连接到 RelaxKonOS 服务器。密码只用于在本机  │
│ 建立会话，不会发送到其他任何地方。          │
│                                            │
│ [错误横幅]                                  │
│                                            │
│ 服务器地址  [ office.example.com        ]   │
│ 登录标识    [ root                      ]   │
│ 密码        [                           ]   │
│             🔒 已保存密码 · 留空即可用指纹登录  ← 状态行（supportingText）
│ ☑ 在本机保存密码，用指纹解封                 │
│ [ 登录 ]                    [ 管理连接 ]     │
└────────────────────────────────────────────┘
```

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

每条记录展示：服务器地址、登录标识、凭据状态、最后使用时间；两个**互不替代**的动作（资料 §11）：

| 动作 | 对凭据 | 对档案 | 确认文案要点 |
| --- | --- | --- | --- |
| **忘记密码** | 删除该身份的凭据 | 保留 | 「下次登录需要重新输入密码；服务器与账户记录会保留。」 |
| **删除登录记录** | 删除（若存在） | 删除该条 `(服务器, 标识)` | 「同时删除为它保存的密码。」并给出具体服务器 + 账户 |

两个动作都只作用于**选中的那一条**。`ConnectionProfileStore.remove` 必须按 `(serverUrl, identifier)` 成对删除（G7/G8 缺陷修复）。

---

## 7. 场景规格

### 7.1 切换账号（资料 §6）

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

### 7.2 生物识别失败与取消（资料 §5、§11）

| 情形 | 密码框 | 保存的凭据 | 用户可做什么 |
| --- | --- | --- | --- |
| 用户取消 | 保持空 | 不动 | 重新点按钮，或手动输入密码 |
| 指纹不匹配 / 暂时锁定 | 保持空 | 不动 | 稍后重试，或手动输入密码 |
| 永久锁定 | 保持空 | 不动 | 先解锁设备再重试，或手动输入密码 |
| 密钥永久失效 | 保持空 | **保留记录与密文；同一 `VaultKind` 的共享 Keystore alias 保护的全部记录都标记为 `Invalidated`**（D5），禁止读取 | 手动输入密码并勾选保存；客户端轮换失效 alias、再次请求本次明确的授权，仅把当前身份重新密封 |
| 本机无可用认证方式 | 保持空 | 不动，状态降为 `Unavailable` | 手动输入密码 |

**取消是静默的**（V1 §5.4）：`ERROR_USER_CANCELED` / `ERROR_NEGATIVE_BUTTON` 不弹错误、不改记录。上表除「取消」外的情形都要给出原因，但绝不给出任何与密码内容有关的信息。

### 7.3 认证失败不修改保存凭据

无论本次使用的是手动密码还是临时解封的保存密码，认证失败都**不修改、不删除、不覆盖**原有 `SavedCredential`。认证失败只报告登录结果；用户可重试生物识别，或输入新密码后再次登录。只有认证成功且用户勾选保存时，才允许写入或覆盖凭据。

| 结果 | 对 `SavedCredential` |
| --- | --- |
| `401 invalid-credential` | 不动 |
| 网络错误 / 超时 / 5xx | 不动 |
| 429 / 403（限流、账户状态类） | 不动 |

删除保存凭据是用户显式的「忘记密码」或「删除登录记录」操作（§6.3），不是任何一次登录失败的副作用。

### 7.4 密钥永久失效改为「标记」而非「删除」（D5）

**变更点：** V1 §5.4 的旧规则是「清除该保险箱中受影响的记录，保留服务器与账户」。本设计改为**保留记录和密文、标记为 `Invalidated`、禁止读取**。一个 `VaultKind` 只有一个 Keystore alias，因此 alias 永久失效时，该保险箱内的所有记录都受影响；不能只把第一次尝试读取的那一条标成失效。

理由：

1. 资料 §5、§11 要求生物识别相关的失败**绝不**自动丢弃用户数据。密钥永久失效是生物识别链路里唯一会走到「删除」的路径，正好是这条要求的靶子。
2. 「删除」会让用户失去「这个身份曾经保存过密码」这条信息。实际后果是：列表说「未保存密码」，用户不知道发生过什么，也没有任何东西提示他「新录入的指纹作废了旧凭据」。
3. 「标记」保留了这条信息，并且能给出准确的原因。它同时避免了一个真实的坑：如果自动删除，用户在安全页里看不到任何痕迹，会反复重新保存、反复失效。

实现：

- `VaultRecord` 增加 `state: VaultRecordState { Sealed, Invalidated }`；转为 `Invalidated` 时保留 `iv` / `ciphertext`，但任何读取路径都必须拒绝解封。
- 新增 `CredentialVault.markInvalidated(record)` 与 `markAllInvalidated(kind)`；`open()` 对 `Invalidated` 记录直接抛 `VaultRecordInvalidatedException`。后者用于共享 alias 失效，避免同保险箱的兄弟记录显示成可用。
- `VaultAccess.load` 把该异常映射为 `UnlockFailure.KeyInvalidated`（与 Keystore 的 `KeyPermanentlyInvalidatedException` 同一出口），调用方只做「标记」，不再做「删除」。用户随后用手动密码成功登录并明确勾选保存时，`VaultAccess.save` 仅轮换一次失效 alias、重新请求生物识别授权，并重新密封当前身份；不会无限重试，也不会复活其他旧记录。
- 保险箱文件格式 `MAGIC` 升版并新增 `state` 字节。按 [AGENTS.md](../../../AGENTS.md) 的 API 演进策略，**不写迁移适配**：旧文件按版本不匹配降级为「无已保存凭据」，用户重新保存一次即可。

### 7.5 同一规则适用于提权保险箱

`ElevationDialog` 目前在 `UnlockFailure.KeyInvalidated` 时同样 `vault.delete(record)`。同一个 D5 规则一并适用：**标记作废、保留记录**。提权对话框在选择管理员凭据时把 `Invalidated` 记录排除在「使用指纹确认」之外，只提供「输入密码」。

注意这与 V1 §5.8.2（D4：服务端拒绝提权一律删除该条密码）**不冲突**：D4 是服务端明确判定，D5 是本机密钥状态，两者处置不同且都有依据。

### 7.6 登出与退出登录

- 登出（`POST /auth/logout` + 清空 back stack）：不清凭据、不清连接档案；清空 `CredentialUnlocked` 标记与内存 token（V1 §4.1 规则 3）。
- 「关闭指纹保存」总开关（安全页）：清空两个保险箱并销毁 Keystore 密钥（现状保留）。会删除凭据，但这是用户在安全页里的**显式**操作，不属于「失败自动删除」。

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

| 禁止项（资料 §10） | 本设计的对应保障 |
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
| 能读回来 | `reveal(serverUrl, identifier)` 返回新分配的 `CharArray` 副本，调用方用后清零；身份不匹配返回 `null` |
| 界面必须说明「未加密」 | 勾选框提示（`login_remember_hint_debug` / `login_no_lock_screen_debug`）、保存后提示（`login_credential_saved_debug`）、状态行（`login_saved_password_debug`）、连接列表（`connections_saved_password_debug`）、安全页（`account_security_debug_record_note`）全部写明未加密 |
| 删除路径必须接通 | 忘记密码、删除登录记录、关闭指纹总开关、安全页「清空全部」都调用 `DebugCredentialStore.delete` / `clear`；安全页单独列出这条记录并标红 |

文件格式 `RKD1`（`magic(i32) | serverUrl(UTF) | account(UTF) | len(i32) | secret(bytes)`）。magic 不匹配或文件被截断时
解码为「无记录」——与本仓库其它二进制格式的演进策略一致（不写迁移，版本不匹配即降级为空）。

安全页与诊断导出都**显式列出**这条记录：`AccountSecurityScreen` 单列一项并注明未加密，`DiagnosticsScreen` 导出报告含
`debugCredentialRecord=` 一行。若某个界面漏报这条明文密码，它就是这个屏幕上唯一不诚实的地方。

---

## 9. 落地方案

### 9.1 新增

| 文件 | 内容 |
| --- | --- |
| `core/auth/SelectedLogin.kt` | `SelectedLogin` + `loginId()` + `credentialKey()` |
| `core/auth/SavedCredentialState.kt` | 四态 + `credentialState(record, unlockMode)` 纯函数 |
| `core/auth/LoginDecision.kt` | `LoginDecision` / `CredentialGap` / `decideLogin()` 纯函数 |
| `ui/connect/LoginViewModel.kt` | 从 `LoginScreen.kt` 拆出，承载状态与两条执行路径 |

### 9.2 修改

| 文件 | 改动 |
| --- | --- |
| `data/ConnectionProfileStore.kt` | `remove(serverUrl)` → `remove(serverUrl, identifier)`；`upsert` 已按对去重，保持 |
| `security/model/SavedConnection.kt` | 直接改名 `SavedLogin`，补 `id` / `displayName` / `hasSavedCredential` / `credentialKey`（见 §12） |
| `security/CredentialVault.kt` | `VaultRecordState`、`VaultRecord.state`、`markInvalidated()` / `markAllInvalidated()`、`VaultRecordInvalidatedException`、`open()` 拒绝作废记录、文件格式升版 |
| `security/BiometricUnlock.kt` | `VaultAccess.load` 映射新异常；明确保存时轮换一次失效 alias 并重新密封当前身份 |
| `ui/connect/LoginScreen.kt` | 表单改为单形态；按钮文案随决策；凭据状态行；错误提示按 `CredentialGap` 分派 |
| `ui/connect/ConnectionListScreen.kt` | 每项两个动作：忘记密码 / 删除登录记录 |
| `ui/more/ConnectionsScreen.kt` | 同上；修掉按 `serverUrl` 全删的缺陷 |
| `ui/more/AccountSecurityScreen.kt` | `Invalidated` 记录单独标注失效原因 |
| `ui/common/ElevationDialog.kt` | `KeyInvalidated` → 标记作废（§7.5） |
| `core/net/ApiResult.kt` | 补 `ProblemCodes.LOGIN_RATE_LIMITED`，提供准确的限流文案；不参与任何凭据删除决策 |
| `core/net/RelaxKonApi.kt` | 登录失败只映射用户可读错误；凭据处置统一由成功后的保存流程和显式删除操作处理 |
| `res/values*/strings.xml` | 新文案，`values` / `values-zh` / `values-ja` 三份同步（缺项会回落英文） |
| `docs/` | 本文档 + V1 修订（§10） |

### 9.3 测试计划

| 测试类 | 覆盖 |
| --- | --- |
| `LoginDecisionTest`（新） | §5.1 决策表逐行，含「PasswordText 非空时 `Available` 被忽略」与「字段不全优先于一切」 |
| `SavedCredentialStateTest`（新） | 四态派生；`Invalidated` 优先于 `unlockMode == null`；`Unavailable ≠ Invalidated` |
| `SelectedLoginTest`（新） | 归一化只做「去前后空格」与「去结尾斜杠」，**不折叠大小写**；同服务器不同账号、不同服务器同账号都不碰撞；`credentialKey` 与保险箱 `recordId` 一致 |
| `ConnectionProfileStoreTest`（改） | 按对删除只影响一条；同服务器多账号互不牵连 |
| `CredentialVaultTest`（改） | `markInvalidated` 后记录与密文仍在、`open()` 被拒绝；「忘记密码」删记录；格式升版后旧文件降级为空 |
| `AuthSessionTest`（改） | 认证失败不触碰凭据；`invalid-credential`、`429` 与 `Transport` 均不会修改保存凭据 |
| `DebugCredentialStoreTest`（新） | §8.1 的兜底存储：连续保存两次后只剩一条、身份不匹配读取为空、`reveal` 返回可清零的副本、`delete` 只删匹配身份、`clear` 清空、异 magic 与截断文件降级为无记录、断言落盘内容确实含明文 |
| `VaultAccessTest`（新） | 平台不可命名的异常变成 `Failed(Unknown)` 而不是逃逸；四种已知拒绝各自保留原结论；`Invalidated` 记录在触达 Keystore 之前就被拒 |
| `SavedCredentialStateTest`（改） | §8.1 的兜底映射只填 `Absent` / `Unavailable`，绝不覆盖 `Available` / `Invalidated`；`SavedInDebugBuild` 优先于解锁方式 |

---

## 10. 需要同步修订的既有文档

| 文档 | 位置 | 修订 |
| --- | --- | --- |
| [`RelaxKonOS.Mobile.V1.Design.md`](./RelaxKonOS.Mobile.V1.Design.md) | §5.2 表格「清除时机」行 | 「密钥永久失效」由**删除**改为**标记作废、保留记录与密文**（D5） |
| 同上 | §5.4 失败分类表 `KeyPermanentlyInvalidatedException` 行 | 同上 |
| 同上 | §5 开头 | 指向本文，声明登录决策与本地凭据模型的细化归属 |
| 同上 | §3.1 认证入口、启动裁决段、§5.5 指纹登录流程 | 已同步：取消「简洁模式」与不可见密码回填，并入统一登录决策。 |
| [`RelaxKonOS.Mobile.Progress.md`](./RelaxKonOS.Mobile.Progress.md) | 全文 | 实现完成后更新（本轮不动） |
| [`RelaxKonOS.Login.md`](../../../docs/platform/RelaxKonOS.Login.md) | §10 | **不改**。桌面端继续「简洁选择模式 + 回填不可见的已保存密码」；Android 按本文是有意分歧，理由见 §6.1 |

---

## 11. 决策记录

编号接续 V1 §5.8 的 D1–D4（凭据保存例外、弱生物识别、时间窗降级、提权拒绝即删除），不重开已确认项。

| 编号 | 决策 | 结论 | 落点 |
| --- | --- | --- | --- |
| D5 | 本机密钥永久失效时如何处置已保存的密码 | **标记作废、保留记录与密文、禁止读取**；只有用户显式的「忘记密码」或「删除登录记录」才删记录 | §7.4、§7.5 |
| D6 | `HasSavedCredential` 是否写入 `SavedLogin` | **写入非敏感显示投影**；登录时复核保险箱，布尔值不构成安全边界 | §2.2、§4.2 |
| D7 | 登录页是否保留「简洁模式」 | **取消**，单形态；「有保存密码」用状态行表达 | §6.1 |
| D8 | 空密码登录 | **不允许**，不引入分支（服务端不支持） | §5.1 |
| D9 | 认证成功但用户未勾选保存 | **不动**已有旧凭据；删除是独立动作 | §5.2、§7.3 |
| D10 | 无锁屏设备（结构上无法建窗口密钥）能否保存密码 | **仅 debug 构建可以**，且需同时满足「设备无任何可用锁屏」与「用户开启指纹保存总开关」；明文落盘、只存一条、界面标注未加密、四条删除路径全部接通 | §8.1 |

## 12. 实施约束

- `SavedConnection` 应直接更名为 `SavedLogin`，并同步更新 Android 端全部调用点、测试与文档；该应用尚未发布，不保留旧名称或兼容别名。
- `DisplayName` 是可选非敏感字段。服务端尚未提供时保持缺省，不显示空占位；日后确定来源时再补编辑入口。
- `CredentialKey` 仅用于关联 `SavedLogin` 和保险箱记录，不在常规界面展示，也不写入日志或诊断导出；如需排障，只能显示脱敏值。
- §7.5 的提权保险箱同样遵守「生物识别失败不自动删除记录」；它与服务端明确拒绝提权时的既有 D4 规则分别处理。
