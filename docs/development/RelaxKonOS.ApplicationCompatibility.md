# RelaxKonOS 应用兼容性

> 本文定义内置应用与 `.roapp` 外置应用在客户端、服务端之间的兼容性契约。
> 目标是在加载外置程序集或调用应用代码之前，由 RelaxKonOS Shell 给出明确、可本地化的提示。

## 1. 为什么分为两类要求

一个应用是否能运行不能只用一个“操作系统”字段描述：

- **客户端平台**决定 UI 和应用私有原生依赖能否运行。例如 Video Player 随包携带
  `VideoLAN.LibVLC.Windows`，只能在 Windows 客户端加载。
- **服务端平台和能力**决定连接的 RelaxKonOS Server 是否能提供应用所需的宿主功能。例如
  POSIX 权限编辑只适用于 Linux Server；同为 Linux 的两台 Server 也可能提供不同功能。

因此平台用于粗粒度筛选，能力用于稳定、可演进的功能契约。权限（`requestedPermissions`）
仍是用户授予外置应用的访问授权，不能替代服务端能力声明。

## 2. 外置包 manifest

```json
{
  "clientPlatforms": ["windows"],
  "serverRequirements": {
    "platforms": ["windows", "linux"],
    "capabilities": ["server.files"]
  }
}
```

字段均为可选：缺省或空数组表示不限制。允许的平台标识目前为小写的 `windows`、`linux`。
能力标识为小写稳定字符串；未知能力不在安装时被拒绝，而是在启动时以“不具备此能力”拦截，
以便旧客户端可以安装面向较新服务端的应用包。

当前 Server 能力如下：

| 能力 | 含义 |
| --- | --- |
| `server.files` | 远程文件 API |
| `server.metrics` | 主机性能指标 API |
| `server.processes` | 主机进程 API |
| `server.terminal` | 远程 PTY 终端 |
| `server.posix.permissions` | POSIX 文件权限；仅 Linux Server 声明 |

Video Player 声明 `"clientPlatforms": ["windows"]`；Server Monitor 声明
`"capabilities": ["server.metrics"]`。内置应用通过 `ApplicationManifest` 使用完全相同的模型，
但当前未声明限制的内置应用保持 Windows + Linux 均可用的默认语义。

## 3. 服务端描述与数据来源

登录成功的 `LoginResponse.server` 返回 `ServerDescriptorDto`：实际运行 RelaxKonOS.Server 的
平台，以及该部署的能力集合。该值由服务端运行时探测，不能由客户端传入或由应用包伪造。

`UserDto.Platform` 继续表示登录请求中的 `clientPlatform`，也就是用户/客户端设备的平台；
它不是 Server 平台，不能用于兼容性判断或标记为“Host platform”。

## 4. 运行时流程

```text
桌面启动 / 文件关联打开
        ↓
ApplicationManager 统一兼容性检查
        ↓
客户端平台 → 服务端平台 → 服务端能力
        ↓
兼容：惰性加载外置程序集并激活
不兼容：Shell 创建 host-owned 桌面窗口说明原因
```

外置包在客户端启动时只读取并注册 manifest 元数据；不会加载入口 DLL。这样把 Windows 专属的
原生依赖包安装在 Linux 客户端时，不会在登录或桌面初始化阶段崩溃。首次通过检查后才会创建
可回收的程序集加载上下文。更新或卸载仍会关闭该应用窗口并卸载上下文。

兼容性检查返回 `Compatible`、`ClientPlatformMismatch`、`ServerPlatformMismatch`、
`MissingServerCapability` 或 `ServerUnavailable`。提示窗口由 Shell 而不是外置应用创建，
保证它可在应用尚未加载时显示，并在 Windows/Linux 上保持一致的桌面窗口行为。

## 5. 维护规则

- 新增服务端功能时，先在 `ServerCapabilities` 增加稳定名称，再由 ServerDescriptor 声明它；
  不得把实现细节或包版本作为能力名。
- 仅在 OS 本身是不可绕过前提时声明 `platforms`；可探测的功能应优先使用 capability。
- 应用仍必须在调用服务端 API 时正确处理失败。兼容性判断是启动前保护与可理解的 UX，不是
  安全边界，也不代替授权检查。
- 为内置应用新增平台专属能力时，在其设计文档中补充 manifest 要求和 Windows/Linux 验收矩阵。
