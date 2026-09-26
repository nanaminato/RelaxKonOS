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

## 无电脑部署计划

这组文档规划“只有手机与远端主机”的首次安装、部署和维护体验；状态为规划，不能据此认定功能已实现或验收。总路线图定义分期与共同约束，各计划定义具体交付和验收；实际进度仍统一记入 `RelaxKonOS.Mobile.Progress.md`。

| 文档 | 用途 |
| --- | --- |
| [总路线图](./RelaxKonOS.Mobile.Deployment.Roadmap.md) | 产品范围、已有基础、八项依赖、R0–R3 发布阶段与共同验收要求。 |
| [AD01：服务器初始化](./RelaxKonOS.Mobile.ServerBootstrap.Plan.md) | SSH 接入、可信包、首次安装、健康确认与服务器生命周期。 |
| [AD02：应用部署向导](./RelaxKonOS.Mobile.ApplicationDeployment.Plan.md) | 镜像与 Java/.NET/Python 包、部署任务、日志、更新与版本回滚。 |
| [AD03：模板应用库](./RelaxKonOS.Mobile.ApplicationCatalog.Plan.md) | 受维护应用模板、动态配置表单、兼容检查与模板版本管理。 |
| [AD04：Docker 与 Compose](./RelaxKonOS.Mobile.DockerCompose.Plan.md) | 容器资源管理、组合应用导入、资源归属与部分失败恢复。 |
| [AD05：网站发布与网络配置](./RelaxKonOS.Mobile.WebPublishing.Plan.md) | 域名、证书、站点、访问验证及后续内网发布。 |
| [AD06：Git 部署与轻量编辑](./RelaxKonOS.Mobile.GitDeployment.Plan.md) | 仓库到隔离构建再到发布，配置编辑、差异和提交。 |
| [AD07：终端、脚本与进程守护](./RelaxKonOS.Mobile.TerminalAutomation.Plan.md) | 移动终端、SSH 排障、一次性任务与持续工作负载。 |
| [AD08：运维与恢复中心](./RelaxKonOS.Mobile.OperationsRecovery.Plan.md) | 任务观察、断线核实、备份恢复、事件与通知。 |

除面向所有客户端的架构或 Protocol 契约外，新的 Android 专属设计和实施文档必须放在这里。
