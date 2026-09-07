# RemoteOS Browser 模块设计

> 内置网页浏览器：基于 `Avalonia.Controls.WebView` 的 `NativeWebView`（平台原生引擎）在 Client 本地渲染网页；服务端只持久化按用户隔离的书签、历史和浏览器偏好。

## 定位与边界

RemoteBrowser 只负责网页导航、展示以及书签、历史和主页等浏览器功能。

- 网页请求由运行 Client 的设备直接发出；Server 不代理网页流量。
- 浏览器不创建、更新或停止 SSH 隧道，也不会在导航到 `localhost` 或 `127.0.0.1` 时自动转发。此类地址按用户输入直接加载。
- 本机 SSH 隧道由独立的 [Port Forwarding](./RemoteOS.PortForwarding.md) 应用显式管理；其设置和活动隧道不参与同步。
- Server 仅保存书签、历史和 `BrowserSettings`，所有浏览器 API 都以 JWT `sub` 隔离用户数据。

## 客户端

`BrowserApp` 创建 `BrowserViewModel` 与 `BrowserMainView`。`NativeWebView` 负责平台原生渲染；View 通过委托调用其后退、前进、刷新和停止方法，ViewModel 不持有 WebView 引用。

导航流程为：地址栏输入经 `NormalizeAddress` 归一化（域名补 `https://`、`localhost:port` 补 `http://`、搜索词转为搜索 URL）后，直接设置 `WebViewSource`。导航完成后，浏览器异步记录历史并更新书签状态。

支持的功能：

- 导航、后退、前进、刷新、停止、主页和地址栏搜索；
- 书签新增、删除、清空与侧边栏导航；
- 历史记录、删除、清空与侧边栏导航；
- Workspace 同步的主页和链接打开位置偏好。

## 服务端与协议

`BrowserEndpoints` 提供以下受 JWT 保护的端点：

| Method | Route | 用途 |
| --- | --- | --- |
| GET / PUT | `/api/v1.0/browser/settings` | 读取或保存 `BrowserSettings` |
| GET / POST / DELETE | `/api/v1.0/browser/bookmarks` | 管理当前用户书签 |
| DELETE | `/api/v1.0/browser/bookmarks/{id}` | 删除单个书签 |
| GET / POST / DELETE | `/api/v1.0/browser/history` | 管理当前用户历史 |
| DELETE | `/api/v1.0/browser/history/{id}` | 删除单条历史 |

`BrowserSettingsDto` 包含 `HomePage` 和 `LinkOpenTarget`，作为 Workspace 的 `browser_settings` JSON 列持久化。旧数据中已废弃的转发字段会由 JSON 反序列化忽略，不会影响升级。

## 维护规则

1. 保持网页渲染和网络访问在 Client；不得将浏览器变为 Server HTTP 代理。
2. 浏览器不得依赖 `IPortForwardingService`，也不得因导航自动创建隧道。
3. `IBrowserClient` 只处理浏览器数据和偏好 API；端口转发由独立应用处理。
4. 书签和历史操作必须按当前 JWT 用户隔离。
