# RelaxKonOS 认证与登录防护建议

## 1. 推荐的整体架构

登录流程建议变成：

```text
客户端
  │ HTTPS
  ▼
┌──────────────────────────┐
│ ① Login Endpoint 限流    │  ← 防请求洪泛 / CPU DoS
└─────────────┬────────────┘
              ▼
┌──────────────────────────┐
│ ② IP / Account 风险检查  │
│ • IP                     │
│ • Account                │
│ • Account + IP           │
└─────────────┬────────────┘
              ▼
┌──────────────────────────┐
│ ③ 登录惩罚 / 冷却判断    │
└─────────────┬────────────┘
              ▼
┌──────────────────────────┐
│ ④ Password Verify        │
│    Argon2id / PBKDF2     │
└─────────────┬────────────┘
              │
       ┌──────┴──────┐
       ▼             ▼
      失败           成功
       │             │
       ▼             ▼
   记录风险      MFA / 设备验证
       │             │
       ▼             ▼
   延迟 / 限制    创建 Session
                     │
                     ▼
                JWT / Token
```

这里最重要的是：**IP、账号、IP + 账号**三个维度一起判断。

只限制 IP，会被代理池绕过；只限制账号，又容易被别人恶意把管理员账号锁死。OWASP 建议登录节流与 MFA 等控制组合使用，并特别提醒账号锁定机制本身可能被攻击者利用制造拒绝服务。

## 2. 不要做传统的“5 次错误永久锁定”

例如下面的设计不推荐：

```text
admin
连续密码错误 5 次
    ↓
账号锁定
    ↓
必须管理员解锁
```

攻击者根本不用知道密码，只需要持续提交：

```text
username = admin
password = random
```

连续请求几次，就能让真正的管理员无法连接自己的服务器。尤其 RelaxKonOS 本身是服务器管理入口，这种 DoS 风险比普通网站更严重。

更推荐：**递增冷却 + IP 限流 + Account 限流 + 高风险时强制 MFA**。

也就是说，攻击越久，猜密码速度越慢，但一般不会仅因为远程攻击而把账号永久封死。

## 3. 四层登录限流

可以按下面的初始参数实现，之后再允许管理员修改。

| 防护层 | 建议策略 | 目的 |
| --- | --- | --- |
| Login Endpoint | 每 IP 约 10 次/分钟，允许小突发 | 防 HTTP 洪泛 |
| IP | 5 分钟内约 30 次失败开始限制 | 防单 IP 暴力破解 |
| Account + IP | 连续失败逐级冷却 | 防针对某账号攻击 |
| Account 全局 | 检测多个 IP 同时攻击同一账号 | 防代理池暴破 |

第一层直接使用 ASP.NET Core 自带的 Rate Limiting Middleware 就很好。.NET 10 当前支持 Fixed Window、Sliding Window、Token Bucket 和 Concurrency limiter，也支持按照 IP、用户等 partition 分桶。

但不要只依赖 ASP.NET RateLimiter，因为它更适合作为 HTTP 层保护；真正的账号安全状态需要 RelaxKonOS 自己维护。

## 4. 核心：递增式惩罚

例如用户 `admin` 在短时间内连续登录失败：

| 连续失败次数 | 行为 |
| --- | --- |
| 1～4 | 正常返回 |
| 5 | 等待约 2 秒 |
| 6 | 等待约 5 秒 |
| 7 | 等待约 15 秒 |
| 8 | 冷却 30 秒 |
| 9 | 冷却 1 分钟 |
| 10 | 冷却 5 分钟 |
| 11～14 | 冷却逐渐增加 |
| ≥15 | 例如冷却 30～60 分钟 + 安全事件 |

这些数字不是协议标准，可以作为 RelaxKonOS 默认值，并通过配置调整。

攻击速度将变成：

```text
正常：       10,000 次 / 分钟
    ↓
限流后：        10 次 / 分钟
    ↓
持续攻击后：     1 次 / 数分钟
```

于是在线暴力破解基本失去意义。NIST 最新 SP 800-63B 也要求密码验证器实现针对失败认证的 rate limiting，并明确认可随着失败次数增加等待时间的做法。

## 5. 同时计算三个风险计数器

假设发生登录尝试：

```text
IP:       203.0.113.10
Username: admin
```

RelaxKonOS 应同时产生：

```text
IP:        203.0.113.10
Account:   admin
AccountIP: admin + 203.0.113.10
```

这样可以识别三类攻击。

### 场景 A：普通暴力破解

```text
IP A
  ↓
admin
admin
admin
admin
```

`AccountIP` 和 `IP` 都快速增加，直接限速。

### 场景 B：代理池攻击

```text
IP A → admin
IP B → admin
IP C → admin
IP D → admin
```

虽然每个 IP 都只有一次，但是 `Account(admin)` 的失败次数一直增长，因此仍然进入冷却。

### 场景 C：Password Spraying

```text
admin         → Password123
root          → Password123
administrator → Password123
test          → Password123
user          → Password123
```

这时单个账号失败次数不高，但是 IP failure count 会高速上涨，因此 IP 被限制。这就是为什么三个维度缺一不可。

## 6. 账号不存在时，也要执行类似逻辑

不要分别返回“用户名不存在”和“密码错误”，统一返回类似：

> 用户名或密码错误

否则攻击者可以先扫描真实账号，再只攻击它们。OWASP 明确建议避免通过错误信息、HTTP 状态或行为差异泄露账号是否存在。

RelaxKonOS 内部还应准备一个 `DummyPasswordHash`：

```csharp
if (user == null)
{
    Verify(password, DummyPasswordHash);
}
```

让“用户不存在”和“密码错误”的计算时间尽量接近，避免基于时序的用户名枚举。不过前面必须先有 IP Rate Limiter，否则攻击者可以利用昂贵的密码 Hash 验证把 CPU 打满。

## 7. 增加“可信设备”

RelaxKonOS 不是普通网站，而是有固定桌面客户端的服务器管理软件，因此非常适合利用可信设备。

首次成功登录时，客户端生成 `DeviceKeyPair`：

```text
Private Key
  ↓
保存在客户端安全存储

Public Key
  ↓
RelaxKonOS Server
```

后续登录使用 Challenge–Response：

```text
Server
  ↓ 随机 Challenge
Client 使用 Private Key 签名
  ↓
Server 使用 Public Key 验证
```

这样攻击者即使猜到 `username` 和 `password`，仍然缺少 Device Private Key。

安全中心可以展示可信设备，例如：

```text
可信设备

Nanami-PC
Windows
最后登录：2026-08-28
IP: xxx.xxx.xxx.xxx

Laptop
Linux
最后登录：2026-08-27

[撤销设备]
```

## 8. MFA 应作为正式能力

建议第一版至少支持：

- TOTP
- Recovery Codes

以后再增加：

- Passkey / FIDO2

当 RelaxKonOS Server 直接暴露在公网时，可以提供：

```text
InternetExposureMode = true
```

开启后，管理员账号必须使用 MFA，或者至少在客户端显示强烈安全建议。OWASP 将 MFA 视为抵御绝大多数密码类攻击最有效的措施之一。

RelaxKonOS 的用户数量通常不会像大型 SaaS 那么夸张，因此 MFA 的部署成本实际上很低。

## 9. 密码策略

如果现在还是下面这种规则：

```text
必须 8 位
必须大写
必须小写
必须数字
必须特殊符号
```

建议改掉。最新 NIST SP 800-63B 对仅使用密码作为单因素认证的密码要求最低 15 个字符；如果密码只是 MFA 的一个因素，可以最低 8 个字符。同时建议允许至少 64 字符，并禁止常见或泄露密码，而不是强迫用户满足“大写 + 数字 + 特殊字符”之类的组合规则。

RelaxKonOS 可以采用：

| 场景 | 最低要求 |
| --- | --- |
| Password only | 最低 15 字符 |
| Password + MFA | 最低 8 字符，建议 12+ |

同时维护常见密码 BlockList，例如：

```text
password
password123
123456789
qwerty...
admin123
relaxkonos
relaxkonos123
用户名本身
...
```

不用自己维护几亿条，主要阻止最容易被在线猜中的密码即可。

## 10. 密码存储

如果准备顺便重构认证模块，建议把密码 Hash 算法封装：

```text
IPasswordHasher
├── Argon2idPasswordHasher
└── LegacyPasswordHasher
```

新密码优先使用 `Argon2id`。OWASP 当前推荐优先使用 Argon2id，并给出了最低参数基线；如果有 FIPS 等要求，则可以采用 `PBKDF2-HMAC-SHA256`。

这样可以在用户成功登录后自动迁移：

```text
旧账号：PBKDF2
    ↓ 用户成功登录
自动重新 Hash
    ↓
新算法：Argon2id
```

不需要强制所有用户一次性修改密码。

## 11. 登录状态的数据模型

不要往 `User` 表里塞大量认证状态字段，而是单独设计：

```text
User
└── AuthenticationState

AuthenticationProtection
├── AccountFailureState
├── IpFailureState
├── TrustedDevice
├── MfaCredential
└── SecurityEvent
```

例如账号状态：

```csharp
AccountFailureState
{
    UserId
    FailureCount
    FirstFailureAt
    LastFailureAt
    PenaltyLevel
    BlockedUntil
    LastSuccessfulLoginAt
}
```

IP 短时间状态可以放在 `MemoryCache`，账号冷却状态建议持久化：

```text
攻击者疯狂尝试
    ↓
RelaxKonOS 重启
    ↓
计数全部清空  ← 不应发生
```

RelaxKonOS 目前是单机管理模式的话，第一版完全没必要引 Redis；现有数据库或者 SQLite 就足够了。以后如果有多节点 RelaxKonOS Server，再抽象为 distributed store。

## 12. 成功登录后不要简单删除所有记录

例如：

```text
14 次失败
    ↓
第 15 次成功
```

可以重置：

```text
AccountFailureCount = 0
Penalty = 0
```

但安全审计记录仍应保存：

```text
SecurityEvent
├── AuthenticationSucceeded
├── AuthenticationFailed
├── AuthenticationRateLimited
├── AuthenticationBlocked
├── NewDeviceLogin
├── MfaFailed
└── SuspiciousLogin
```

客户端可以据此做一个很有价值的“安全中心”，例如：

```text
过去 24 小时

失败登录        317
被阻止请求      286
攻击来源 IP      12
受攻击账号        1

admin
317 次失败登录
主要来源：
103.x.x.x
43.x.x.x
185.x.x.x
```

对于服务器管理器来说，这个功能会非常实用。

## 13. 客户端不应知道具体阻止原因

服务器内部可以判断：

```text
ACCOUNT_RATE_LIMITED
IP_RATE_LIMITED
INVALID_PASSWORD
UNKNOWN_ACCOUNT
DEVICE_BLOCKED
```

但登录客户端尽量只显示：

> 登录失败，请检查凭据后重试。

如果确实已经进入冷却，可以显示：

> 登录尝试过于频繁，请稍后重试。

服务器返回：

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 30
```

ASP.NET Core Rate Limiting 也原生支持 `Retry-After` 这套处理方式。客户端可以直接显示：

```text
登录尝试过于频繁
请在 27 秒后重试

[登录]  ← 按钮倒计时禁用
```

但客户端倒计时只是 UX，真正限制必须全部在 Server。攻击者不会使用 Flutter/Avalonia UI，而是直接用 curl 调用登录接口。

## 14. IP 获取必须特别小心

如果 RelaxKonOS 部署为：

```text
Internet
  ↓
Nginx
  ↓
RelaxKonOS
```

就不能无条件相信 `X-Forwarded-For`。否则攻击者可以通过伪造多个 header IP 绕过限流。

RelaxKonOS 应设计并配置：

```text
TrustedProxies
TrustedNetworks
```

只有请求来自配置好的 Nginx、IIS 或 Caddy 时，才解析 forwarded headers；否则直接使用：

```csharp
HttpContext.Connection.RemoteIpAddress
```

作为来源。这部分以后可以和 Nginx 管理器结合起来。

## 15. 管理员被攻击时如何恢复

RelaxKonOS 必须始终保留本机恢复路径。服务器本地可执行：

```bash
relaxkonos auth status
```

查看：

```text
admin
Temporary blocked until ...
MFA enabled
Trusted devices: 2
```

然后执行：

```bash
sudo relaxkonos auth unlock admin

# Windows
RelaxKonOS.Server.exe auth unlock admin
```

要求 `root` 或 `Administrator` 权限。这样即使认证配置搞坏了，只要有服务器本机、root 或控制台权限，就始终有本地恢复路径。

对于服务器管理软件，这条应成为架构原则。

## 16. 最终建议的登录防御模型

- HTTPS 强制保护凭据传输；登录接口先做低成本 IP Rate Limit，避免密码 Hash 被用来打 CPU DoS。
- IP + Account + IP/Account 三维计数，同时抵抗暴力破解、代理池和 password spraying。
- 递增式冷却，而不是轻易永久锁号，避免攻击者利用锁定机制攻击管理员。
- 统一登录失败响应 + Dummy Hash，避免枚举用户名。
- 公网管理账号推荐或强制 MFA，第一版采用 TOTP + Recovery Codes。
- 增加 Trusted Device / Device Key Pair，利用 RelaxKonOS 桌面客户端的优势。
- 密码 BlockList + 长密码策略 + Argon2id/PBKDF2，解决弱密码与离线破解问题。
- 所有失败、限流、异常设备进入 `SecurityEvent`，以后直接做成 RelaxKonOS“安全中心”。
- 登录限制状态部分持久化，服务器重启不能成为清空防护的手段。
- 保留 root/Administrator 本机恢复命令，保证管理员永远有 break-glass 路径。

如果实际落地，建议做成独立模块：

```text
RelaxKonOS.Server
└── Security
    └── Authentication
        ├── LoginService
        ├── PasswordService
        ├── AuthenticationProtectionService
        ├── LoginRiskEvaluator
        ├── AccountThrottleService
        ├── IpThrottleService
        ├── MfaService
        ├── TrustedDeviceService
        ├── RecoveryService
        └── SecurityEventService
```

这样以后登录保护、MFA、设备管理、安全日志和权限系统可以很好地连起来，而不会把认证逻辑全部堆进 Controller。
