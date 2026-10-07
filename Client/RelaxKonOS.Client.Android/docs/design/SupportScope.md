# Android 支持范围

Android 使用原生 Compose 页面管理已有远端主机。生产容器、Nginx、FRP、Mihomo、SMB 和宿主防火墙运行在远端；手机提供交互、SSH/SFTP、本地转发和系统应用访问入口。

## 应用组织

文件与 Git 共用文本编辑器，图片查看属于文件工作流。登录、首页、帮助和关于承担引导。服务地址交由系统浏览器或外部应用打开。Android 没有独立欢迎、记事本、内置浏览器或注册表应用。

内置功能随签名客户端更新；桌面 `.roapp` 包不能作为 Android 应用运行。移动包与权限边界见 [应用包说明](ApplicationPackages.Design.md)。应用分类、返回与状态保留见 [应用内功能导航](../features/ApplicationNavigation.md)。

## 部署与运维边界

服务器中心支持 SSH 信任、SFTP、隧道、安装选项、执行回执与独立健康核实。SSH 认证、包上传、安装结束、API 健康与账号登录分别核实。应用级安装观察、上传恢复、登录回填及恢复上版界面的覆盖有限，不能假定离页或进程回收后整个安装流程自动续行。具体流程见 [服务器中心](../features/ServerCenter.md)。

网站发布不提供 DNS 服务商自动化或内网发布集成；不收集未接入服务商的凭据。证书签发当前不支持提交 DNS-01。见 [网站发布](../features/WebPublishing.md) 与 [证书](../features/Certificates.md)。

Git 构建采用受限 Ubuntu BuildKit 环境。静态站点及其他构建模板、更新比较和按发布修订引用回收产物不属于当前完整能力；成功构建的镜像可能被发布修订引用，不能直接清理。见 [Git](../features/Git.md) 和 [构建环境](../development/GitBuild.Ubuntu.md)。

Android 提供定义备份创建、清单和只读预检，没有恢复提交界面。卷/数据库一致性恢复和跨安装秘密重绑定不属于当前能力；定义备份不能代表业务数据备份。告警观察与前台通知不保证事件持久重放或可靠后台送达。见 [任务、告警与恢复](../features/OperationsRecovery.md)。

## 平台与验证

Windows 设备密钥针对 Windows 10/11 工作站，不代表 Windows Server 支持同一授权流程。SMB 在 Windows 外部或漂移共享上为只读；防火墙管理按当前 Server 的 UFW 能力门控。能力可见不等于获得写入授权。

手机、平板、旋转、分屏、大字体、三语、主题、TalkBack、进程回收和真实宿主操作的验证要求见 [验证要求](../development/Verification.md)。文档中的功能描述不代表所有设备与宿主组合已经验证通过。
