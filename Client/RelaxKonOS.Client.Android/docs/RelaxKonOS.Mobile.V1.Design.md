# RelaxKonOS Android 初版（V1）设计

> **状态：设计初稿，待评审。** 本文是 [`RelaxKonOS.Mobile.Design.md`](./RelaxKonOS.Mobile.Design.md) 的可落地细化：给出**初版的确定功能集、页面清单、导航流转**，以及**用指纹解锁已保存凭据（服务器密码 + 管理员密码）**的完整设计。
>
> 上位约束（冲突时以上位为准）：
> - [`RelaxKonOS.Mobile.Design.md`](./RelaxKonOS.Mobile.Design.md) — 移动端产品/技术决策、手机与平板自适应、国际化与主题
> - [`RelaxKonOS.Protocol.md`](../../../docs/architecture/RelaxKonOS.Protocol.md) — REST / Hub wire contract
> - [`RelaxKonOS.Security.md`](../../../docs/platform/RelaxKonOS.Security.md) — 权限提升、危险操作确认、风险分级
> - [`RelaxKonOS.PrivilegedOperations.Goal.md`](../../../docs/platform/RelaxKonOS.PrivilegedOperations.Goal.md) — 宿主提权（capability + target + 5 分钟授权）
> - [`RelaxKonOS.Login.md`](../../../docs/platform/RelaxKonOS.Login.md) — 登录、已保存连接、错误码矩阵
>
> 实现状态不写入本文，统一记入 [`RelaxKonOS.Mobile.Progress.md`](./RelaxKonOS.Mobile.Progress.md)。

---

## 1. 定位

初版 Android 客户端是 **Mobile Shell 的最小可用闭环**：能在手机和平板上登录一台服务器，看到主机状态，操作文件，连上终端，做几类受控的管理操作，并且**把"每次都要手输密码"这件事收敛为指纹一次确认**——同时不放松任何服务端校验。

三条底线：

1. **不做桌面缩小版。** 不迁移 Desktop、Taskbar、Start Menu、WindowManager、`RemoteWindow`、桌面内置应用。
2. **不复制服务端规则。** 页面只消费状态与意图；认证、提权、错误码映射、能力判断都在 Kotlin data layer。
3. **指纹只解封本地保险箱，不产生通行证。** 服务端该验的密码照样验，该发的 5 分钟授权照样只覆盖单一 capability + target。

---

## 2. 初版功能集

### 2.1 交付范围

能力可见性由**登录后拿到的 `ServerDescriptorDto.capabilities`** 决定：能力缺失时入口不出现，不用灰色占位。

| 域 | 初版内容 | 门控能力标识 | 优先级 |
| --- | --- | --- | --- |
| 连接与认证 | 多服务器连接档案；指纹免密登录；token 刷新；登出；会话过期回到登录页 | —（`/auth/*` 恒可用） | P0 |
| 首页 | 服务器名/平台/版本、连接状态、CPU/内存/磁盘/网络实时、近期告警、高频入口 | `server.metrics`（无则只显示连接与基本信息） | P0 |
| 文件 | 浏览、上传、下载、新建目录、重命名、复制/移动、删除（二次确认）、属性、受保护路径提权 | `server.files`；POSIX 权限显示另需 `server.posix.permissions` | P0 |
| 终端 | PTY 会话打开/附加/尺寸同步、扩展键栏、会话切换、断开即 detach | `server.terminal` | P0 |
| 容器 | 容器/镜像/Stack/网络/卷的查看与受控操作、容器日志 | `server.docker` | P1 |
| 进程与守护 | 性能趋势、进程分页查询与结束进程、受管工作负载与实时日志 | `server.processes` / `server.metrics` / `server.guardian` | P1 |
| 设置与诊断 | 连接管理、账户与安全（凭据保险箱）、外观与语言、诊断导出、关于、登出 | — | P0（骨架） |
| 部署与 Web 服务 | 查看发布与运行状态、站点列表与受控操作 | `server.application-deployments` / `server.web-server` | P2（能力可用才显示） |

### 2.2 初版明确不做

Git、隧道/代理、防火墙、证书、注册表、内置浏览器、代码编辑器、文件服务（SMB）、任务栏/多窗口、桌面壁纸与主题调色板同步。

理由：这些应用的桌面工作流密度高（多栏 diff、表格批量编辑、配置原文编辑），在移动端没有已定义的"可完成且安全"的任务流。宁可没有入口，也不给只读或不可操作的占位。

### 2.3 一个必须先讲清的密码域问题

登录标识可能是**宿主系统账户名**，也可能是 **Alias**（见 [`RelaxKonOS.AliasLogin.Goal.md`](../../../docs/platform/RelaxKonOS.AliasLogin.Goal.md)）。这两个不是同一个密码域：

- **登录凭据**：满足 `/auth/login`。Alias 密码可满足。
- **提权凭据**：满足 `POST /privileged/elevation` 的 `elevation-password-required` 挑战，必须是**宿主 OS 账户密码**，且该账户当前必须是宿主管理员。Alias 密码**永远不能满足**提权挑战（AliasLogin 明文规则：「Alias 密码不能通过任何现有 OS 提权复验」）。

因此初版必须把两者做成**两个独立保险箱**，不能"登录密码顺便当管理员密码"。§5 全部围绕这一点展开。

---

## 3. 页面清单

路由常量集中在 Kotlin `ui/nav/Routes.kt`，与 `AuthApiRoutes` / `PrivilegedApiRoutes` 一样只定义一次。布局列标明该页在各断点下的形态（Compact `<600dp`、Medium `600–839dp`、Expanded `≥840dp`，沿用 [`LayoutState.kt`](../app/src/main/java/app/relaxkonos/mobile/core/layout/LayoutState.kt) 的既有实现）。

### 3.1 认证入口（Shell 之外）

| 路由 | 页面 | 作用 | 布局差异 |
| --- | --- | --- | --- |
| `connect/list` | 连接档案列表 | 多服务器选择、编辑、删除单条记录；显示该条是否已保存密码/是否受指纹保护 | 单栏列表；平板为列表 + 详情两栏 |
| `connect/login` | 登录 | 服务器地址、登录标识、密码、记住连接、**使用指纹登录** | 单栏卡片；平板居中卡片 + 连接信息侧栏 |

启动裁决（不是独立路由，是 `AuthSession` 的一个状态）：进程启动后先读本地连接档案 —— 无档案 → `connect/login` 完整表单；有档案 → `connect/login` 简洁模式（默认选中最近使用项，凭据不可见，主按钮为指纹登录）。

密码输入框（登录页与提权对话框共用同一个控件）是**两态**的：默认掩码，点击尾部眼睛图标切成明文并**保持在明文**，再点一次回到掩码。它不是"按住才可见"的手势——长密码需要能看清，而不是靠按住按键维持。可见性只是展示状态（`rememberSaveable`），不写入会话、保险箱或任何文件；密码本身仍按 §5.3.2 的规则以 `CharArray` 承载并在请求结束后清零。

### 3.2 Shell 顶级目的地

顶级导航固定五项，对齐 [`RelaxKonOS.Mobile.Design.md`](./RelaxKonOS.Mobile.Design.md) §5.1：**主页 / 文件 / 终端 / 管理 / 更多**。

| 路由 | 页面 | Compact | Medium | Expanded |
| --- | --- | --- | --- | --- |
| `home` | 首页 | 单栏状态卡 | 单栏 + 侧栏 | 状态卡 + 趋势 + 近期操作三区 |
| `files` | 文件 | 单栏目录列表 | 列表优先 | 位置栏 + 列表 + 详情/预览三栏 |
| `terminal` | 终端 | 单会话全屏 + 扩展键栏 | 会话抽屉 + 终端 | 会话列表 + 终端双栏 |
| `manage` | 管理 | 能力域列表 | rail + 列表 | 能力域列表 + 内容区 |
| `more` | 更多 | 设置项列表 | 列表 | 列表 + 详情两栏 |

### 3.3 嵌套页面

| 路由 | 页面 | 说明 | 归属 |
| --- | --- | --- | --- |
| `files/detail?path=` | 文件详情/属性 | 大小、时间、POSIX 权限、打开方式 | files |
| `files/upload` | 上传（概览 + 逐项进度） | 走系统文件选择器取流 | files |
| `terminal/sessions` | 会话列表 | 多实例切换、显式关闭 PTY | terminal |
| `manage/docker/containers` | 容器列表 | 筛选、状态徽标 | manage |
| `manage/docker/containers/{id}` | 容器详情 | 详情 + 日志 + 受控操作 | manage |
| `manage/docker/images` | 镜像列表 | 拉取、删除 | manage |
| `manage/docker/stacks` | Stack 列表 | 定义查看、启停 | manage |
| `manage/monitor` | 性能 | CPU/内存/磁盘/网络/GPU 趋势 | manage |
| `manage/processes` | 进程 | 分页查询、结束进程 | manage |
| `manage/guardian` | 守护工作负载 | 列表 + 状态 | manage |
| `manage/guardian/{id}` | 工作负载详情 | 声明、实时日志、启停/重启 | manage |
| `manage/deployments` | 部署 | 发布与操作状态 | manage |
| `manage/webservers` | Web 服务 | 实例/站点、重载、配置测试 | manage |
| `more/connections` | 连接管理 | 等同 `connect/list`（Shell 内入口） | more |
| `more/account-security` | **账户与安全** | 两个凭据保险箱、指纹开关、清除 | more |
| `more/appearance` | 外观与语言 | 颜色模式、跟随系统、语言覆写 | more |
| `more/diagnostics` | 诊断 | 连接自检、服务端健康、日志导出（已脱敏） | more |
| `more/about` | 关于 | 版本、服务端信息、开源许可 | more |

### 3.4 全局叠加层（不属于导航图）

| 叠加层 | 触发 | 约束 |
| --- | --- | --- |
| 提权对话框 | 收到 `403 elevation-required` / `elevation-password-required` | 显示 capability 与**规范化目标**、账户名、`使用指纹确认` 与 `输入密码`；不做成底部 Sheet（避免误触绕过） |
| 危险操作确认 | 删除、停止/重启服务、部署、关闭终端 | 确认文本必须含具体目标（[`RelaxKonOS.Security.md`](../../../docs/platform/RelaxKonOS.Security.md) §7） |
| 错误横幅 / Snackbar | 网络、能力缺失、问题码 | 只显示映射后的本地化文案，不显示原始 `type` URI |
| 进度 Sheet | 上传/下载/长操作 | 可折叠，不阻塞导航 |

---

## 4. 界面流转

### 4.1 导航图

```text
启动
 │
 ├─ 无连接档案 ──────────────► connect/login（完整表单）
 │                                  │
 └─ 有连接档案 ──► connect/login（简洁模式，指纹登录）
                                    │
                         认证成功 → 拉取 ServerDescriptorDto
                                    │
                          ┌─────────▼─────────┐
                          │   MobileShell      │  ← 五项顶级导航
                          └─────────┬─────────┘
        ┌───────────┬───────────────┼───────────────┬───────────┐
        ▼           ▼               ▼               ▼           ▼
      home        files         terminal         manage       more
                    │               │               │           │
              files/detail   terminal/sessions  docker/*   account-security
              files/upload                      monitor   connections
                                                processes appearance
                                                guardian  diagnostics
                                                deploy    about
                                                webservers
```

三条流转规则：

1. **顶级目的地互不压栈。** 在 `files` 里进了详情再点 `terminal`，切回 `files` 时应回到该目的地自己的栈顶（Compose Navigation 的 save/restore state），而不是被重置到列表——平板上尤其明显。
2. **同一目的地在不同断点下用不同承载方式。** Expanded 下 `files/detail` 渲染为右栏而不是入栈页面，系统返回键因此不"返回"到列表，而是直接退出该目的地（与平板预期一致）。
3. **登出是清栈操作。** 登出先 `POST /auth/logout` 吊销 refresh token，再清空整个 back stack 到 `connect/login`，并保留连接档案（对齐桌面「退出远程桌面只注销会话，不清除已保存连接」）。

系统返回键顺序（沿用 Mobile.Design §6）：**关闭叠加层 → 返回详情/列表 → 退出当前导航层 → 交给系统**。

### 4.2 连接失效与重连流转

```text
任意页面
 │
 ├─ 网络错误 ─────► 错误横幅 + 就地重试；不清会话、不动凭据
 │
 ├─ 401（access 过期）─► 静默刷新一次 → 重试原请求
 │
 ├─ refresh 被明确拒绝 ─► 清会话 ─► connect/login（凭据仍在，可指纹再登录）
 │
 └─ 后台/进程回收 ─► 回前台重建：先刷新 token，再按当前路由重建页面与 Hub 订阅
```

服务端重启会让 refresh token 失效（[`RelaxKonOS.Login.md`](../../../docs/platform/RelaxKonOS.Login.md) §7），此时属于「refresh 被明确拒绝」，走回登录页但**不删凭据**。

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
| 记录键 | `serverUrl + 登录标识` | `serverUrl + 宿主管理员账户名` |
| 载荷 | 登录密码 | 管理员密码 |
| 解锁时机 | 登录页点击「使用指纹登录」 | 提权对话框点击「使用指纹确认」 |
| 用途 | `POST /auth/login` | `POST /privileged/elevation` |
| 指纹强度要求 | `BIOMETRIC_STRONG` 优先；不满足时可用设备凭据（见 §5.6） | **只接受 `BIOMETRIC_STRONG` 按次授权**；不满足则不保存 |
| 保存前提 | 用户显式勾选「记住此服务器的登录凭据」 | 用户在提权对话框显式勾选「用指纹保存此管理员密码」 |
| 清除时机 | 用户删除、密码被服务端拒绝、密钥永久失效 | 用户删除、密码被服务端拒绝、密钥永久失效 |

**为什么提权保险箱更严格。** 提权密码一行就能改变宿主机状态（文件删除、服务启停、部署、宿主时区/主机名），且服务端只给 5 分钟、单 capability 的窗口。允许它被「PIN 解锁的长期密钥」保护，等于把设备 PIN 的强度降级为宿主管理员强度。宁可让用户每次手输，也不降级。

### 5.3 不可变安全约束

1. **指纹不产生任何服务端凭据。** 指理解封的只是本地密文；`/auth/login` 与 `/privileged/elevation` 每次仍由服务端用 `IIdentityProvider` 重新验证密码。
2. **服务器不存储密码**（既有原则，不变）。Android 端明文也只在内存中短暂存在：用 `ByteArray`/`CharArray` 承载，提交后立即清零；不进入 `String` 常量池、不进入日志、崩溃报告、分析事件或诊断导出。
3. **不保存 RefreshToken。** 沿用桌面与 M0 规则：iOS/Android 均只在内存中持有 token。
4. **密文与元数据分离。** 密文写入 `noBackupFilesDir`（`allowBackup="false"` 已设置）；Keystore 只保存密钥，不保存数据。
5. **AAD 绑定记录身份。** AES-GCM 的附加认证数据绑定 `vault | serverUrl | account`，防止把 A 服务器的密文挪到 B 服务器条目下复用。
6. **不做跨设备迁移。** 不导出、不云同步、不随系统备份恢复。卸载即失效（Keystore 密钥随应用卸载销毁）。
7. **首次保存必须先验证一次指纹。** 保存动作本身要过一次 `BiometricPrompt`，确保密钥确实受用户生物特征保护、且用户当场能通过；不允许"先存着，等用的时候再说"。
8. **禁止静默提权。** 任何拿到管理员密码后自动重试的操作，都必须由用户在这次交互中点过按钮（指纹或输密码）。不做"失败自动弹指纹"的后台循环。

### 5.4 Android 实现

```text
Keystore                     →  Keystore 之外
┌──────────────────────┐        ┌────────────────────────────────┐
│ AES-256 密钥          │        │ CredentialVault (noBackupDir)   │
│ alias:                │  加解密 │  connection[]: iv + ciphertext  │
│  rk.connection.vault  │◄──────►│  elevation[]:  iv + ciphertext  │
│  rk.elevation.vault   │        │  记录键: serverUrl + account    │
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
| `KeyPermanentlyInvalidatedException` | 清除该保险箱中**受影响的那条记录**，保留服务器与账户，提示重新输入；在账户与安全页标注原因 |
| 设备无强生物识别 / 未录入 | 入口不出现（R4），走密码输入 |

**实现期确认的两处平台约束**

1. **Keystore 没有"弱生物识别"标志位。** `KeyProperties` 只提供 `AUTH_BIOMETRIC_STRONG` 与
   `AUTH_DEVICE_CREDENTIAL`，因此 D3 的时间窗密钥（`DeviceUnlockWindow`）只能声明
   `AUTH_DEVICE_CREDENTIAL or AUTH_BIOMETRIC_STRONG`：强生物识别设备上两者皆可解封，`WEAK_ONLY` 设备上只有
   设备凭据可以。`BiometricPrompt` 提供的认证方式必须与密钥接受的集合同步，否则会出现"提示框成功、解密失败"
   的错配——那会被保险箱当成篡改并删除用户记录。
   若设备只有弱生物识别且从未设置锁屏，窗口密钥无法创建：`VaultKeyManager` 抛 `VaultKeyUnavailableException`，
   `VaultAccess` 归类为 `UnlockFailure.Unavailable`，界面回退到输入密码，且**不删除**任何记录
   （"无法使用"不是"已失效"的证据）。

2. **保存管理员密码失败不取消提权。** 勾选「在本机保存管理员密码」后若这次保存未成功（用户取消指纹、
   保险箱不可用或密钥失效），提权对话框保持打开、勾选被清除并显示原因；用户再按一次「授权」即在**不保存**的
   前提下继续提权。用户显式提出的保存请求不能被静默丢弃，但一次便利性失败也不应中断他已经发起的操作。

### 5.5 交互流程

**首次保存登录凭据**

```text
登录成功
   │
   ▼
「记住此服务器的登录凭据？」
   ├─ 否 ─► 只保存 serverUrl + 登录标识（下次回填账户，仍需输入密码）
   └─ 是 ─► 能力探测
              ├─ 支持强生物识别 ─► BiometricPrompt 验证一次
              │                     ├─ 通过 ─► 写入连接保险箱，标记「指纹保护」
              │                     └─ 取消/失败 ─► 不保存密码，只保存 serverUrl + 标识
              └─ 不支持 ────────► 明确告知「此设备不支持指纹，未保存密码」
```

**指纹登录**

```text
connect/login（简洁模式）
   显示：服务器选择器、登录标识、[使用指纹登录]（主）、[使用密码]（次）
        │
        ▼ 点击指纹
   BiometricPrompt → 解出密码
        │
        ▼
   POST /auth/login { identifier, password, clientPlatform: "android", deviceName, clientVersion }
        │
        ├─ 200 ─► 拉取 ServerDescriptorDto ─► MobileShell
        └─ 401 invalid-credential ─► 删除该条密码（保留 serverUrl + 标识）
                                     ─► 提示「保存的密码已失效，请重新输入」
```

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

一个待确认的**行为差异**（不需要改契约，但需要产品决定）：桌面端 `HostElevationBroker` 只发送 `password`，不发送 `administratorUsername`；移动端为了"保存管理员账户 + 密码"这一对记录，建议**始终发送 `administratorUsername`**，让服务端按显式账户验证，而不是依赖默认账户推断。

### 5.8 决策记录

2026-09-22 已确认（对应 PrivilegedOperations 的决策门文化）：

| 编号 | 决策 | 结论 | 落点 |
| --- | --- | --- | --- |
| D1 | 是否允许在客户端保存宿主管理员密码 | **允许** | 见下方 §5.8.1；已在 [`RelaxKonOS.Security.md`](../../../docs/platform/RelaxKonOS.Security.md) §5.1 登记为受限例外 |
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

## 6. Kotlin 工程落点

下面是**已落地**的目录（与源码逐项对应；尚未落地的部分单列在表后）：

```text
app/src/main/java/app/relaxkonos/mobile/
├─ MainActivity.kt                      # AppCompatActivity 宿主：应用语言/夜间模式、会话裁决、全局叠加层
├─ AppContainer.kt                      # 组合根（含 RelaxKonApplication）
├─ core/
│  ├─ auth/  AuthSession.kt             # SessionState / TokenStore 同文件；401 单次刷新 + 单次重试
│  ├─ layout/LayoutState.kt             # Compact / Medium / Expanded 断点
│  └─ net/   RelaxKonApi.kt             # REST 实现（路由常量按域分组，同文件私有）
│            RelaxKonGateway.kt         # 便于替换的网关接口
│            Models.kt  ApiResult.kt  Wire.kt
├─ security/
│  ├─ BiometricCapability.kt            # STRONG / WEAK_ONLY / DEVICE_CREDENTIAL_ONLY / NONE + 解锁模式映射
│  ├─ VaultKeyManager.kt                # Keystore 密钥生成、失效检测、StrongBox 探测
│  ├─ CredentialVault.kt                # 两个保险箱的读写、AAD 绑定、二进制容器
│  ├─ BiometricUnlock.kt                # BiometricPrompt 封装 + VaultAccess（保存/解封全序列）
│  └─ model/SavedConnection.kt
├─ data/
│  ├─ ConnectionProfileStore.kt
│  ├─ ElevationRepository.kt            # 含 ElevationCoordinator / ElevationAnswer(Provider)
│  ├─ FilesRepository.kt
│  └─ SystemRepository.kt
└─ ui/
   ├─ nav/    Routes.kt  MobileNavigator.kt  MobileNavHost.kt  ShellScaffold.kt
   ├─ connect/ LoginScreen.kt  ConnectionListScreen.kt
   ├─ home/    HomeScreen.kt
   ├─ files/   FilesScreen.kt  FileDetailScreen.kt
   ├─ manage/  ManageScreen.kt  monitor/MonitorScreen.kt  processes/ProcessesScreen.kt
   ├─ more/    MoreScreen.kt  AccountSecurityScreen.kt  ConnectionsScreen.kt
   │           AppearanceScreen.kt  DiagnosticsScreen.kt  AboutScreen.kt
   └─ common/  CommonState.kt  UiMessage.kt  Labels.kt  SectionCard.kt
               ElevationDialog.kt  ConfirmDangerousDialog.kt  ErrorBanner.kt  ProgressSheet.kt
```

尚未落地（按阶段归属，落地时补入上表）：

| 计划文件 | 阶段 | 说明 |
| --- | --- | --- |
| `core/net/ProblemDetails.kt` `RelaxKonJson.kt` | — | 已由 `ApiResult.kt` + `Wire.kt` 覆盖，不单独拆文件 |
| `data/AuthRepository.kt` | — | 认证职责已在 `AuthSession` 内，不另加一层 |
| `data/TerminalRepository.kt`、`ui/terminal/*` | V1-C | 需要 SignalR 客户端与 PTY 渲染 |
| `data/ManageRepository.kt`、`ui/manage/docker|guardian|deployments|webservers/*` | V1-E | Docker、守护工作负载、部署、Web 服务 |
| `ui/files/UploadScreen.kt` | V1-C | 需要网关新增流式上传入口；当前 `RelaxKonGateway` 无上传方法 |
| `security/model/SavedElevationCredential.kt` | — | 管理员凭据即 `VaultRecord`，无需额外模型 |

依赖新增（仅 Android 侧 Gradle，不进 `Directory.Packages.props`）：

| 依赖 | 用途 | 状态 |
| --- | --- | --- |
| `androidx.biometric:biometric` | `BiometricPrompt` / `BiometricManager` 兼容封装 | 已用于 `security/` |
| `androidx.fragment:fragment` | `FragmentActivity` 宿主（`BiometricPrompt` 要求） | 已用于 `MainActivity` |
| `androidx.lifecycle:lifecycle-viewmodel-compose` | 页面状态与意图 | 已用于各目的地状态 |
| `androidx.compose.material:material-icons-core` | 导航项图标 | 已用于 `ui/nav/` |
| `androidx.appcompat:appcompat` | API 33 以下的“应用语言”实现（`AppCompatDelegate.setApplicationLocales`） | 已用于 `MainActivity` + `more/appearance` |
| `androidx.datastore:datastore` | 原子写入 | 未采用：现有实现用 `noBackupFilesDir` 下的临时文件 + `renameTo` 原子提交，`CredentialVault` / `ConnectionProfileStore` 已覆盖该需求 |
| `androidx.navigation:navigation-compose` | 导航图与 save/restore state | 未采用：§4.1 的“每个目的地各自保有栈”由 `ui/nav/MobileNavigator.kt` 直接表达，避免为三条规则引入整张导航图 |
| `androidx.security:security-crypto` | `EncryptedFile` | 未采用：密文已由 Keystore 密钥直接加密，`EncryptedFile` 只会再包一层非必要的加密 |

Manifest 新增：

```xml
<uses-permission android:name="android.permission.USE_BIOMETRIC" />
<!-- API 23–27 旧接口回退，按探测结果决定是否需要 -->
<uses-permission android:name="android.permission.USE_FINGERPRINT" />
```

另有 `android:localeConfig="@xml/locales_config"`（en / zh-CN / ja）与
`androidx.appcompat.app.AppCompatDelegate.autoStoreLocales`（API 33 以下的语言持久化）。

`allowBackup="false"` 保持不动——凭据密文不得随系统备份迁移到其他设备。

---

## 7. 阶段与验收

| 阶段 | 交付 | 退出条件 |
| --- | --- | --- |
| V1-A：认证闭环 | 连接档案、登录、token 刷新、**连接保险箱 + 指纹登录**、登出 | 手机与平板均能用指纹登录；新录指纹后要求重新输入；卸载重装后保险箱为空 |
| V1-B：自适应 Shell | 五项顶级导航、断点布局、能力门控、首页 | 旋转/分屏/重启后布局正确；能力缺失时入口不出现 |
| V1-C：核心操作 | 文件、终端（含真机触摸/IME/软键盘 PoC） | 手机 + 两种平板尺寸完成登录、上传、终端 attach 与断线恢复 |
| V1-D：提权闭环 | 提权对话框、**提权保险箱 + 指纹提权**、危险操作确认 | 受保护路径删除与服务操作经指纹完成；5 分钟窗口内复用、token 刷新后失效均验证 |
| V1-E：管理工作台 | Docker、进程/守护、部署（按能力） | 失败、取消、超时均有可理解状态 |

**必须覆盖的自动化测试**

- `CredentialVault`：加解密往返、AAD 不匹配拒绝、记录键隔离、密钥失效处理、明文清零。
- `BiometricCapability`：四种探测结果的映射与 UI 门控。
- `AuthSession` 状态机：登录成功/失败、401 单次刷新重试、refresh 被拒后的清会话、网络错误不清会话。
- `ElevationRepository`：`elevation-required` → 授权 → 单次重试；jti 变化后授权失效；重试不循环。
- 秘密扫描：断言日志、诊断导出与崩溃报告上下文中不出现密码与 token。

**必须的真机矩阵**：一台手机（竖/横屏）、一台约 8 英寸平板、一台约 11 英寸平板。每台验证：指纹登录（成功/取消/失败/锁定）、指纹提权、软键盘遮挡、旋转、后台恢复、危险操作确认文本。

---

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
