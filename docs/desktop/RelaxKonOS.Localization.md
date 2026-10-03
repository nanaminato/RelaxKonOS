# RelaxKonOS 本地化流程

RelaxKonOS 使用 BCP-47 语言名称（`en-US`、`zh-CN`、`ja-JP`），并以英文源字符串/键作为回退基线。

## 客户端文本

`LocalizationService` 负责当前语言，从 `Client/RelaxKonOS.Client/Localization` 加载 JSON 语言包，并触发 `LanguageChanged`。规定的迁移方式是稳定键加上通过 `LocalizationService.Get(key, englishFallback)` 提供的英文回退值；AXAML 绑定本地化视图模型属性，代码创建的控件也使用同一方法。系统不会扫描可视树或按源句子查找，因此语言变更只能由各显示值的所有者处理。

登录视图在认证前使用本机语言。`LocalLanguageStore` 仅将该 BCP-47 名称写入本地应用数据。认证后，`PreferencesSync` 会加载当前用户工作区的 `WorkspacePreferencesDto.Language`；设置页将后续变更写入该工作区偏好。除三个显式语言外，设置页提供可同步的 `follow-system`：系统中文映射为 `zh-CN`，系统日文映射为 `ja-JP`，其余系统语言映射为 `en-US`。SSH 桌面始终使用同一套设备本地映射，不读取或写入工作区偏好。退出登录后恢复本地登录语言。

## API 文本

每个类型化 HTTP 客户端都由 `AcceptLanguageHandler` 包装，后者将当前语言写入 `Accept-Language`。服务端会在 `Content-Language` 中回显选定的请求语言。诸如 RFC 7807 `ProblemDetails` 标题等 API 自有展示元数据由 `ApiLocalizer` 本地化；用户名、文件路径、书签名和原始主机错误文本等用户/领域值不会翻译或修改。

第三方包会收到 `IExternalAppContext.SystemLanguage`，应本地化自己的资源，并在 `LanguageChanged` 时刷新。


## 桌面语言切换追踪

客户端自动记录 `%LOCALAPPDATA%\RelaxKonOS\logs\language-switch-<启动时间>-<进程号>.log`，每行一个 JSON 事件。日志包含构建标识、进程、线程、递增序号、语言选择及下拉选项重建、本地化通知、偏好应用、同步读操作编号、HTTP 请求编号及服务端关联编号、保存语言和版本号。不记录令牌、服务器地址、完整偏好或异常消息。单文件上限约 2 MB，轮转段为 `.previous`；保留当前及最近五次运行。

复现步骤：启动新构建的客户端，打开设置的“时刻与语言”，依次切换 English、日本語、简体中文；再快速连续切换，并在保存中切换。记录最终选择、界面实际语言和问题发生时间，收集本次运行的日志及 `.previous`。`selection.completed` 与 `localization.notify.end` 用于确认本地即时状态，`sync.read.apply` 与 `preferences.apply` 用于识别旧读结果覆盖，`sync.read.skipped_draft` 表示草稿保护生效，`save.acknowledged` 表示服务器接受的语言及版本。

Headless 验证使用真实 `TimeLanguagePageView` 与双向绑定下拉框，检查连续切换后选中值、本地生效语言及已打开页面的翻译文本；日志写入测试输出的 `preview-qa/logs/`。此验证不代替连接实际服务器时的同步竞争复现。

2026-10-03 的 Headless 复现确认：更换语言选项集合时，ComboBox 双向绑定会写回旧选项，使刚选中的语言立即回退；无需服务器即可触发。设置页将选项重建排到当前选择写入完成后，在刷新选项期间忽略控件回写，并重新通知当前选中项，避免本地切换被刷新过程撤销。
