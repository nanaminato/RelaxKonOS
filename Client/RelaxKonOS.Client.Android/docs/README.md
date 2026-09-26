# RelaxKonOS Android 文档

本目录是 Android 客户端的唯一详细文档源。它覆盖 Android 手机与平板的产品设计、实现进展和发布；仓库根目录的 [`docs/mobile`](../../../docs/mobile/README.md) 仅保留跨项目入口。

| 文档                                                                                               | 用途                                                              |
| ------------------------------------------------------------------------------------------------ | --------------------------------------------------------------- |
| [`RelaxKonOS.Mobile.Design.md`](./RelaxKonOS.Mobile.Design.md)                                   | Mobile Shell 初稿：应用目录、导航、手机/平板自适应、国际化、多主题、安全边界和验收。               |
| [`RelaxKonOS.Mobile.V1.Design.md`](./RelaxKonOS.Mobile.V1.Design.md)                             | 初版（V1）可落地设计：功能集与能力门控、页面清单与路由、界面流转、指纹解锁已保存凭据（服务器密码与管理员密码）。       |
| [`RelaxKonOS.Mobile.LoginCredentials.Design.md`](./RelaxKonOS.Mobile.LoginCredentials.Design.md) | 登录与本地凭据：身份唯一键、四个独立概念、登录决策表、密码框与「已保存密码」的关系、切账号/忘记密码/删除记录、明文生命周期。 |
| [`RelaxKonOS.Mobile.ServerCenter.Design.md`](./RelaxKonOS.Mobile.ServerCenter.Design.md) | 服务器中心在既有登录页、连接管理、自适应导航和凭据模型中的 Android 接入方式。 |
| [`RelaxKonOS.Mobile.Progress.md`](./RelaxKonOS.Mobile.Progress.md)                               | 已实现范围、验证记录、已知限制和下一阶段。                                           |
| [`RelaxKonOS.Mobile.BulkUpload.Design.md`](./RelaxKonOS.Mobile.BulkUpload.Design.md)             | 大文件上传（分块与续传）的 Android 侧设计：源可寻址策略与缓存落盘、前台服务、分片循环、续传日志、状态归属与验收。 |
| [`android-release.md`](./android-release.md)                                                     | Android 本地环境、构建、调试、签名和发布要求。                                     |

除面向所有客户端的架构或 Protocol 契约外，新的 Android 专属设计和实施文档必须放在这里。
