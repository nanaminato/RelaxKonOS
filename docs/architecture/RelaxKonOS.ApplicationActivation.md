# RelaxKonOS Application Activation

> 本文定义同一 Client 设备内的应用启动、深链和窗口实例复用。它不是 Client↔Server
> 协议；远端文件、容器等资源的真源仍在 Server，资源变更通知另行通过受授权的 Hub 设计。

## 1. 三种本地协作方式

| 方式 | 用途 | 返回值 | 范围 |
| --- | --- | --- | --- |
| activation | 打开或导航 UI | `AppActivationResult` | 当前 Client |
| host action（后续） | 受控的本地副作用，如建立 loopback 转发 | 强类型结果 | 当前 Client |
| resource event（后续） | 文件等远端资源发生变更的提示 | 无；客户端重新读取真源 | Workspace |

应用不得直接引用另一个应用的实现、ViewModel 或本地服务。activation 的入口为
`IAppActivation`（应用上下文）和 Shell 所有的 `IAppActivationService`。

## 2. URI 约定

`remoteos://` 是 Shell 保留 URI；scheme、host 和路径由 Shell 验证，不能动态反射调用应用。
当前已注册：

```text
remoteos://settings/personalization
remoteos://settings/apps
remoteos://settings/apps/{appId}/permissions
remoteos://file/open?appId={appId}&path={encodedPath}
```

最后一条仅允许 `remoteos.explorer` 作为来源，以兼容现有内置文件打开模型；外置包不能用它
传递宿主路径，而应使用其受限的文件 capability。调用方应使用
`RemoteOsActivationUris`，不能手工拼接字符串。

未匹配、歧义或不合法的 URI 分别得到 `RouteNotFound` 或 `InvalidUri`；应用不应猜测目标或
降级为直接调用另一个应用。

### 2.1 第三方 URI scheme

第三方包可在 `manifest.json` 用 `supportedUriSchemes` 显式声明非保留 scheme，并实现
`IExternalAppActivationHandler`。例如 Help Center 声明 `help`，接受：

```text
help://guide/docker/install?lang=en
```

Shell 先验证 URI（绝对 URI、无 user-info、无端口、scheme 长度不超过 32），只在已声明同一
scheme 且 `CanHandleActivation` 返回真时才投递。若设置了该 scheme 的默认程序，则它必须也在
候选集中并直接启动；否则只有唯一候选程序时才会启动。多个候选程序时，Shell 显示“选择应用”
对话框，用户可以仅本次打开，或保存为此 scheme 的默认程序再打开。没有候选程序时，Shell 显示
明确的未处理程序提示；对于 `help://`，提示安装 Help Center 并将其设为 help 链接处理程序。
安装入口及安装后重试属于后续受信任应用目录流程。`remoteos` 为保留 scheme，外部包不可声明。

第三方 handler 仅接收 host 已验证后的 URI 与受限 `IExternalAppContext`，不能获得另一个应用或
宿主的实现对象。应用自身必须继续验证它所拥有的 authority、路径与 query 参数。

## 3. 同 URI 与窗口策略

`ApplicationManifest.InstancePolicy` 定义逻辑主窗口的复用行为：

| 策略 | 同一 URI/重复启动行为 |
| --- | --- |
| `MultiWindow` | 每次 activation 都运行一次应用激活流程，通常新建窗口。Notepad 和 Code Editor 明确使用此策略。 |
| `SingleWindow` | Runtime 查找非模态主窗口，将 URI 投递给 `IAppActivationHandler`，恢复并聚焦该窗口；不会再调用 `Activate` 或创建第二个主窗口。 |
| `SingleWindowPerActivationKey` | 预留给按 Shell 规范化 key（如工作区根）复用的应用；当前未注册 key，因此不应在新应用中使用。 |

模态对话框不计为应用实例。应用路由处理器负责把重复 activation 变成页面切换、对象选中或
内部标签页导航；Runtime 只负责阻止额外主窗口并聚焦已有窗口。

文件打开也遵循该规则：当前所有文件处理程序都是多窗口；未来的单窗口文件程序在已有窗口时
会先被 Runtime 聚焦，随后应实现自己的 activation 路由，把文件放入已有的标签或文档模型。

Settings 是首个 `SingleWindow` 应用：再次打开个性化页会切换现有窗口的页面；打开
`apps/{appId}/permissions` 会选中应用并打开其权限编辑器。Task Manager、Port Forwarding、
Firewall、Process Guardian 与 Docker 也已声明单窗口；Docker 尚未接入预览 action。

## 4. 扩展规则

新增 `remoteos://` 公开路线时，内置应用实现 `IAppActivationHandler`；第三方 scheme 则声明
`supportedUriSchemes` 并实现 `IExternalAppActivationHandler`。两者都应在应用设计文档中定义：路径、参数、
权限、同 URI 行为、单/多窗口策略和本地化错误 UX。Shell 要拒绝两个应用同时声明同一路线。

将来的 Docker 预览不能仅靠 URI：镜像本身没有运行服务或已发布端口。它应先以受确认的
host action 创建仅 Server-loopback 的端口映射，再请求本机 Port Forwarding action 得到实际
`localhost` URL，最后通过 Browser activation 打开该 URL；不得默认发布到 `0.0.0.0`。
