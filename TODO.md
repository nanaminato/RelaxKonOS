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
