# RelaxKonOS Browser 模块设计

> 内置网页浏览器：基于 `Avalonia.Controls.WebView` 的 `NativeWebView`（平台原生引擎）在 Client 本地渲染网页；服务端只持久化按用户隔离的书签、历史和浏览器偏好。

## 定位与边界

RemoteBrowser 只负责网页导航、展示以及书签、历史和主页等浏览器功能。

- 网页请求由运行 Client 的设备直接发出；Server 不代理网页流量。
- 浏览器不创建、更新或停止 SSH 隧道，也不会在导航到 `localhost` 或 `127.0.0.1` 时自动转发。此类地址按用户输入直接加载。
- 本机 SSH 隧道由独立的 [Port Forwarding](./RelaxKonOS.PortForwarding.md) 应用显式管理；其设置和活动隧道不参与同步。
- Server 仅保存书签、历史和 `BrowserSettings`，所有浏览器 API 都以 JWT `sub` 隔离用户数据。

## 客户端

`BrowserApp` 创建 `BrowserViewModel` 与 `BrowserMainView`。`NativeWebView` 负责平台原生渲染；View 通过委托调用其后退、前进、刷新和停止方法，ViewModel 不持有 WebView 引用。

导航流程为：地址栏输入经 `NormalizeAddress` 归一化（域名补 `https://`、`localhost:port` 补 `http://`、搜索词转为搜索 URL）后，更新当前标签状态并通过 `ViewNavigateRequested` 请求 View 导航。导航完成后，浏览器异步记录历史并更新书签状态。

支持的功能：

- Chrome 风格的水平标签栏、单行导航栏、圆角自适应地址框、书签星标和三点菜单；
- 独立标签的导航状态、创建/切换/关闭；`Ctrl+T` 新建，`Ctrl+W` 关闭当前标签，`Ctrl+L` 聚焦地址栏；关闭最后一个标签后保留空白新标签页；
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

`BrowserSettingsDto` 包含 `HomePage` 和 `LinkOpenTarget`，通过 `WorkspaceConfigurationRegistry.BrowserPath` 保存到配置注册表。

集合读取采用 `offset` + `limit` 的单页数组：默认 100、最大 500，非正 limit 归一为 1，负 offset 归一为 0。书签可用 `url` 参数精确查询，当前页星标因此不依赖已加载的书签列表。右侧栏默认关闭，打开后按需追加页面；本地新增、删除、历史记录写入后重新加载首屏，异步过期页不会覆盖新状态。完整契约见 [Protocol](../architecture/RelaxKonOS.Protocol.md)。

## 界面与平台行为

布局参考 [Chrome 标签管理](https://support.google.com/chrome/answer/2391819) 与 [工具栏](https://support.google.com/chrome/answer/14835450)：标签位于顶部，新建按钮跟随标签；后退/前进、刷新/停止、主页、地址框和菜单位于同一行。所有颜色继续使用系统主题 token，中/英/日文资源齐全。

新标签页提供地址栏聚焦入口，不自动联网。地址栏没有固定 700px 最小宽度；侧栏宽度最多占内容区 45%。HTTPS 标记仅反映 URL 协议，不能代替证书验证；不再向所有 URL 显示固定锁形图标。

Windows/macOS 的每个有网页内容的标签按需创建独立 `NativeWebView`，切换仅改变可见性并保留导航状态；关闭标签移除其原生 surface。窗口关闭、失活、拖动、菜单打开和设置对话框显示时按生命周期隐藏/释放 surface。Linux 保留系统浏览器委托模式，切换标签不会再次启动宿主浏览器；未提供 Linux 内嵌网页或浏览器进程管理。

验证入口：`RelaxKonOS.ApplicationLayout.Tests --browser-only` 覆盖标签状态、分页、三种语言、两种主题和 1100/640/480px 三种宽度，并生成真实 Avalonia 布局截图。原生适配器的网页导航和平台资源释放仍需 Windows/macOS 实机验收。

当前实施状态见 [优化进度台账](../development/RelaxKonOS.Optimization.Progress.md)。

## 维护规则

1. 保持网页渲染和网络访问在 Client；不得将浏览器变为 Server HTTP 代理。
2. 浏览器不得依赖 `IPortForwardingService`，也不得因导航自动创建隧道。
3. `IBrowserClient` 只处理浏览器数据和偏好 API；端口转发由独立应用处理。
4. 书签和历史操作必须按当前 JWT 用户隔离。


### 2026-10-07：图标对齐与收藏/历史面板

所有浏览器图标按钮显式设置水平和垂直内容居中；更多、书签/历史、关闭与分类下拉采用矢量图形，摆脱文字图标的字体基线差异。回归检查直接比较图形与按钮的实际中心位置。

面板参考 [Chrome 侧面板](https://support.google.com/chrome/answer/13156494) 的分类切换方式，移到内容区右侧。顶部显示当前分类、下拉切换及关闭入口；书签使用站点图标、标题和域名双行布局，完整 URL 保留在提示和复制操作中。历史记录按本地日期显示今天/昨天/日期标题，行内显示访问时间、标题与域名。

条目支持单击打开、在新标签页打开、复制完整链接、删除；清空整个列表收纳到列表管理菜单。继续使用原来的有界分页和加载更多，数量明确表示已加载条目。历史标题随增删替换维护日期边界，保持来源 DTO 与持久化模型不变；没有新增文件夹或搜索功能契约。所有弹出菜单显示时统一隐藏原生网页 surface，关闭后恢复当前标签可见性。

验证包括图标实际中心、菜单条目 DataContext、新标签/复制/删除动作、日期分组边界、3 语言 × 2 主题 × 3 宽度以及原有地址解析回归。面板图见 [亮色书签](../development/optimization-evidence/Browser-Bookmarks-Light-1100.png)、[亮色历史](../development/optimization-evidence/Browser-History-Light-1100.png) 和 [暗色窄窗口历史](../development/optimization-evidence/Browser-History-Dark-640.png)。截图使用 fixture 数据，不包含真实用户书签或历史。
