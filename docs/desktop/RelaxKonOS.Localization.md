# RelaxKonOS 本地化流程

RelaxKonOS 使用 BCP-47 语言名称（`en-US`、`zh-CN`、`ja-JP`），并以英文源字符串/键作为回退基线。

## 客户端文本

`LocalizationService` 负责当前语言，从 `Client/RelaxKonOS.Client/Localization` 加载 JSON 语言包，并触发 `LanguageChanged`。规定的迁移方式是稳定键加上通过 `LocalizationService.Get(key, englishFallback)` 提供的英文回退值；AXAML 绑定本地化视图模型属性，代码创建的控件也使用同一方法。系统不会扫描可视树或按源句子查找，因此语言变更只能由各显示值的所有者处理。

登录视图在认证前使用本机语言。`LocalLanguageStore` 仅将该 BCP-47 名称写入本地应用数据。认证后，`PreferencesSync` 会加载当前用户工作区的 `WorkspacePreferencesDto.Language`；设置页将后续变更写入该工作区偏好。退出登录后恢复本地登录语言。

## API 文本

每个类型化 HTTP 客户端都由 `AcceptLanguageHandler` 包装，后者将当前语言写入 `Accept-Language`。服务端会在 `Content-Language` 中回显选定的请求语言。诸如 RFC 7807 `ProblemDetails` 标题等 API 自有展示元数据由 `ApiLocalizer` 本地化；用户名、文件路径、书签名和原始主机错误文本等用户/领域值不会翻译或修改。

第三方包会收到 `IExternalAppContext.SystemLanguage`，应本地化自己的资源，并在 `LanguageChanged` 时刷新。
