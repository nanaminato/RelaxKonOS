# RelaxKonOS Alias Login / 独立登录别名认证 Goal

请先阅读并分析当前 RelaxKonOS 项目中与以下内容相关的现有实现：

- Authentication / Login
- System User Authentication
- User / Account
- Session
- Authorization / Permission
- Settings
- RelaxKonServer
- PrivilegedHelper（如相关）
- Linux / Windows 系统用户相关实现
- Token / Cookie / JWT / Refresh Token（如果存在）
- 前端 RelaxKon 中当前登录页面、设置页面和认证状态管理

不要立即修改代码。

请基于当前项目实际代码、项目结构和现有设计，编写一份新的 Goal 模式设计文档，用于指导后续实现 **Alias Login（登录别名）** 功能。

---

# 1. Goal

RelaxKonOS 当前主要依赖操作系统账号进行身份认证。

希望增加一种新的登录机制：

1. 用户第一次仍然使用真实的操作系统账号完成认证。
2. 登录成功后，可以进入：

   `Settings → Account / Security → Login Alias`

3. 用户可以创建：

   - Login Alias
   - Alias Password

4. 创建完成后，RelaxKonOS 可以允许：

   `Alias + Alias Password`

   登录。

5. Alias 登录成功后，仍然映射到原来的系统用户。

例如：

```text
Linux System User
nanami
        │
        ▼
RelaxKonOS Identity
SystemUser = nanami
        │
        ├── Alias = developer
        └── AliasPassword = ********
```

之后：

```text
developer + AliasPassword
        ↓
RelaxKonOS Authentication
        ↓
Canonical Identity = nanami
        ↓
Workspace / Files / Permissions / Applications
```

因此 Alias **不是一个新的系统用户，也不是新的 RelaxKonOS 用户**。

Alias 只是 RelaxKonOS 层面的登录凭据。

---

# 2. Important Identity Principle

必须严格区分：

```text
System Identity
        ≠
Login Credential
```

系统用户仍然是真正的身份主体。

例如：

```text
UserId
SystemUserName
SystemUid / SID
HomeDirectory
Permissions
Workspace
File Ownership
```

都继续关联真实系统用户。

Alias 仅用于认证。

建议模型：

```text
System User
    │
    ├── Canonical Identity
    │
    └── Login Credentials
            ├── System Credential
            └── Alias Credential
```

不要因为创建 Alias 而创建新的 Linux User / Windows User。

---

# 3. Disable Original Login

用户创建 Alias 后，可以选择：

```text
Disable system account login in RelaxKonOS
```

开启后：

```text
nanami + SystemPassword
```

不能再通过 RelaxKonOS 登录。

但是：

```text
developer + AliasPassword
```

仍然可以登录，并映射至：

```text
SystemUser = nanami
```

这里的“Disable System Login”必须仅表示：

> 禁止该系统账号作为 RelaxKonOS 的直接登录凭据。

不得执行：

```bash
usermod -L
passwd -l
```

也不得：

- 禁用 Windows Account
- 修改 Linux PAM 用户状态
- 修改系统密码
- 修改 SSH 登录状态
- 修改 SMB/SFTP/FTP 登录状态
- 修改 sudo 权限

即：

```text
Disable System Login
```

是 **RelaxKonOS Authentication Policy**，

而不是：

```text
Disable Operating System Account
```

请在设计文档中特别说明这个安全边界。

---

# 4. Desired Login Flow

需要设计类似：

```text
POST /api/auth/login
```

用户输入：

```text
Identifier
Password
```

其中 Identifier 可能是：

```text
System Username
```

或者：

```text
Alias
```

认证流程可以概念化为：

```text
Identifier
    │
    ▼
Resolve Login Identity
    │
    ├── Alias?
    │      │
    │      ├── Yes
    │      │    ↓
    │      │ Alias Credential Verification
    │      │    ↓
    │      │ Resolve Canonical System User
    │      │
    │      └── No
    │           ↓
    │       System Account
    │           ↓
    │       Check whether System Login is enabled
    │           ↓
    │       Existing OS Authentication
    │
    ▼
Canonical User Identity
    │
    ▼
Existing Session / Token Creation
```

请结合当前代码判断最适合在哪里进行 Identifier Resolution。

---

# 5. Alias Credential Storage

Alias Password 不能明文保存。

请分析项目当前是否已经存在：

- PasswordHasher
- ASP.NET Core Identity PasswordHasher
- Argon2
- bcrypt
- PBKDF2
- 数据保护 API
- Secret Store

如果存在，应尽量复用已有基础设施。

如果不存在，请提出适合 RelaxKonOS 的安全方案。

例如可以设计：

```text
AliasCredential
{
    UserId
    Alias
    PasswordHash
    CreatedAt
    UpdatedAt
    SystemLoginEnabled
}
```

但不要机械照搬这个结构。

首先分析项目已有 User / Settings / Storage 模型，再决定数据应该放在哪里。

文档必须说明：

- Password Hash Algorithm
- Salt
- 是否需要 Pepper
- Password Change
- Password Reset
- Credential Revocation
- Failed Login Handling
- Rate Limiting
- Audit Logging

---

# 6. Alias Rules

分析并设计 Alias 的约束。

至少考虑：

```text
长度
允许字符
大小写敏感性
Unicode
空格
系统用户名冲突
其他 Alias 冲突
保留名称
```

尤其需要解决：

```text
系统中存在用户 developer
```

同时：

```text
nanami 的 Alias = developer
```

这种 Identifier 冲突。

原则上登录 Identifier 必须能够被唯一解析。

请设计明确的冲突策略。

例如可以考虑：

```text
Alias 和系统用户名共用一个 namespace
```

也可以提出更合理的方案，但必须解释理由。

---

# 7. Settings UI

请结合当前 RelaxKon 前端 Settings 的实际结构设计 UI。

建议概念结构：

```text
Settings
└── Account & Security
    └── Login
        ├── System Account
        │   nanami
        │
        ├── Login Alias
        │   developer
        │
        ├── Change Alias
        ├── Change Alias Password
        │
        └── Allow system account login
            [ ON / OFF ]
```

第一次创建 Alias 时：

```text
Create Login Alias
Alias:
Password:
Confirm Password:
```

对于敏感操作，应考虑是否要求：

```text
Re-authentication
```

例如：

- 创建 Alias
- 修改 Alias
- 修改 Alias Password
- 禁用 System Login
- 删除 Alias

请结合当前认证模型设计，而不是单独添加一个 UI。

---

# 8. Safety Requirements

必须重点分析以下安全问题。

## 8.1 防止账号锁死

如果：

```text
Alias 未正确创建
```

不得允许用户直接：

```text
Disable System Login
```

必须保证至少存在一个可工作的登录方式。

考虑：

```text
Alias created
        ↓
Alias password verified
        ↓
Only then allow disabling system login
```

---

## 8.2 Recovery

请设计账号恢复策略。

例如：

- Server console recovery
- Administrator recovery
- Re-enable system login
- Delete alias credential
- Recovery code

但不要擅自增加过于复杂的账号系统。

优先寻找和复用当前 RelaxKonOS 已有管理能力。

---

## 8.3 Alias Enumeration

登录 API 不应泄露：

```text
Alias exists
System user exists
Password wrong
System login disabled
```

给攻击者。

外部响应应尽量统一，例如：

```text
Invalid username or password.
```

内部日志可以记录实际失败原因。

---

## 8.4 Brute Force Protection

分析当前是否已经存在：

- Rate Limiter
- Login attempt tracking
- IP rate limiting
- Account rate limiting

如果没有，请在 Goal 中提出最小实现。

---

# 9. Existing OS Authentication Must Remain

Alias 功能不能替代当前系统认证模块。

期望架构应该类似：

```text
Authentication
│
├── SystemAuthenticationProvider
│
└── AliasAuthenticationProvider
```

或者根据当前代码设计更合适的抽象。

不要为了 Alias Login 大规模重写现有 OS Authentication。

优先：

```text
small
isolated
extensible
backward-compatible
```

的改造。

---

# 10. Authorization Must Not Change

Alias Login 只改变：

```text
Authentication
```

不得改变：

```text
Authorization
```

Alias 登录后得到的身份必须与原系统账户登录完全一致。

例如：

```text
nanami
```

通过系统认证登录：

```text
UserId = X
```

通过：

```text
developer
```

Alias 登录：

```text
UserId = X
```

必须是同一个 Canonical User。

因此以下内容不能产生两份：

- Workspace
- Settings
- App State
- Permissions
- File ownership
- Terminal sessions
- Docker permissions
- SSH configuration
- File Service permissions
- Audit identity

---

# 11. Session Behavior

请分析当前 RelaxKonOS Session / Token 机制。

Alias 登录后生成的 Session 应保存：

```text
Canonical UserId
```

而不是依赖 Alias 本身。

可以额外记录：

```text
AuthenticationMethod = Alias
```

或者：

```text
AuthenticationMethod = System
```

用于：

- Security UI
- Audit
- Session Management

但 Alias 改名后：

```text
已有 Session
```

不应该因此失效，除非安全策略明确要求。

修改 Alias Password 是否应该 revoke existing sessions，请在文档中分析并做出决策。

---

# 12. API Design

基于当前 RelaxKonServer 的 API 风格，设计所需 API。

不要预先假定路径。

请先检查现有 Controller / Endpoint / Minimal API 结构。

功能至少需要覆盖：

```text
Get alias configuration
Create alias
Change alias
Change alias password
Delete alias
Enable/disable system login
```

对于所有敏感 API：

- 必须认证
- 必须验证当前用户
- 不得允许操作其他用户
- 根据需要进行 re-authentication

如果管理员功能已有统一模型，可以复用，但不要为本功能额外扩大管理员权限。

---

# 13. Storage

请检查当前 RelaxKonOS 是否：

- 使用数据库
- 使用 JSON
- 使用 SQLite
- 使用用户配置文件
- 使用自定义 Persistent Store

根据现有架构决定 AliasCredential 放置位置。

目标不是引入新的大型依赖。

如果当前项目没有数据库，不应仅仅因为这个功能强制引入完整数据库系统。

同时必须考虑：

```text
Linux
Windows
```

跨平台行为。

---

# 14. Audit

建议敏感操作记录 Security Audit：

```text
AliasCreated
AliasChanged
AliasPasswordChanged
AliasDeleted
SystemLoginDisabled
SystemLoginEnabled
AliasLoginSucceeded
AliasLoginFailed
```

但日志中不得记录：

```text
Password
PasswordHash
Credential Secret
```

Alias 是否完整记录，请结合当前隐私日志策略决定。

---

# 15. Migration / Backward Compatibility

这是一个已有系统，因此必须设计升级行为。

升级后已有用户默认应该：

```text
Alias = none
SystemLoginEnabled = true
```

即：

```text
当前用户完全不受影响
```

只有用户主动创建 Alias 后，新功能才生效。

不得因为升级 RelaxKonOS 而导致现有用户无法登录。

---

# 16. Multi-platform Considerations

分别分析：

## Linux

当前系统认证可能涉及：

- PAM
- passwd
- shadow
- system user
- sudo / PrivilegedHelper

Alias 不应修改 Linux account。

## Windows

当前系统认证可能涉及：

- Windows Account
- SID
- Local Account / Domain Account
- Logon API
- Windows Service

Alias 不应创建或者修改 Windows User。

Alias Login 最终仍然解析至原始 Windows identity / SID。

请根据项目当前实际支持情况进行分析，不要假设不存在的功能。

---

# 17. Threat Model

Goal 文档中增加一个简短 Threat Model。

至少分析：

```text
Alias guessing
Brute force
Credential database theft
Username/Alias enumeration
Alias collision
Session theft
CSRF
Unauthorized settings modification
User locking themselves out
Privilege escalation
Canonical identity confusion
```

重点检查是否可能出现：

```text
Alias A
        ↓
错误映射
        ↓
System User B
```

这种身份混淆问题。

Canonical UserId 必须是系统内部唯一且稳定的身份依据。

---

# 18. Expected Architecture

请基于实际项目代码给出推荐架构。

概念上可能类似：

```text
                     Login Request
                          │
                          ▼
                 LoginIdentifierResolver
                          │
              ┌───────────┴───────────┐
              ▼                       ▼
     Alias Authentication      System Authentication
              │                       │
              └───────────┬───────────┘
                          ▼
                  Canonical Identity
                          │
                          ▼
                  Session / Token
                          │
                          ▼
                     RelaxKonOS
```

但这只是概念参考。

最终设计必须根据当前项目已有类、接口、服务和依赖关系进行调整。

不要为了匹配该图而创建不必要的抽象。

---

# 19. Deliverable

请生成一个 Goal 文档，例如：

```text
docs/goals/alias-login.md
```

如果当前项目已经有统一的 Goal 文档目录或命名规则，请遵循现有规则。

文档应至少包括：

1. Background
2. Current State
3. Goal
4. Non-Goals
5. Terminology
6. Existing Architecture Analysis
7. Proposed Architecture
8. Identity Model
9. Authentication Flow
10. Alias Credential Model
11. System Login Disable Policy
12. API Design
13. Frontend / Settings UX
14. Storage
15. Session Behavior
16. Security Requirements
17. Threat Model
18. Migration
19. Linux Considerations
20. Windows Considerations
21. Error Handling
22. Audit
23. Implementation Phases
24. Testing Strategy
25. Acceptance Criteria
26. Open Questions

---

# 20. Implementation Phases

请将实现拆分为几个可单独验证的小阶段。

建议方向：

```text
Phase 1
Current authentication analysis + data model

Phase 2
Alias credential backend

Phase 3
Alias authentication

Phase 4
Settings APIs

Phase 5
Frontend settings UI

Phase 6
Disable system login

Phase 7
Security hardening

Phase 8
Migration + tests
```

但请根据项目实际依赖重新调整。

每个 Phase 应说明：

- 修改哪些现有模块
- 可能新增哪些组件
- 不应该修改哪些组件
- 验证方式
- 完成标准

---

# 21. Acceptance Criteria

至少包括以下验收场景：

### Existing user

```text
SystemUser + SystemPassword
→ login success
```

与当前行为一致。

### Create alias

```text
System login
→ Settings
→ Create alias
→ success
```

### Alias login

```text
Alias + AliasPassword
→ login success
→ same Canonical User
→ same Workspace
```

### Disable system login

```text
Alias configured
SystemLoginEnabled = false
```

之后：

```text
SystemUser + SystemPassword
→ RelaxKonOS login denied
```

但是：

```text
Alias + AliasPassword
→ success
```

并且系统账号本身仍然正常存在。

### Wrong password

```text
Alias + wrong password
→ denied
```

### Alias conflict

不能创建与：

```text
existing alias
```

或者根据设计：

```text
existing system username
```

冲突的 Alias。

### Remove alias

如果 System Login 已关闭：

```text
Delete Alias
```

必须先恢复其他有效登录方式，避免账号锁死。

### Authorization

无论使用：

```text
System Login
```

还是：

```text
Alias Login
```

得到的：

```text
UserId
Workspace
Permissions
```

必须完全一致。

---

# 22. Coding Constraints

这次任务只编写 Goal / Architecture 文档。

不要立即实现代码。

但是必须阅读真实代码并在文档中引用当前项目实际：

- class
- interface
- service
- controller
- endpoint
- frontend component
- storage implementation

不要根据假设设计一套与当前项目无关的新认证系统。

如果发现现有架构已经具有适合 Alias Login 的扩展点，应优先复用。

如果现有实现存在会阻碍本功能的架构问题，请在 Goal 中明确指出，但避免不必要的大规模重构。

最终目标是：

> 在保持 RelaxKonOS 现有“系统用户是真实身份主体”的基础上，引入 RelaxKonOS 自己管理的登录别名凭据，并允许用户在确认 Alias 可用后关闭系统账号直接登录 RelaxKonOS，同时不影响底层操作系统账号及其权限。