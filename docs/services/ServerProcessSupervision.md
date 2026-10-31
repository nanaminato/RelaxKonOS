# 服务端进程监督与健康停机恢复

> 状态：2026-10-31。适用于外部服务监督器（systemd、Windows Service、容器编排）托管 RelaxKonOS Server 的部署。Host 本身不重启自己；健康看门狗只请求停机并设置非零退出码。

## 1. 健康停机契约

开启：

```json
{
  "ModuleHealth": {
    "Enabled": true,
    "StopOnUnhealthy": true,
    "ProbeIntervalSeconds": 60,
    "StopExitCode": 1
  }
}
```

- `StopExitCode` 范围 1–255，默认 1。
- 只有启用中的模块出现 `unhealthy`、`load-failed` 或 `quarantined` 才会触发。
- 触发时：
  - 写 `<content-root>/data/module-management/health-stop.json`；
  - 设置进程退出码 `StopExitCode`；
  - 请求 `IHostApplicationLifetime.StopApplication()`。
- 不会修改启用集合、安装、回退或加载新模块。
- 监督器必须配置“非零退出后重启”；否则健康停机只会让服务保持停止。

## 2. systemd

`/etc/systemd/system/relaxkonos-server.service` 示例（按实际安装路径替换）：

```ini
[Unit]
Description=RelaxKonOS Server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=relaxkonos
Group=relaxkonos
WorkingDirectory=/opt/relaxkonos/server
ExecStart=/opt/relaxkonos/server/RelaxKonOS.Server
EnvironmentFile=-/etc/relaxkonos/server.env
Restart=on-failure
RestartSec=5
TimeoutStopSec=30
KillSignal=SIGINT
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

启用与检查：

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now relaxkonos-server
systemctl status relaxkonos-server
journalctl -u relaxkonos-server -n 100 --no-pager
```

`Restart=on-failure` 会响应健康停机设置的非零退出码。

## 3. Windows Service

`sc.exe` 恢复策略示例（`<service-name>` 替换为实际服务名）：

```powershell
sc.exe failure "<service-name>" reset= 86400 actions= restart/5000/restart/15000/restart/60000
sc.exe qfailure "<service-name>"
```

如果使用 PowerShell 创建服务：

```powershell
New-Service -Name "RelaxKonOS.Server" `
  -BinaryPathName '"C:\RelaxKonOS\Server\RelaxKonOS.Server.exe"' `
  -DisplayName "RelaxKonOS Server" `
  -StartupType Automatic
```

创建后仍要设置上面的 `sc.exe failure` 恢复动作。

## 4. 验证

1. 在测试环境启用健康停机并让一个测试模块返回 `unhealthy`。
2. 观察 `health-stop.json` 的 `exitCode` 和模块状态。
3. 确认监督器在数秒内重新拉起进程。
4. 确认重启后的 `module-health` 不把旧的失败状态冒充为健康。
5. 确认没有自动安装、自动启用或自动回退。

## 5. 边界

- 本流程不包含应用层自重启；Host 不 `Process.Start` 自身。
- 容器编排的 `restartPolicy` 同样需要允许非零退出重启。
- 健康停机不是提权 Broker，不授予 root/Administrator 权限。
- 生产部署仍需要 Linux 实机/真实服务监督器验收；当前测试主要运行在 Windows 开发机。
