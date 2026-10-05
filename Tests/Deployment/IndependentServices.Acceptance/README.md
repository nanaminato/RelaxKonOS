# 独立组件服务实机验收

仅在可丢弃的隔离主机运行。先部署同版本 Server/Helper，以 root 或 Windows 管理员执行。测试会安装、启动、停止或卸载真实组件服务，必须指定 `--confirm-isolated-host`。

```powershell
dotnet publish Tests/Deployment/IndependentServices.Acceptance/IndependentServices.Acceptance.csproj -c Release -r win-x64 --self-contained true -o artifacts/acceptance-win
dotnet publish Tests/Deployment/IndependentServices.Acceptance/IndependentServices.Acceptance.csproj -c Release -r linux-x64 --self-contained true -o artifacts/acceptance-linux
```

运行模式：`setup` 安装 FRPS、两个 FRPC、Mihomo，Windows 同时安装 Nginx；`setup-mihomo` 可在首次流程因下载失败中断后独立完成 Mihomo，不重复安装已存在的 Nginx；`status` 使用全新进程读取服务和应用证明；`stop-start` 检查实例隔离；`rollback` 占用回环端口验证失败应用恢复旧配置；`restore` 恢复 FRPS 基线；`cleanup` 通过生产 Helper 移除服务。

FRP 使用 17099、19090/19091，Mihomo 使用 17890/17991，Windows Nginx 使用 18080。所有配置使用回环地址及固定测试 Token，不用于生产。主机登录凭据不属于此工程。

无 GitHub 连通性时，在 runner 同目录提供官方 `frp-linux.tar.gz` / `frp-windows.zip` 与 `mihomo-linux.gz` / `mihomo-windows.zip`。Helper 与共享清单仍核验 SHA-256；Windows archive roots 必须采用安装器配置的物理数据目录。

结果和边界见[进度文档](../../../docs/services/RelaxKonOS.IndependentComponentServices.Progress.md)。这是 Helper/系统服务层验收，不替代 GUI/API、版本升级和选择性卸载测试。

`cleanup-mihomo` 先通过生产 Helper 停止服务、移除服务，再清理本程序固定版本的暂存文件（不创建或伪造 Server 管理记录）。选择性卸载的实机检查须另走真实部署启动器：请求 `removeComponents: "nginx,frp"` 与 `retention: "retain"`，核对精确回执、保留的 Mihomo 和数据库；再按原路径重装并检查健康。完整删除数据且保留部分组件必须拒绝，清理失败必须保留 Helper、程序和管理数据。本轮证据见上述进度文档。
