# RelaxKonOS 开发者 CLI

`relaxkonos-dev` 会发布 RelaxKonOS 应用项目并创建其 `.roapp` 包，无需为项目编写专用 Shell 脚本。

```bash
relaxkonos-dev pack ./MyApp --configuration Release
relaxkonos-dev pack ./MyApp --configuration Debug --no-build
relaxkonos-dev pack ./MyApp --runtime win-x64 --configuration Release --install
relaxkonos-dev watch ./MyApp --runtime win-x64 --configuration Debug
```

`pack` 默认写入 `artifacts/<entry-assembly>.roapp`。它要求 `.csproj` 旁存在 `manifest.json`；可用 `--manifest` 和 `--output` 覆盖这些路径。默认会用 `dotnet publish` 重新编译指定的 `Debug` 或 `Release` 配置；传入 `--no-build` 时改为 `dotnet publish --no-build`，直接打包该配置已有的编译结果。它会打包 `manifest.json` 的 `entryAssembly` 所声明目标框架目录下的完整发布输出，包括私有依赖项和原生运行时资产；若清单声明 `iconPath`，也会安全地复制该相对路径的图标资源。

对会连接正在运行的 RelaxKonOS Shell 的命令设置 `RELAXKONOS_DEV_TOKEN`（或传入 `--token`）：`--install`、`watch`、`apps`、`install`、`update`、`launch` 和 `uninstall`。POSIX shell 使用 `export RELAXKONOS_DEV_TOKEN="<pairing-token>"`，Windows PowerShell 使用 `$env:RELAXKONOS_DEV_TOKEN = "<pairing-token>"`。使用 `watch --no-install` 可在不连接 Shell 的情况下构建包。

不带参数运行 `relaxkonos-dev` 可查看完整命令参考。RelaxKonOS 仓库的“开发者模式”指南说明包格式和兼容性约定。

## 远程设置服务

`settings` 直接调用远程 Server，独立于设置窗口和本地 Developer Bridge。必须显式指定 HTTPS origin；宿主 JWT 从 `RELAXKONOS_HOST_ACCESS_TOKEN` 读取，开发配对 token 不授予宿主权限。不要把 token 写入脚本或提交到仓库。

```sh
relaxkonos-dev settings --server https://remote.example:5001 catalog
relaxkonos-dev settings --server https://remote.example:5001 time
relaxkonos-dev settings --server https://remote.example:5001 preview-time --revision <snapshot-revision> --idempotency-key <unique-key> --zone <remote-zone-id>
relaxkonos-dev settings --server https://remote.example:5001 apply-time --id <reviewed-plan-id>
relaxkonos-dev settings --server https://remote.example:5001 operation --id <plan-id>
relaxkonos-dev settings --server https://remote.example:5001 rollback --id <plan-id> --revision <observed-revision>
```

先读取远程枚举的时区 ID，预览后检查差异、目标、影响和期限。应用仅提交原 planId；同一宿主 JWT 必须已有 `HostTimeChange` 对 `host/time` 的短期授权。缺少授权时返回 Server 的结构化 428 错误，不弹密码窗口。输出为 Server JSON，HTTP 失败退出 1。网络异常不自动重试写入；使用原 planId 查询操作，不生成新计划重做不确定的写入。回滚重新检查权限和观测 revision。当前 CLI 尚未接入主机名或 DNS。


## 环境变量

环境变更使用远程宿主作用域 `hostUser` 或 `hostMachine`。先用 `environment-target` 发现服务端绑定的授权目标；同一 JWT 需已有该目标的 `HostEnvironmentRead` 授权，预览和回滚读回也需要它。应用/回滚另需 `HostEnvironmentChange`；`--reveal` 另需 `HostEnvironmentReveal`，不会自动申请或扩大授权。默认读取只输出名称、类型、掩码和元数据；显式揭示会把原值写到标准输出，请只在需要显示这些值时使用。

```sh
relaxkonos-dev settings --server https://remote.example:5001 environment-target --scope hostUser
relaxkonos-dev settings --server https://remote.example:5001 environment --scope hostUser
relaxkonos-dev settings --server https://remote.example:5001 environment --scope hostUser --reveal
relaxkonos-dev settings --server https://remote.example:5001 preview-environment --scope hostUser --revision <snapshot-revision> --idempotency-key <unique-key> --changes changes.json
relaxkonos-dev settings --server https://remote.example:5001 apply-environment --id <reviewed-plan-id>
relaxkonos-dev settings --server https://remote.example:5001 operation --id <plan-id>
relaxkonos-dev settings --server https://remote.example:5001 rollback --id <plan-id> --revision <observed-revision>
```

`changes.json` 使用共享 `EnvironmentChangeSet` 格式；`--changes -` 从标准输入读取 UTF-8 JSON。输入上限 2 MiB，服务端继续验证 128 项/256 KiB 数据上限及远程平台语义。不要把变量值放到命令行参数中。

```json
{
  "changes": [
    { "name": "JAVA_HOME", "operation": "Set", "value": "/opt/jdk", "valueKind": "String" },
    { "name": "EMPTY", "operation": "Set", "value": "" },
    { "name": "OLD_VALUE", "operation": "Delete" }
  ],
  "confirmHighImpact": false
}
```

空字符串是持久值，删除必须使用 `Delete` 且不带 value。PATH/运行时注入等高影响变更须检查后显式设置 `confirmHighImpact: true`。预览保持脱敏，审阅本地变更文件与计划目标、影响和期限后再提交 planId；不要修改文件后误以为原计划会跟随变化。CLI 命令已实现，Linux provider 与跨平台真实写入验收仍待完成。
