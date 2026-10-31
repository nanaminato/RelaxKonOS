# 服务端 ModuleHost v2 实施记录

更新：2026-10-31。这里只记录当前实现与证据，不将设计目标视为已交付。

| 跟踪项 | 当前状态 | 已有证据 / 余项 |
| --- | --- | --- |
| MD-01 唯一 v2 契约 | 已实现并回归 | manifest / installed / package-plans 使用 schemaVersion=2，拒绝 v1；打包器、CLI、SDK、Server 与桌面同步 |
| MD-05 Linux 身份与权限 | 已实现；独立运行时实机验证 | 每模块稳定专用 UID/GID，无附加组、no_new_privs；root 拥有只读版本快照，模块拥有私有数据；root 监督器控制超时/断管清理 |
| MD-05 Windows 身份与 ACL | 未实现 | 生产启动失败关闭，不继承 Server 账户冒充隔离 |
| MD-06 安装与代码信任 | 已实现并回归 | 冻结计划展示 runtime/privileges；精确信任绑定版本/发布者/归档与清单摘要；更新不继承，撤销立即阻断代理/Broker |
| MD-07 / PB-01/02 Broker 核心 | 已实现并定向回归 | 服务启动/停止/重启；持久计划、确认、幂等、重放/过期/漂移拒绝、并发单执行、响应丢失进入 RecoveryRequired |
| PB-03 Core/SDK/桌面 | 已接线并编译 | SDK 仅在 Server 代理签发的当前请求上下文中 prepare；Core 用户 API 查看/批准/拒绝；桌面输入 planId 审查和提交，不将密码发给模块 |
| PB-05 审计 / 实机闭环 | 未收口 | Linux Core→ModuleHost→Broker→真实 Helper 正常闭环已实测；需完善管理员审计身份、Helper 故障与进程中断验收 |
| PB-06 常设许可/后台提权 | 未实现 | 无当前请求 context 一律拒绝 prepare；没有自动执行或后台队列 |
| 完整发布安装与更新 | 已接入打包/安装脚本，尚未实机验收 | 发布包含 server/module-host；Linux 安装器配置 root 文件和固定 sudoers；不能把独立探针视为已升级现有 Server 实例 |

## 本地验证

构建命令：

```text
dotnet build Tests/ServerModules/RelaxKonOS.ServerModules.Tests.csproj -p:NuGetAudit=false -p:UseSharedCompilation=false -m:1
dotnet Tests/ServerModules/bin/Debug/net10.0/RelaxKonOS.ServerModules.Tests.dll
```

完整 ServerModules 回归 **566 项 PASS**，包括包 HTTP 上传/计划/apply、签名/撤销/路径攻击、安装/更新/回退/启动核实、第三方不进 Core、精确信任、Broker 门控及独立进程路由。此前一次 HTTP apply 503 未在本轮完整回归中再现；本记录不将其隐去或作为已定位根因。

可单独运行 `--module-host-checks`、`--module-package-checks`、`--module-privilege-checks`。Windows 沙箱不允许所需 loopback/临时文件原子替换，以上实际检查在获准的沙箱外执行。现有编译警告尚在；没有将“零错误”表述为“零警告”。

## Linux 测试服务器证据

服务器：100.113.194.85，Ubuntu 26.04 x64。未在文档、脚本或仓库保存登录或提权密码。

独立测试运行时位于 `/opt/relaxkonos-modulehost-test`，测试源包位于测试用户的 `modulehost-v2-test/packages`，隔离根为 `/var/lib/relaxkonos-module-host-test`。初始独立探针的配置绑定测试用户 UID。随后安装器已为完整 v2 验收实例重建 `/etc/relaxkonos/module-host.json`，绑定现有 `relaxkonos-server` 服务账户 UID（999），源包根指向该验收实例数据。

`Tests/ServerModules/verify-linux-module-host-isolation.py` 已针对实际 root 启动器运行，验证：

- 模块实际身份为 `rkmod-*` 非 root 账户；
- 不可写自己的只读清单；自己的 Storage 可读写；
- 不可读 `/etc/shadow` 与实际存在的旧 Server `data/module-management/state.json`；
- 不可连接实际 `/run/relaxkonos/privileged.sock`；
- ready / health / shutdown 完整退出通过。

实测暴露并修正了 root 运行时目录权限问题：从 Windows 生成的 tar 解包可能将顶层目录置为 0777；启动器拒绝这种路径。生产安装脚本在授予固定 sudoers 前检查整个运行时及祖先必须 root 所有且不可由 group/other 写入。测试副本已收紧权限后复测通过。

尚未验证：其他模块实际私有目录越权、恶意挂起/脱离后代清理、完整发布包升级、Helper 中断、owner-device/sessionValid 关闭及 Windows 专用身份。原有 Server/Helper/Guardian 实例仍运行原版本；新完整验收实例单独运行，未替换旧实例。

## 约束

生产不使用 ModuleHosts:Entries 指向任意包目录；这些配置仅在 Development 下供诊断。生产从已安装、已启用、有精确信任的 module-host 记录启动。Linux SourceRoot 只用于 root 启动器经 openat2 读取并验证文件；模块只能访问其复制快照和私有数据，不获得 Server 数据目录遍历或 Helper socket 组。

当前 Broker 的操作集合是显式封闭的三个服务动作；Host 的 ModulePrivileges:Services 与 Helper 的 root 管理服务白名单都必须允许目标。包/文件/网络/任意进程操作没有实现，也不会回退到 shell。


### 完整 Linux 正常闭环实测

`/opt/relaxkonos-v2-test` 使用本轮构建的新 Server/ModuleHost；`relaxkonos-v2-test.service` 在现有非特权服务账户下监听 `127.0.0.1:16591`。独立测试 Helper 使用 `/run/relaxkonos-v2-test/privileged.sock`。现有三项原服务未停止或改写服务配置。

`verify-linux-v2-broker.py` 已执行并通过：真实 PAM 登录；本地签名目录接纳；第三方 v2 包上传、运行模式/提权预览、确认安装与精确信任；受管启用计划及重启；专用 ModuleHost 代理访问；错误管理员密码不执行；Core 确认后真实 Helper 重启固定 `relaxkonos-module-broker-fixture.service` 并以原生 PID 变化核实；重复批准被拒绝且 PID 不变；管理员验证记忆仍要求新计划确认；撤销精确信任立即阻断业务代理与待执行计划。

脚本通过 getpass 读取凭据，只在内存中用于 sudo/PAM/Core，不输出或保存令牌/密码。验收结束时该第三方模块信任已撤销。测试固定服务仅运行 `/usr/bin/sleep infinity`。

新实例曾因父数据目录缺少服务账户遍历权限、缺少 Production 必需的观测配置启动失败，已在测试准备脚本修正。未导出原始 journal；只使用预定义分类输出辅助排查。正常闭环实测不能替代中断/恢复验收。

用户已明确要求 Linux 与 Windows 均实现与测试，因此 Windows 专用身份仍是当前目标的必需剩余工作，整体目标保持未完成。
