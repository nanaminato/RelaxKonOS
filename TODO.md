# TODO

- 容器化应用部署（镜像、Java/.NET/Python 程序包）：[Goal](docs/applications/RelaxKonOS.ApplicationDeployment.Goal.md)；[实现及测试进度](docs/applications/RelaxKonOS.ApplicationDeployment.Progress.md)。

- 文件浏览器 Windows 11 体验优化：[进度与后续任务](docs/applications/RelaxKonOS.Explorer.Progress.md)。

- Add a trusted app-catalog/install flow for missing third-party URI handlers. For `help://` links,
  offer installation of Help Center, then retry the original URI after a verified install. Do not let
  source applications such as Docker Manager install packages directly.
1. 排名不分先后
2. 文件上传，解压，以及进度可视化
3. 文件浏览器框选逻辑
4. 程序安装器（服务器）
5. task 抽象脚本化便于自动化执行（Agent）
6. 定时任务
7. 拖动文件夹/文件到桌面

   优先级	做什么	价值
   1	在真实 Linux + Docker 主机验收应用部署	把刚完成的旗舰能力变成可发布能力
   2	新增“事件与告警中心”	汇总部署失败、证书续期、Guardian 崩溃、Docker 异常、隧道断开等，并提供审计、跳转与处理入口
   3	开发 Android 运维伴侣 Beta	登录、主机状态、告警推送、容器/守护进程启停、日志查看、紧急恢复
   4	再逐步扩展移动端	先补文件浏览、终端，再评估是否需要 Git、部署向导等复杂功能


Android 的产品定位应是“随身控制台”，而不是“手机上的完整桌面 OS”。首版明确不做多窗口桌面、完整 Explorer、代码编辑器和内置浏览器；重点是用户离开电脑时仍能发现问题、确认风险操作、快速止血。
这条路线还有一个很好的复利：事件中心先给桌面带来即时价值，之后只需加入设备注册与 Android 推送，就自然成为移动端最有吸引力的入口。

新增“事件与告警中心” 汇总部署失败、证书续期、Guardian 崩溃、Docker 异常、隧道断开等，并提供审计、跳转与处理入口
设计一个目标以将此功能做成一个优雅的应用，