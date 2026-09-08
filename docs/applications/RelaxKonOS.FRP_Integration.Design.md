# RelaxKonOS FRP 内网穿透集成设计

## 1. 目标

RelaxKonOS 需要集成基于 FRP 的内网穿透能力，同时满足以下目标：

- 降低普通用户部署 `frpc` / `frps` 的门槛。
- 不把 FRP 协议实现直接耦合进 RelaxKonOS 主进程。
- 支持 Windows、Windows Server 与 Linux。
- 支持 RelaxKonOS 自动管理 FRP，也支持用户自带 FRP。
- 支持连接 RelaxKonOS 管理的 `frps`、用户自建 `frps` 和第三方 FRP 服务。
- 保持未来扩展 Cloudflare Tunnel、Tailscale 或其他隧道方案的可能性。
- 正确处理 Windows Defender 对 `frpc.exe` / `frps.exe` 的误报、隔离与删除问题。
- 不通过关闭杀毒软件、加壳、隐藏文件等方式规避安全软件。

---

## 2. 总体架构原则

推荐采用：

> **RelaxKonOS 原生管理 FRP，但 `frpc` / `frps` 始终作为独立进程运行。**

不推荐：

- 把 FRP 的 Go 源码直接嵌入 RelaxKonOS。
- 自己实现 FRP 协议。
- 把 `frpc` / `frps` 作为 RelaxKonOS 主进程的一部分。
- 强制用户只能连接 RelaxKonOS 自己部署的 `frps`。

推荐结构：

```text
RelaxKonOS
   │
   ├── Tunnel Manager
   │
   ├── Runtime Manager
   │
   └── FRP Provider
           │
           ├── frpc
           └── frps

frpc / frps 均作为独立子进程或系统服务运行
```

RelaxKonOS 负责：

- 安装
- 下载
- 版本管理
- 配置生成
- 启动 / 停止
- 重启
- 状态监控
- 日志收集
- 升级
- 回滚
- 隧道管理
- 安全校验
- Windows Defender 兼容处理

实际网络转发由 FRP 自身完成：

```text
frpc <──────────────> frps
```

RelaxKonOS 不参与隧道数据转发。

---

## 3. FRP 的运行模式

建议至少支持两种模式。

### 3.1 Managed FRP

由 RelaxKonOS 自动管理 FRP Runtime。

流程：

```text
检测 OS / CPU 架构
        ↓
下载官方 FRP Runtime
        ↓
验证 SHA-256
        ↓
安装到版本目录
        ↓
生成配置
        ↓
启动 frpc / frps
        ↓
监控运行状态
```

适合普通用户。

RelaxKonOS 可以负责：

- 自动下载官方版本
- 选择 Windows / Linux
- 选择 amd64 / arm64
- 检查更新
- 升级
- 回滚
- 管理配置
- 管理日志

### 3.2 External FRP

允许高级用户使用系统中已经存在的 FRP。

例如：

```text
Linux:
/usr/local/bin/frpc

Windows:
D:\Tools\frp\frpc.exe
```

RelaxKonOS 可以提供不同管理级别：

```text
External FRP

○ 仅使用现有 FRP
○ RelaxKonOS 管理配置
○ RelaxKonOS 管理启动 / 停止
○ 仅监控状态
```

这样不会强制用户改变现有部署。

---

## 4. frpc 与 frps 的职责划分

### 4.1 frpc：RelaxKonOS 内网穿透的主要功能

典型场景：

```text
内网服务器
    │
RelaxKonOS
    │
  frpc
    │
Internet
    │
  frps
    │
公网 VPS
```

RelaxKonOS 的“内网穿透”页面应主要围绕 `frpc` 展开。

建议功能：

- FRP 服务器配置
- 连接状态
- 隧道列表
- TCP / UDP / HTTP / HTTPS
- STCP / XTCP 等高级模式
- 日志
- 连接统计
- 启停
- 重连
- 配置校验

---

### 4.2 frps：可选的 FRP 服务端能力

如果 RelaxKonOS 安装在有公网 IP 的服务器上，可以允许用户启用 FRP Server。

示例：

```text
RelaxKonOS
  ↓
FRP Server
  ↓
frps
```

建议支持：

- bind 地址
- bind 端口
- Token
- OIDC
- TLS
- Dashboard
- 允许远程端口范围
- 最大连接数
- 日志
- 启停
- 版本升级

`frps` 应作为可选功能，而不是 RelaxKonOS 的必需组件。

---

## 5. 必须支持第三方 frps

不要把 RelaxKonOS 设计成：

```text
RelaxKonOS frpc
      ↓
只能连接
      ↓
RelaxKonOS frps
```

正确方式：

```text
RelaxKonOS frpc
      │
      ├── RelaxKonOS 管理的 frps
      ├── 用户自己的 frps
      ├── 第三方 FRP 服务
      └── 其他兼容 FRP Server
```

RelaxKonOS 只需要用户配置：

- Host
- Port
- Auth
- TLS
- Transport
- 其他高级参数

建议将服务器定义抽象为：

```csharp
public sealed class FrpServerProfile
{
    public Guid Id { get; init; }

    public string Name { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; } = 7000;

    public FrpAuthConfig Auth { get; set; } = new();

    public FrpTlsConfig Tls { get; set; } = new();
}
```

不要简单设计成：

```csharp
public string Token { get; set; }
```

因为未来可能需要：

- Token
- OIDC
- TLS Client Certificate
- 其他认证方式

---

## 6. 不要让业务层直接依赖 FRP

建议抽象统一的隧道 Provider。

例如：

```csharp
public interface ITunnelProvider
{
    string ProviderId { get; }

    Task<TunnelProviderStatus> GetStatusAsync();

    Task StartAsync();

    Task StopAsync();

    Task<IReadOnlyList<TunnelInfo>> GetTunnelsAsync();

    Task CreateTunnelAsync(TunnelDefinition tunnel);

    Task UpdateTunnelAsync(TunnelDefinition tunnel);

    Task DeleteTunnelAsync(Guid tunnelId);
}
```

实现：

```text
ITunnelProvider
    │
    ├── FrpTunnelProvider
    ├── CloudflareTunnelProvider
    ├── TailscaleTunnelProvider
    └── RemoteOsTunnelProvider
```

这样 UI 只面向统一的：

```text
Tunnel
Server
Status
Provider
```

而不是强绑定 FRP。

---

## 7. Runtime Manager

建议 RelaxKonOS 建立统一的 Runtime Manager。

负责管理：

```text
FRP
Nginx
Caddy
Git
其他第三方 Runtime
```

FRP 只是 Runtime Manager 中的一种 Runtime。

### Linux

```text
/opt/remoteos/
├── RelaxKonOS
├── runtimes/
│   └── frp/
│       ├── 0.69.0/
│       │   ├── frpc
│       │   └── frps
│       └── 0.70.0/
│           ├── frpc
│           └── frps
└── data/
    └── frp/
        ├── configs/
        └── logs/
```

### Windows

```text
C:\ProgramData\RemoteOS\
├── runtimes\
│   └── frp\
│       ├── 0.69.0\
│       │   ├── frpc.exe
│       │   └── frps.exe
│       └── 0.70.0\
│           ├── frpc.exe
│           └── frps.exe
└── data\
    └── frp\
        ├── configs\
        └── logs\
```

不要直接覆盖旧版本。

---

## 8. 版本升级与回滚

推荐使用版本目录。

例如：

```text
runtimes/frp/
├── 0.69.0/
└── 0.70.0/
```

升级流程：

```text
当前版本 0.69
      ↓
下载 0.70
      ↓
SHA-256 校验
      ↓
生成新配置
      ↓
配置验证
      ↓
测试启动
      ↓
切换 Active Runtime
      ↓
确认运行正常
      ↓
保留或清理旧版本
```

如果失败：

```text
0.70 启动失败
      ↓
切回 0.69
      ↓
恢复旧配置
```

RelaxKonOS 应保存：

- Current Version
- Previous Version
- Install Time
- SHA-256
- Source
- Runtime Path
- Status

---

## 9. 配置管理：数据库作为 Desired State

不要把 `frpc.toml` 当作业务数据源。

推荐：

```text
RelaxKonOS Database
        │
        │ Desired State
        ↓
FrpConfigGenerator
        ↓
frpc.toml / frps.toml
```

例如 Tunnel 数据模型：

```text
Tunnel
├── Id
├── Name
├── Provider
├── Protocol
├── LocalHost
├── LocalPort
├── RemotePort
├── Domain
├── Enabled
├── Encryption
├── Compression
└── ServerProfileId
```

配置修改流程：

```text
UI
 ↓
API
 ↓
Database
 ↓
Validate
 ↓
Generate TOML
 ↓
frpc verify
 ↓
atomic replace
 ↓
reload / restart
```

这样未来 FRP 配置格式变化时，只需要调整：

```text
FrpConfigGenerator
```

UI 和数据库无需大规模修改。

---

## 10. RelaxKonOS 自身通过 FRP 暴露

FRP 可以作为 RelaxKonOS 自己的一种远程连接方式。

例如：

```text
RelaxKonOS Backend
127.0.0.1:8080
      │
    frpc
      │
Internet
      │
    frps
      │
remoteos.example.com
```

客户端连接方式可以设计成：

```text
连接方式

● IP / Host
○ FRP
○ Cloudflare Tunnel
○ 其他
```

但必须遵循：

> **RelaxKonOS Backend 不依赖 FRP 才能启动。**

正确结构：

```text
RelaxKonOS Service
    │
    ├── HTTP API
    │
    ├── WebSocket
    │
    └── FrpRuntimeSupervisor
             │
             └── frpc
```

即使 `frpc` 崩溃：

- 公网 FRP 连接失效
- 局域网 RelaxKonOS 仍可使用
- 用户仍能登录修复

不能出现：

```text
frpc 崩溃
   ↓
RelaxKonOS 一起不可用
```

---

# 11. Windows Defender / Windows Server 问题

## 11.1 RelaxKonOS 下载无法天然避免查杀

如果 Windows Defender 将：

```text
frpc.exe
frps.exe
```

识别为：

- HackTool
- Riskware
- PUA
- 网络穿透工具
- 可疑远控组件

那么即使由 RelaxKonOS 自动下载：

```text
RelaxKonOS
   ↓
HTTPS Download
   ↓
frpc.exe
   ↓
Microsoft Defender
   ↓
隔离 / 删除
```

依然可能发生。

RelaxKonOS 本身是管理员或 SYSTEM 身份，也不会自动让 `frpc.exe` 获得 Defender 信任。

---

## 11.2 推荐两种 Windows 安全模式

### 标准模式

默认模式。

```text
● 标准模式

RelaxKonOS 不修改 Windows Defender
```

行为：

- 下载 FRP
- 校验 SHA-256
- 尝试安装
- 如果 Defender 删除文件，则检测失败原因
- 给出明确提示

这是推荐默认值。

---

### 兼容模式

仅在用户明确授权后启用。

```text
○ Defender 兼容模式

允许 RelaxKonOS 为 FRP Runtime 配置
最小范围 Defender Exclusion
```

必须：

- 明确提示风险
- 不默认开启
- 不静默修改
- 可以撤销

---

## 12. Defender 排除范围

绝对不要排除整个：

```text
C:\ProgramData\RemoteOS
```

因为其中未来可能包含：

```text
plugins
apps
uploads
temp
runtimes
cache
```

如果整个目录被排除，恶意文件可能利用该目录逃避扫描。

推荐范围：

```text
C:\ProgramData\RemoteOS\runtimes\frp\
```

更严格时可以只排除：

```text
C:\ProgramData\RemoteOS\runtimes\frp\0.70.0\frpc.exe

C:\ProgramData\RemoteOS\runtimes\frp\0.70.0\frps.exe
```

推荐：

> **优先文件级，其次版本目录级，最后才考虑 FRP Runtime 根目录。**

不要扩大到 RelaxKonOS 根目录。

---

## 13. 不要混淆 Defender Process Exclusion

要区分：

- File Exclusion
- Folder Exclusion
- Process Exclusion

对于 FRP 被删除的问题，需要重点处理的是：

```text
File / Folder Exclusion
```

而不是单纯配置：

```text
Process Exclusion
```

因为 Process Exclusion 并不等价于：

> 不扫描这个 EXE 文件本身。

---

## 14. 推荐的 Windows FRP 安装流程

如果使用标准模式：

```text
下载
 ↓
SHA-256
 ↓
安装
 ↓
Defender 扫描
 ↓
成功 → 完成
 ↓
被删除 → 提示用户
```

如果用户启用兼容模式：

```text
① 获取官方版本信息
      ↓
② 获取下载地址与 SHA-256
      ↓
③ 显示 Defender 安全提示
      ↓
④ 用户明确授权
      ↓
⑤ 创建最小范围 Exclusion
      ↓
⑥ 下载官方 FRP
      ↓
⑦ 强制 SHA-256 校验
      ↓
⑧ 解压 / 安装
      ↓
⑨ 验证二进制
      ↓
⑩ 启动 FRP
```

如果 SHA-256 不一致：

```text
立即删除
禁止启动
标记安装失败
```

---

## 15. SHA-256 校验必须是强制项

一旦配置 Defender Exclusion，RelaxKonOS 自己就承担更多安全责任。

因此 Managed Runtime 必须保存：

```csharp
public sealed class RuntimePackage
{
    public string Version { get; init; } = "";

    public string Platform { get; init; } = "";

    public string Architecture { get; init; } = "";

    public string DownloadUrl { get; init; } = "";

    public string ExpectedSha256 { get; init; } = "";
}
```

安装后建议显示：

```text
FRP Runtime

Version
0.70.x

Source
✓ Official Release

Integrity
✓ SHA-256 Verified

Antivirus
⚠ Defender Exclusion Enabled
```

---

## 16. 企业 Windows / Windows Server 环境

部分环境可能存在：

- Group Policy
- Microsoft Defender for Endpoint
- Intune
- Tamper Protection
- Security Baseline
- Domain Policy

即使 RelaxKonOS 以管理员运行，也不一定能够修改 Defender。

因此不能简单：

```csharp
AddDefenderExclusion();
return Success;
```

应该检测实际结果。

例如：

```text
FRP Runtime

Microsoft Defender:
Enabled

Exclusion:
Failed

Reason:
Managed by organization policy

Possible causes:
- Group Policy
- Tamper Protection
- Microsoft Defender for Endpoint
```

然后告诉管理员：

```text
请由组织管理员添加以下排除项：

C:\ProgramData\RemoteOS\runtimes\frp\...
```

RelaxKonOS 不应该尝试绕过企业策略。

---

## 17. 不建议关闭 Windows Defender

绝对不应引导用户：

```text
关闭实时保护
关闭 Microsoft Defender
关闭 Tamper Protection
```

RelaxKonOS 的产品原则应是：

> **与安全软件协作，而不是绕过安全软件。**

推荐优先级：

```text
正常扫描
   ↓
检测误报
   ↓
用户确认
   ↓
最小范围 Exclusion
```

---

## 18. 不建议的规避方式

禁止采用：

### 18.1 加壳

```text
frpc.exe
 ↓
UPX / Pack
 ↓
RelaxKonOSFrpc.exe
```

这可能反而提高杀毒软件的风险评分。

### 18.2 修改二进制规避特征

不要尝试：

- 修改 PE Header
- 随机修改 Resource
- 修改字符串
- 注入 Stub
- 二次封装

### 18.3 加密落盘

不要：

```text
下载加密文件
 ↓
运行时解密
 ↓
内存执行
```

这种行为非常接近恶意软件的规避模式。

### 18.4 静默添加 Defender 白名单

不要：

```text
RelaxKonOS Installer
   ↓
自动 Add-MpPreference
   ↓
不提示用户
```

必须经过用户明确授权。

---

## 19. RelaxKonOS 代码签名

建议未来为：

```text
RelaxKonOS.exe
RemoteOS.Service.exe
RelaxKonOS.Installer.exe
```

使用 Authenticode Code Signing。

代码签名可以改善：

- SmartScreen 信誉
- Windows 安装体验
- 企业环境可信度
- 用户对 RelaxKonOS 本身的信任

但是：

```text
RelaxKonOS.exe   ← RelaxKonOS 签名
   │
   └── frpc.exe
```

不会导致：

```text
frpc.exe 自动继承 RelaxKonOS 签名
```

Windows Defender 仍会单独分析 `frpc.exe`。

所以：

> **RelaxKonOS 签名不能解决所有 FRP 误报。**

---

## 20. Runtime 安全状态模型

建议 Runtime Manager 统一维护安全状态。

例如：

```text
FRP

Version
0.70.x

Runtime
Running

Source
Official

Integrity
Verified

Antivirus
Defender Exclusion Enabled

Update
Latest
```

可以抽象：

```csharp
public sealed class RuntimeSecurityStatus
{
    public bool IntegrityVerified { get; init; }

    public AntivirusStatus Antivirus { get; init; }

    public bool IsExcluded { get; init; }

    public string? DetectionName { get; init; }
}
```

未来：

```text
Runtime Manager
├── FRP
├── Nginx
├── Caddy
├── Git
└── Other Runtime
```

全部可以复用。

---

# 21. 权限设计

FRP 管理必须接入 RelaxKonOS 权限系统。

建议权限：

```text
network.tunnel.read
network.tunnel.create
network.tunnel.update
network.tunnel.delete

network.frp.client.read
network.frp.client.manage

network.frp.server.read
network.frp.server.manage

network.frp.runtime.install
network.frp.runtime.update

network.frp.security.read
network.frp.security.manage
```

对于敏感认证信息：

```text
network.frp.secret.read
network.frp.secret.update
```

需要单独控制。

---

## 22. Secret 管理

以下内容不能作为普通配置直接明文返回：

- Token
- OIDC Client Secret
- STCP Secret Key
- TLS Private Key
- API Credential

应该进入 RelaxKonOS SecretStore。

列表和普通读取 API 返回：

```json
{
  "authType": "token",
  "tokenConfigured": true
}
```

普通读取 API 不返回：

```json
{
  "token": "actual-secret-token"
}
```

但 Controller 打开 Token 编辑器时，可调用受单独授权和审计保护的编辑读取 API 回显该 Profile 或托管 FRPS 的完整 Token；该值仅用于当前编辑会话，不得出现在列表、导出、日志、生成配置下载或其他普通读取 API 中。

---

# 23. UI 设计建议

主菜单：

```text
网络
├── 网络接口
├── 防火墙
└── 内网穿透
```

内网穿透：

```text
内网穿透
├── 概览
├── 隧道
├── FRP 服务器
├── FRP 服务端
├── 日志
└── 设置
```

---

## 24. 客户端页面

建议：

```text
FRP Client

状态
● 已连接

服务器
frp.example.com:7000

Runtime
FRP 0.70.x

隧道
4

[管理隧道]
[服务器设置]
[查看日志]
```

---

## 25. 服务端页面

```text
FRP Server

状态
● Running

Bind
0.0.0.0:7000

Auth
Token

Allowed Ports
10000-20000

Dashboard
Disabled

[设置]
[停止]
[日志]
```

---

## 26. Defender 提示 UI

第一次检测到 FRP 被 Defender 删除时：

```text
FRP 安装失败

Microsoft Defender 阻止了 FRP Runtime。

RelaxKonOS 下载的是官方 FRP Runtime，
但 Windows Defender 可能将部分 FRP 二进制
识别为风险网络工具。

[查看检测信息]

○ 保持 Defender 设置不变

○ 为 FRP Runtime 添加最小范围排除项

排除范围：
C:\ProgramData\RemoteOS\runtimes\frp\

RelaxKonOS 将继续强制验证下载文件 SHA-256。

[取消]
[继续]
```

必须：

- 默认不启用 Exclusion
- 清楚说明安全影响
- 支持撤销
- 显示实际排除范围

---

# 27. Runtime API 建议

可以抽象：

```csharp
public interface IRuntimeManager
{
    Task<RuntimeStatus> GetStatusAsync(
        string runtimeId);

    Task InstallAsync(
        RuntimeInstallRequest request);

    Task UpdateAsync(
        string runtimeId,
        string version);

    Task RollbackAsync(
        string runtimeId);

    Task StartAsync(
        string runtimeId);

    Task StopAsync(
        string runtimeId);
}
```

FRP Runtime：

```text
RuntimeId = "frp"
```

Tunnel Provider：

```text
ProviderId = "frp"
```

这样：

```text
Runtime
```

与：

```text
Tunnel Provider
```

职责分离。

---

# 28. 推荐模块划分

```text
RelaxKonOS
│
├── Network
│   │
│   ├── Firewall
│   │
│   ├── Interfaces
│   │
│   └── Tunnels
│       │
│       ├── TunnelService
│       ├── TunnelRepository
│       │
│       └── Providers
│           └── FRP
│
├── Runtimes
│   │
│   ├── RuntimeManager
│   ├── RuntimeDownloader
│   ├── RuntimeIntegrityVerifier
│   ├── RuntimeSupervisor
│   └── Providers
│       └── FrpRuntimeProvider
│
├── Security
│   │
│   ├── SecretStore
│   └── Antivirus
│       └── WindowsDefenderProvider
│
└── FRP
    │
    ├── FrpConfigGenerator
    ├── FrpConfigValidator
    ├── FrpClientManager
    └── FrpServerManager
```

---

# 29. 最终推荐架构

RelaxKonOS 对 FRP 的定位应该是：

> **深度集成，但不强绑定。**

最终关系：

```text
RelaxKonOS
   │
   ├── Tunnel Manager
   │       │
   │       └── FRP Provider
   │
   ├── Runtime Manager
   │       │
   │       └── FRP Runtime
   │              │
   │              ├── frpc
   │              └── frps
   │
   ├── Secret Store
   │
   └── Security Integration
           │
           └── Windows Defender
```

其中：

```text
RelaxKonOS 管理 FRP
```

但：

```text
RelaxKonOS != FRP
```

---

# 30. 最终结论

推荐方案可以总结为：

1. **RelaxKonOS 内置 FRP 管理能力。**
2. **`frpc` / `frps` 作为独立 Runtime 运行。**
3. **默认按需下载，而不是强制随 RelaxKonOS 安装。**
4. **支持 Managed FRP。**
5. **支持用户自带 External FRP。**
6. **支持用户自己的第三方 `frps`。**
7. **frpc 是主要客户端功能。**
8. **frps 是可选服务端功能。**
9. **通过 `ITunnelProvider` 解耦 FRP。**
10. **通过 Runtime Manager 管理二进制与版本。**
11. **数据库保存 Desired State，TOML 只作为生成结果。**
12. **所有 Managed Runtime 强制进行 SHA-256 完整性验证。**
13. **Windows Defender 默认不修改。**
14. **如果 FRP 被 Defender 拦截，可提供用户主动开启的兼容模式。**
15. **Defender Exclusion 必须最小化范围。**
16. **绝不能排除整个 RelaxKonOS 数据目录。**
17. **绝不能关闭 Defender 作为默认解决方案。**
18. **绝不能通过加壳、隐藏、加密落盘等方式规避杀毒软件。**
19. **企业 Windows 环境必须尊重 GPO、Intune 与 Tamper Protection。**
20. **RelaxKonOS 自己应该进行代码签名，但不要假设该签名可以解决 FRP 的全部误报。**
21. **FRP Secret 必须进入 SecretStore。**
22. **FRP 管理能力必须进入 RelaxKonOS 权限系统。**
23. **RelaxKonOS 主服务不能依赖 FRP 才能运行。**
24. **为未来 Cloudflare Tunnel、Tailscale 等 Provider 保留扩展空间。**

整体设计原则：

> **把 FRP 当作 RelaxKonOS 可管理的第三方网络 Runtime，而不是 RelaxKonOS 自身的一部分。**

这样可以同时获得：

- 一键安装体验
- 跨平台能力
- FRP 独立升级
- 故障隔离
- 用户自定义能力
- 第三方服务兼容性
- 更好的 Windows Defender 安全边界
- 更好的未来扩展性
