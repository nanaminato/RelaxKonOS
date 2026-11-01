# 第三方模块高信任扩展评审（DC-06）

> 状态：评审完成，2026-11-01。结论：当前版本**不实现**高信任扩展执行；所有自由命令/扩展程序请求保持稳定、明确的拒绝。本文记录威胁模型、平台隔离评估、若实施所需的独立授权与失败语义，以及为什么“已签名”不等于“可安全执行”。实现边界由 [声明式特权方向](RelaxKonOS.ModuleDeclarativePrivileges.Direction.md) 和 [Broker 设计](RelaxKonOS.ServerModulePrivilegeBroker.Design.md) 共同约束。

## 1. 范围与决定

本评审针对“现有 `service.manage`、`package.manage`、`file.manage`、`data.manage`、`app.manage` 等类型化能力无法表达，模块希望携带一个受审查程序并由宿主执行”的机制。它不是 `ProcessExecute` 的产品化，也不是 shell/解释器/包管理器的通用入口。

决定：

- 当前版本不新增 `process.execute`、`high-trust.extension`、`shell.execute`、`command.execute` 能力类别，不新增 Helper 执行任意程序的封闭操作。
- 清单 `schemaVersion=3` 仍拒绝未知字段；携带 `highTrustExtensions` 的清单在读取时拒绝，不会“先接受声明、以后再说”。
- 配置 `ModuleHighTrustExtensions:Enabled=true` 或任何未知高信任设置会使 Server 启动失败；只接受显式 `Enabled=false` 作为部署决策，缺省也保持拒绝。
- 试图以高信任类别提交的请求返回稳定错误 `module.privilege_high_trust_extension_unsupported`，而不是通用 unknown 或命令代理。
- 缺少普通适配器时不会自动升级到高信任执行；模块必须删除该功能或等待宿主显式支持。

## 2. 资产与威胁模型

保护资产：主机 root/LocalSystem、其他模块的数据与秘密、用户会话与管理员凭据、宿主服务可用性、审计证据、模块包来源信任、网络与文件系统边界。

| 威胁 | 说明 | 若实现必须处理的边界 |
| --- | --- | --- |
| 恶意发布者 | 发布者签名的程序本身即可执行恶意行为 | 签名只证明身份/字节；必须按发布者/程序摘要独立授权，不能把“已签名”当成安全结论 |
| 模块被攻陷 | 合法程序被利用或参数被操纵 | 参数 schema、固定工作目录/环境、最小权限、网络/文件系统限制、超时与进程树终止 |
| 供应链替换 | 同名程序在不同版本中改变行为 | 精确程序 SHA-256、发布者、扩展 ID 与参数 schema 全部冻结；安装更新不继承旧授权 |
| 权限扩大 | 扩展要求 root/LocalSystem 或额外 capability | 独立的高信任授权；完整 root 必须标为主机级代码信任，由部署策略决定，不能与普通能力共用授权 |
| 混淆代理 | 模块借宿主服务、环境变量、配置或工作目录注入执行 | 不允许 shell/解释器/包管理器作为入口；不接收 argv、自由文本、任意环境或任意 cwd |
| TOCTOU/路径竞态 | 校验后替换程序、配置或输入 | 受保护 staging、打开文件描述符校验摘要、原子发布、执行前再次核对 |
| 秘密外泄 | 扩展读取模块秘密、管理员凭据或其他实例数据 | 目的限定的秘密通道；不通过 argv/env/文件路径传递；输出脱敏与长度限制 |
| 资源耗尽 | CPU、内存、文件描述符、子进程或磁盘被占满 | cgroup/Job Object 限制、进程树回收、输出上限、并发与超时 |
| 持久化/横向移动 | 扩展写入启动项、服务、账户或网络监听 | 只读根/受限写入根、无特权的运行身份、网络命名空间或显式网络策略 |
| 失败后残留 | 超时/崩溃留下子进程、挂载、临时文件或半完成状态 | 失败终止、进程树 kill、临时资源清理、耐久回执、进入待恢复而不是重放 |
| 审计误导 | 日志显示“已签名”即被当作安全 | 审计记录程序身份、摘要、参数摘要、运行身份、限制、退出码与终止原因；不记录秘密 |

## 3. 平台可实现的隔离评估

### Linux

候选组合：专用低权限 UID/GID、`no_new_privs`、capability 清零、只读根与私有 tmp、mount/PID/net/user namespace、seccomp-bpf、cgroup v2 CPU/内存/PID/IO 限制、固定 `cwd`/环境、关闭继承文件描述符、执行前打开并校验程序摘要、超时后 kill 整个进程树。

可行但不充分：`systemd-run` 的 `DynamicUser`/`ProtectSystem`/`PrivateNetwork`/`NoNewPrivileges` 等属性可提供一层沙箱，但需要目标发行版 systemd 版本矩阵、cgroup 委派、嵌套容器和真实逃逸测试；属性拼写或版本差异不能作为行为隔离的证明。

### Windows

候选组合：独立低权限本地账户或 AppContainer、受限令牌/低完整性级别、Job Object 资源与进程树限制、ACL 保护的 staging、关闭继承句柄、网络 capability/防火墙限制、LocalSystem Helper 只作为启动器而非业务执行者。

可行但不充分：Job Object/受限令牌能限制资源与部分访问，但不能证明任意 Win32/驱动/COM 行为安全；AppContainer 的 capability 与文件系统代理需要逐项实测。签名 MSI/PE 仍可能携带恶意自定义动作。

### macOS

沙箱 profile、独立低权限用户与资源限制可用，但当前项目没有 macOS System Helper 的完整验收矩阵；不纳入本轮支持声明。

结论：没有完成“真实隔离 + 逃逸测试 + 失败终止 + 跨平台矩阵”的实现，因此不能把任何候选组合作为已交付能力。

## 4. 若实施所需的独立授权

高信任扩展必须与普通能力授权、代码信任、常设许可分离：

1. **部署策略**：root/LocalSystem 配置显式启用高信任机制，并按发布者、扩展 ID、程序 SHA-256、平台和参数 schema 允许；默认关闭。
2. **安装期审查**：展示程序绝对路径、发布者签名、SHA-256、参数 schema、秘密输入通道、运行身份、文件/网络权限、cwd、环境、超时和资源限制；用户安装模块不等于授权扩展执行。
3. **每次执行确认**：默认每次确认；不允许常设许可覆盖高信任扩展；确认绑定模块/发布者/程序摘要/参数摘要/主机状态/计划哈希。
4. **Helper 独立复核**：重新校验程序摘要、参数形状、运行身份、策略与前置状态；不接受 Server 传入的任意命令。
5. **秘密通道**：仅通过目的限定的受控通道输入，秘密不出现在 argv、环境、计划、日志或普通回执。
6. **失败关闭**：任何读不到策略、摘要不符、隔离不可用或平台不支持都拒绝，不降级为普通进程或 shell。

### 提议但**未接受**的清单形状

以下仅用于未来评审，不属于 `schemaVersion=3` 当前契约；当前清单读取会因未知字段拒绝：

```json
{
  "highTrustExtensions": [
    {
      "extensionId": "example-helper",
      "executable": "server/linux-x64/helper",
      "sha256": "64-hex",
      "publisherId": "example",
      "parameterSchema": "example-helper.v1",
      "runAs": "dedicated-unprivileged",
      "workingDirectory": "package-root",
      "environment": [],
      "network": "none",
      "timeoutSeconds": 60,
      "memoryBytes": 268435456
    }
  ]
}
```

该形状仍不能表达任意程序的安全行为；`runAs=root` 必须进入单独的主机级代码信任评审，而不是普通高信任扩展。

## 5. 失败与恢复语义（实施时）

- 执行前写耐久回执并记录程序摘要、参数摘要、运行身份和限制；回执不含秘密。
- 启动失败、超时、非零退出、输出超限和进程树残留分别映射稳定错误；超时/崩溃必须终止整个进程树。
- 已产生不可逆副作用而无法证明终态时进入 `RecoveryRequired`，不自动重放；操作员只能核对原操作、隔离/清理或明确关账。
- 不允许把“签名有效”“退出码 0”或“输出看起来正常”当作安全或业务成功证据。

## 6. 当前实现边界与证据

- `ModuleCapabilityCatalog` 不包含任何高信任/命令类别；`process.execute`、`high-trust.extension`、`shell.execute`、`command.execute` 均返回 `module.privilege_high_trust_extension_unsupported`。
- 清单读取拒绝未知 `highTrustExtensions` 字段；没有“双格式解析”或未实现字段的静默保留。
- `PrivilegedOperationKind` 没有 `ExecuteCommand`/`ProcessExecute`/`Shell`；`PrivilegedOperationRequest` 没有 executable/argv/command-line 字段。Helper 只接受已交付的类型化操作。
- `ModuleHighTrustExtensions:Enabled=true`、非布尔值或 allowlist 配置使 Server 启动失败；显式 `Enabled=false` 或缺省保持拒绝。
- 定向检查：`Tests/ServerModules --module-high-trust-checks` 覆盖上述拒绝码、类别缺失、清单未知字段、配置守卫和协议形状；完整回归计数见 [v2 实施记录](RelaxKonOS.ServerModuleV2.Progress.md)。

## 7. 重新评审门槛

只有同时满足以下条件才考虑实现：

1. Linux 与 Windows 各自完成真实隔离、逃逸测试、资源限制和进程树终止证据；不以 mock 或单平台代替另一平台。
2. 独立部署策略、每次确认、程序/参数/秘密绑定和 Helper 独立复核均已实现并通过拒绝/漂移/重放测试。
3. 证明高信任路径不能被普通声明式能力、服务定义、文件写入、包安装脚本或环境变量旁路。
4. 明确完整 root/LocalSystem 的主机级代码信任语义，且部署策略可拒绝。
5. 有可解释的失败、恢复、审计和卸载行为，且不会把未知结果自动重放。

在满足这些条件前，DC-06 的“明确拒绝”就是当前正确的交付状态。

## 8. 关联文档

- [第三方模块声明式特权能力：下一阶段方向](RelaxKonOS.ModuleDeclarativePrivileges.Direction.md)
- [服务端模块提权 Broker 设计](RelaxKonOS.ServerModulePrivilegeBroker.Design.md)
- [统一特权 Helper 架构](privileged-helper.md)
- [受管运行时设计](../platform/RelaxKonOS.ManagedRuntimes.Design.md)
- [v2 实施记录](RelaxKonOS.ServerModuleV2.Progress.md)
