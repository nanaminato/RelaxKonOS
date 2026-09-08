# RelaxKonOS 注册表与配置同步架构

> 本文档定义 RelaxKonOS 的服务端注册表（Registry）架构：它如何成为可编辑配置的期望状态真源、如何在多用户/多设备下隔离，以及如何将变更安全地同步为有效配置与运行时状态。
>
> **状态：第一阶段已实现。** 已提供按用户/作用域隔离的持久化注册表、既有 Workspace 配置导入和可编辑的内置注册表应用。同步状态仅作为服务端实现细节，不在用户界面展示。

- Workspace 的用户与设备模型见 [`RelaxKonOS.Workspace.md`](./RelaxKonOS.Workspace.md)
- 当前持久化边界见 [`RelaxKonOS.Storage.md`](../platform/RelaxKonOS.Storage.md)
- 应用私有设置现有契约见 [`RelaxKonOS.AppSettings.md`](../development/RelaxKonOS.AppSettings.md)
- 设置中心见 [`RelaxKonOS.Settings.md`](../desktop/RelaxKonOS.Settings.md)

---

## 1. 目标与边界

注册表是 RelaxKonOS 中一部分**配置型持久化数据**的统一控制面，提供树状浏览、受控编辑、版本化审计、延迟同步和重启提示。它不是对 SQLite 表或宿主操作系统注册表的直接映射。

目标：

- 修改首先写入持久化注册表并标记同步状态，而不是直接写入既有业务实体。
- 每个注册表项由 schema 定义值类型、校验、权限、同步处理器和生效策略。
- 同步失败可显示、重试、回滚；服务重启不会丢失未同步修改。
- 多用户、多 Workspace、多设备的配置和同步状态完全隔离。
- 通过内置注册表应用查看、修改和管理受支持配置。

非目标：

- 不提供对任意数据库表、任意 JSON 文档或宿主 Windows Registry 的通用编辑入口。
- 不把会话、业务数据或机密数据伪装成配置项。
- 不以注册表应用绕过既有高风险操作的授权与专用工作流。

---

## 2. 三层状态模型

```text
Registry Desired State（持久化、可编辑）
        │ 写入后标记 PendingSync
        ▼
Registry Sync Worker（校验、排序、重试、审计）
        ▼
Effective State（现有领域存储 / SQLite 投影）
        ▼
Runtime State（应用、Shell、服务进程）
```

| 层 | 职责 | 示例 |
|---|---|---|
| Desired State | 用户希望使用的配置；注册表的唯一写入真源 | `Theme=Dark` |
| Effective State | 已通过同步器校验、可供既有服务读取的配置投影 | `Workspace.Preferences.Theme=Dark` |
| Runtime State | 当前进程实际采用的配置 | 已打开 Shell 的主题资源 |

一个配置项可已经同步到 Effective State，但仍未反映到 Runtime State。例如浏览器设置在下次打开浏览器才生效；需要重启的服务只显示待重启，不由注册表自动重启。

**规则：**接入注册表后的普通 `PUT` API 也必须经由 `RegistryWriter` 写 Desired State。禁止 API 与同步器各自直接写同一个有效配置，避免双真源和覆盖顺序不确定。

---

## 3. 多用户、Workspace 与设备隔离

RelaxKonOS 当前身份模型为 `User → Workspace → Session ← Device`；默认一个 User 拥有一个持久 Workspace。注册表必须延续该所有权边界，而不能以全局路径存储用户设置。

```text
User Alice
  └─ Workspace A
       ├─ Workspace-scoped registry values
       └─ Device A1 / Device A2 scoped values

User Bob
  └─ Workspace B
       └─ Workspace-scoped registry values
```

注册表语义采用 Windows `HKCU` 的“当前用户配置单元”思想，但数据库中显式保存所有者：

```text
Hive:     CurrentUser
UserId:   JWT subject
Scope:    User | Workspace | Device
ScopeId:  UserId | WorkspaceId | DeviceId
Path:     Software\\RelaxKonOS\\...
Name:     value name
```

`UserId` 是强制租户边界；服务端从 JWT 解析 `ScopeId`，客户端不得指定或伪造。任意读取、写入、同步记录、审计记录和“待重启”标记都必须携带 `UserId` 与 `ScopeId`。

建议唯一键：

```text
(UserId, Scope, ScopeId, Path, Name)
```

结果是 Alice 修改主题只影响 Alice 的 Workspace；Bob 的主题、同步队列、失败记录和重启提醒均独立。多个设备连接同一 Workspace 时共享 Workspace 范围设置；仅属于设备的 UI/硬件偏好使用 Device 范围。

---

## 4. 注册表数据模型

### 4.1 `registry_entries`

| 字段 | 说明 |
|---|---|
| `UserId`, `Scope`, `ScopeId` | 所有者与作用域，组成隔离边界 |
| `Path`, `Name` | 逻辑注册表位置与值名 |
| `ValueType`, `ValueJson` | 强类型值；JSON 仅为传输/存储形式，不代表无约束 |
| `Revision` | 单项乐观并发版本 |
| `State` | 当前同步状态 |
| `DesiredUpdatedAt`, `DesiredUpdatedBy` | 最后期望值写入信息 |
| `AppliedRevision`, `AppliedAt` | 最近成功投影的版本与时间 |
| `LastErrorCode`, `LastErrorMessage` | 最近一次同步失败的安全错误摘要 |

### 4.2 `registry_changes`

追加式变更与审计日志，不以覆盖 `registry_entries` 代替历史。记录 ChangeId、Entry 标识、旧/新值摘要、来源（普通 API / 注册表应用 / 迁移）、操作者、时间、目标 Revision、尝试次数和处理结果。

值历史应设保留策略；对敏感值只保存掩码或不可逆摘要。机密本身不得作为注册表值。

### 4.3 `registry_schema`

schema 是代码中注册的白名单，不允许用户创建任意可同步路径。每个定义包含：

- 路径与值名匹配规则、允许的 Scope；
- 值类型、默认值、长度/范围/枚举校验；
- 所需权限；
- `IRegistryProjectionHandler` 同步处理器；
- `ApplyMode` 生效策略；
- 是否可读、可写、可删除、是否应在审计中脱敏。

---

## 5. 同步与一致性

### 5.1 写入流程

```text
注册表应用 / 既有配置 API
  → 授权 + schema 校验 + If-Match 校验
  → 原子写入 registry_entries 和 registry_changes
  → State = PendingSync
  → 返回 Desired State 与同步状态
```

写入请求只承诺 Desired State 已持久化；不承诺运行时已经生效。

### 5.2 同步流程

`RegistrySyncWorker` 后台消费 PendingSync 项：

1. 按 `(UserId, Scope, ScopeId, 配置域)` 串行处理，避免同一配置域乱序覆盖。
2. 读取目标 Revision，并再次执行 schema/依赖校验。
3. 调用对应 Projection Handler，在一个数据库事务中更新 Effective State 与注册表 Applied 信息。
4. 若 Desired Revision 在处理期间已改变，放弃旧结果并处理新版本。
5. 根据 `ApplyMode` 触发运行时刷新或标记需重启。

同步器须具备启动恢复、指数退避和显式“立即重试”入口。失败不回滚用户的 Desired State；用户可以修改、重试或恢复到历史版本。

### 5.3 同步状态

| 状态 | 含义 |
|---|---|
| `Synced` | Desired Revision 已投影，运行时无需额外操作或已刷新 |
| `PendingSync` | 已持久化，等待处理 |
| `Applying` | 正在同步；崩溃恢复后重新判定 |
| `Failed` | 最近处理失败；保留期望值、错误摘要与重试入口 |
| `RestartRequired` | 已投影，但目标应用/服务仍需重启或重载 |
| `Superseded` | 旧 Change 已被同项更新版本替代 |

---

## 6. 生效策略

每个 schema 项指定 `ApplyMode`；注册表应用必须展示该信息。

| ApplyMode | 行为 | 示例 |
|---|---|---|
| `Immediate` | 同步成功后通知运行时刷新 | Shell 主题、时钟格式 |
| `RestartApplication` | 投影成功，标记指定 RelaxKonOS 应用需关闭并重开 | 浏览器设置、终端外观 |
| `ReloadService` | 仅调用已受控的 reload 操作；失败进入 `Failed` | 未来可重载的服务配置 |
| `RestartServer` | 只标记服务端待重启，绝不由注册表自动重启 | 影响服务启动参数的配置 |

重启要求按 `(UserId, Scope, ScopeId, Target)` 保存或从状态推导，确保 Alice 的待重启应用不会显示给 Bob。

---

## 7. 首批接入范围

| 注册表域 | Effective State 投影 | Scope | ApplyMode |
|---|---|---|---|
| `Workspace\\Terminal\\Appearance` | `Workspace.TerminalSettings` | Workspace | `RestartApplication` |
| `Workspace\\Desktop\\Preferences` | `Workspace.Preferences` | Workspace | 多数 `Immediate`；个别项按 schema 指定 |
| `Workspace\\Browser\\Settings` | `Workspace.BrowserSettings` | Workspace | `RestartApplication` |
| `AppSettings\\{appId}\\{key}` | `app_settings` | User / Workspace / Device | 应用声明 |
| `User\\ImageMirrors` | `image_mirrors` | User | 下次 Docker 操作使用 |

第一阶段不接入：身份与会话、认证防护状态、refresh token、数据保护密文、隧道密钥、证书私钥、浏览器书签/历史、Git 仓库与操作日志。防火墙、网络、服务启停、证书签发等高影响操作继续使用专用 API；未来若纳入，只能作为受控命令型配置而不是可任意编辑的底层记录。

---

## 8. 注册表应用

内置应用 `relaxkonos.registry` 是唯一拥有全局注册表浏览体验的客户端。

- 左侧显示授权范围内的注册表树，右侧显示值名、期望值、有效值、类型、版本、状态与生效方式。
- 编辑使用 schema 驱动的控件与校验；未知、只读或无权限路径不可编辑。
- 提供同步现在、重试失败项、撤销未同步修改、从历史版本恢复等操作。
- 展示变更审计与安全错误摘要；不显示机密值。
- 顶部显示当前用户范围内的待同步和待重启数量。

应用权限新增：

```text
server.registry.read
server.registry.write
```

这两项能力默认只授予内置注册表应用。外置应用不能获得任意注册表路径访问权；如需接入，应使用其受限的应用命名空间和既有应用权限模型。

---

## 9. 迁移与实施顺序

1. 建立 Protocol 契约、注册表表、schema 注册、读 API 和只读注册表应用；启动时将当前配置导入为 `Synced` 的初始 Desired State。
2. 实现 `RegistryWriter`、`RegistrySyncWorker` 与 Workspace/应用设置的 Projection Handler；将既有配置 `PUT` 端点改为写注册表，保持外部 API 契约不变。
3. 启用编辑、审计、失败重试、历史恢复、待重启提示和应用内刷新通知。
4. 在验证隔离、并发与恢复行为后，逐项加入更多低风险配置域。

迁移期间的唯一真源切换必须按配置域完成：某个域一旦接入，所有写路径都改经 `RegistryWriter`；未接入域继续沿用现有仓储与端点。不得长期让同一配置域同时存在直接写和注册表写两条路径。

---

## 10. 验收条件

- 两个不同用户修改同名配置时，注册表项、同步队列、有效配置和重启提示均互不影响。
- 同一用户的两个设备共享 Workspace 范围值，但可拥有独立 Device 范围值。
- 服务重启后 `PendingSync`、`Failed` 与 `RestartRequired` 状态可恢复并继续处理。
- 并发编辑相同项时，`If-Match`/Revision 明确产生冲突而不静默覆盖。
- 所有注册表写入均可追溯操作者、时间、来源与处理结果。
- 无权限用户、未知路径、无效值和机密路径均不能通过注册表应用或 API 写入。
