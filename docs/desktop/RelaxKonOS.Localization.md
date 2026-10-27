# RelaxKonOS 本地化流程

RelaxKonOS 使用 BCP-47 语言名称（`en-US`、`zh-CN`、`ja-JP`），并以英文源字符串/键作为回退基线。

## 客户端文本

`LocalizationService` 负责当前语言，从 `Client/RelaxKonOS.Client/Localization` 加载 JSON 语言包，并触发 `LanguageChanged`。规定的迁移方式是稳定键加上通过 `LocalizationService.Get(key, englishFallback)` 提供的英文回退值；AXAML 绑定本地化视图模型属性，代码创建的控件也使用同一方法。系统不会扫描可视树或按源句子查找，因此语言变更只能由各显示值的所有者处理。

登录视图在认证前使用本机语言。`LocalLanguageStore` 仅将该 BCP-47 名称写入本地应用数据。认证后，`PreferencesSync` 会加载当前用户工作区的 `WorkspacePreferencesDto.Language`；设置页将后续变更写入该工作区偏好。除三个显式语言外，设置页提供可同步的 `follow-system`：系统中文映射为 `zh-CN`，系统日文映射为 `ja-JP`，其余系统语言映射为 `en-US`。SSH 桌面始终使用同一套设备本地映射，不读取或写入工作区偏好。退出登录后恢复本地登录语言。

## 资源键校验

`Tools/verify-localization.py` 除要求三语言键集完全一致外，还要求**客户端源码引用的每个键都在 `en-US` 中存在**：它扫描
`Client/RelaxKonOS.Client/**` 下的 `.cs` 与 `.axaml`，识别 `{loc:Loc …}`、`LocalizedText.Get/Ref/RefWithFallback/Format`、
`LocalizedStatus.Key/Format`、`xxxLocalization.Get`，以及 `ComponentObservationText.Key` / `TunnelProblemText.Format`
这类只接收末段、由包装器补前缀的调用。用拼接构造的键（`"component_center.badge." + kind`、`"login.local_manage_" + key`）
无法静态求值，只校验前缀至少命中一个既有键。

新增、改名或删除资源键后运行：

```bash
python Tools/verify-localization.py
```

同一脚本还校验三语**格式参数**是否一一对应（`{0}` 出现在句末也计入，`{0:yyyy年M月d日}` 这类格式串同样识别），
译文漏掉占位符会直接失败，而不是在运行时丢掉一段数据。

`Tests/Client/RelaxKonOS.WindowPreviews.Tests` 里的 `LocalizationResourceChecks` 是同一套规则在测试套件中的副本
（`--project-editor-only` 运行）：它比对**三语键集是否一致**（缺键/多键都会 throw，并列出前 10 个键）、
三语格式参数是否一致，并实测 20 条 `code_editor.context.*` 是否真能按当前语言加载。它读的是构建输出目录里的
语言包，因此只覆盖「已打进产物」的状态；源码引用的键是否存在，仍只有 python 脚本能查。

键名写错不会编译失败；在没有 python 的环境里，`[missing: <key>]`（AXAML）或键名本身（`LocalizedText.Get(key)`）
也不会让既有测试失败——键存在性校验目前只由 python 脚本负责，新增调用点时不要绕过它。

## API 文本

每个类型化 HTTP 客户端都由 `AcceptLanguageHandler` 包装，后者将当前语言写入 `Accept-Language`。服务端会在 `Content-Language` 中回显选定的请求语言。诸如 RFC 7807 `ProblemDetails` 标题等 API 自有展示元数据由 `ApiLocalizer` 本地化；用户名、文件路径、书签名和原始主机错误文本等用户/领域值不会翻译或修改。

`ApiLocalizer` 只用于服务端**自己渲染**的认证类标题（11 个键，调用点在 `AuthEndpoints`）。其余端点一律用稳定问题码充当 `title`
（`title: code`），并在 `type` 后缀与 `problemCode` 扩展里重复同一个码；`detail` 是**英文**诊断文本，不是给用户读的句子。
客户端按码映射到本地化文案，`detail` 只进诊断详情。

服务端源码里因此不应出现中文用户可见文案：新增端点写 `title: code`，异常消息一律英文。
审计方法：`grep -P '[\x{4e00}-\x{9fff}]' RelaxKonOS.Server/**/*.cs`，命中只允许是注释、
`Settings/ApplicationCatalog` 的多语言标签与搜索关键词。

第三方包会收到 `IExternalAppContext.SystemLanguage`，应本地化自己的资源，并在 `LanguageChanged` 时刷新。

## 桌面语言切换追踪

客户端自动记录 `%LOCALAPPDATA%\RelaxKonOS\logs\language-switch-<启动时间>-<进程号>.log`，每行一个 JSON 事件。日志包含构建标识、进程、线程、递增序号、语言选择及下拉选项重建、本地化通知、偏好应用、同步读操作编号、HTTP 请求编号及服务端关联编号、保存语言和版本号。不记录令牌、服务器地址、完整偏好或异常消息。单文件上限约 2 MB，轮转段为 `.previous`；保留当前及最近五次运行。

复现步骤：启动新构建的客户端，打开设置的“时刻与语言”，依次切换 English、日本語、简体中文；再快速连续切换，并在保存中切换。记录最终选择、界面实际语言和问题发生时间，收集本次运行的日志及 `.previous`。`selection.completed` 与 `localization.notify.end` 用于确认本地即时状态，`sync.read.apply` 与 `preferences.apply` 用于识别旧读结果覆盖，`sync.read.skipped_draft` 表示草稿保护生效，`save.acknowledged` 表示服务器接受的语言及版本。

Headless 验证使用真实 `TimeLanguagePageView` 与双向绑定下拉框，检查连续切换后选中值、本地生效语言及已打开页面的翻译文本；日志写入测试输出的 `preview-qa/logs/`。此验证不代替连接实际服务器时的同步竞争复现。

2026-10-03 的 Headless 复现确认：更换语言选项集合时，ComboBox 双向绑定会写回旧选项，使刚选中的语言立即回退；无需服务器即可触发。设置页将选项重建排到当前选择写入完成后，在刷新选项期间忽略控件回写，并重新通知当前选中项，避免本地切换被刷新过程撤销。

## 中文界面术语

桌面与 Android 的中文界面按操作发生的位置命名：

| 场景 | 界面用语 | 说明 |
| --- | --- | --- |
| 运行客户端的设备 | 本机、此设备 | 上传来源、下载目标、剪贴板和外部浏览器。Android 可明确写“手机”。 |
| 运行 RelaxKonOS Server 的机器 | 服务器 | 系统账户、权限、代理、进程、安装和诊断均指服务器侧。 |
| 容器外的机器与端口 | 主机、主机端口 | 保留容器领域的标准术语，避免与容器端口混淆。 |
| 通过 SSH 隧道登录 | SSH 服务器、SSH 登录隧道 | 不向用户暴露“受管登录”或安装标识作为连接类型名称。 |
| RelaxKonOS 管理的资源 | RelaxKonOS 管理的证书／共享／运行时 | 不单独使用“受管”；说明谁在管理什么。 |
| Nginx 安装来源与控制范围 | 由 RelaxKonOS 安装、已接入管理 | 前者由 RelaxKonOS 安装并管理；后者是已有实例接入站点配置管理，不能混为一种状态。 |
| Guardian 管理的程序 | 守护程序 | 程序配置、启动、停止、重启和日志使用一致名称。容器部署中的“工作负载”仍可保留。 |
| 查询已提交操作 | 操作结果记录、重新读取当前状态 | 不使用“回执”“回读”“当前事实”等实现术语；结果未知不等于失败，也不应提示直接重试。 |
| 代理影响的对象 | 适用范围、生效状态 | 不使用“消费范围”或“出站偏好”。 |

短按钮直接描述动作；复杂管理归属和限制放在说明文案中。错误提示说明操作对象、失败原因和用户可以采取的下一步，避免暴露接口实现细节。

上述位置命名参考 [Microsoft 远程桌面的远程设备与本地资源用语](https://learn.microsoft.com/zh-cn/windows-app/get-started-connect-devices-desktops-apps)及 [VS Code Remote SSH 的远程机器与端口转发说明](https://code.visualstudio.com/docs/remote/ssh)。这是本产品的文案规则，技术文档中的运行宿主、API 名称与协议字段按实际技术含义保留。
