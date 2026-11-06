# MB-01B 完整宿主验证（2026-11-06）

本轮沿用 MB-01A 的冻结 Windows/Linux Release 发布目录。产品源码以 `d9f4ae76` 为基准，其中 Server 二进制沿用 MB-01A 所记录的 `443b0b7e` 产品源码。工作期间进入 master 的其他里程碑提交保持独立，测量仍使用冻结制品。[MB-01A 资源与基线报告](RelaxKonOS.ServerModuleBaseline.20261105.md)

## 测量配置

完整宿主按原默认九模块和 300 秒宽限期启动，每个进程使用独立内容根和新建 SQLite。正式批次按 `on-1, idle-1, on-2, idle-2, on-3, idle-3` 运行。每轮从 spawn 开始以约 1 秒间隔观察父进程和已知后代，首次 healthz 200 作为就绪。纯闲时轮保持 36 秒窗口。业务轮先经过同样的初始闲时窗口，再使用一个已认证身份和固定的空仓库列表执行只读 Git 请求，使用真实 Helper 查询 Firewall 状态与规则。等待模块目录报告 Dormant 后重新读取。终端使用真实 SignalR WebSocket，使用一条 80×24 会话验证输出、断开、新连接附回与回放，然后关闭会话并检查它从会话列表消失。

Windows 采用 Personal 模式、现有所有者的 Windows Negotiate 引导和既有命名管道 Helper。探测器在每轮新数据库中注册临时设备密钥。Linux 正式补测使用无特权传输的 User Mode、现有 PAM 凭据和真实 PTY 入口，Firewall 请求受宿主能力门禁拒绝。Windows 和 Linux 的身份与 Helper 条件随证据分别记录。

可复现入口为 `Tests/Performance/run_server_baseline.py --cases on-idle` 和 `Tests/Performance/HostProbe/HostProbe.csproj`。先用 `dotnet publish` 生成客户端，冻结 Server 发布目录，指定新的 `--output` 目录与产品 `--commit`、`--rid`。Windows 传入 `--windows-personal-config` 与 `--windows-owner-probe`，前者指向当前所有者已经配置好的 Helper JSON。Linux 以被测账号运行并传入 `--authenticated-git --linux-user-probe`。账号凭据通过 stdin 送入，SSH 使用原固定主机密钥。

## 验收口径

启动期最大值是观测到的样本最大值。亚秒进程和采样点之间的瞬时峰值属于未覆盖范围。RSS、PSS、私有提交和私有驻留分别报告。已存在的持久 Helper 是共享外部进程，其内存不计入本次 Server 进程树合计。仅当目录观测到 Dormant、后续同一操作成功且状态回到 Active 时，才记作自然释放与再唤醒。对象释放与代码回收分开，生产内存收益仍需同环境对照和长期样本。

业务轮的 Git 操作是认证后的空仓库列表读取。非空真实工作树、长时间真实构建、镜像清理和外部故障属于独立验收。关闭终端后的会话查询仅验证业务清理结果。整树和脱离父子关系的外部进程仍按采样可见性记录。

## Windows 正式交错批次

三轮均通过 Defender Helper 只读及重读、认证 Git、原 300 秒策略下的自然释放和真实 Terminal Hub 重连。Server 日志确认 Git 和 Firewall 均释放第一代，后续请求唤醒第二代。终端独立保持连接与断开各五秒。下表以 MiB 表示整棵可观测树，私有列为 Windows 私有提交。

| 轮次 | 就绪 ms | 启动采样点 | 启动 RSS 最大值 | 启动私有最大值 | 就绪 35 秒 RSS | 就绪 35 秒私有 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| on-1 | 3131.0 | 3 | 146.43 | 69.27 | 175.55 | 89.59 |
| idle-1 | 3131.1 | 3 | 154.20 | 75.66 | 174.79 | 92.37 |
| on-2 | 3119.3 | 3 | 153.07 | 74.13 | 174.13 | 92.01 |
| idle-2 | 2914.1 | 3 | 151.64 | 71.65 | 173.43 | 90.37 |
| on-3 | 3185.8 | 3 | 154.69 | 76.19 | 175.09 | 92.96 |
| idle-3 | 3001.1 | 3 | 153.07 | 74.73 | 174.07 | 91.73 |

业务各阶段采用该阶段最后三点的中位数，数值为 RSS / 私有提交 MiB。自然休眠窗口从业务请求后计时并以目录实际读到 Dormant 为准。

| 轮次 | 自然 Dormant 秒 | 释放前 | Dormant | Terminal 已连接 | Terminal 已断开 |
| --- | ---: | ---: | ---: | ---: | ---: |
| on-1 | 301.838 | 246.18 / 131.02 | 246.38 / 131.22 | 338.31 / 201.04 | 337.07 / 199.09 |
| on-2 | 300.959 | 240.52 / 129.32 | 241.36 / 130.15 | 334.45 / 200.96 | 334.67 / 201.05 |
| on-3 | 301.216 | 241.00 / 129.79 | 241.68 / 130.46 | 334.20 / 200.18 | 334.42 / 200.22 |

Windows PSS 和私有驻留不可用。短窗内可见 RSS 和私有提交并未随 Git、Firewall 自然释放下降。应用对象状态与操作系统页指标各自保留。

## Linux 正式交错批次

Ubuntu User Mode 三轮认证 Git、自然释放、第二代唤醒和真实 Terminal Hub 重连均通过。Firewall 三次均由能力门禁返回 403。下表使用整棵可观测树的 MiB。Linux 的私有驻留原值保留在摘要及 JSONL，私有提交缺失。首轮就绪 PSS 低于同批其余样本，原值保留。此轮与测试机代理补救时段重叠，负载与共享页分摊未作因果归因。

| 轮次 | 就绪 ms | 启动采样点 | 启动 RSS 最大值 | 启动 PSS 最大值 | 就绪 35 秒 RSS | 就绪 35 秒 PSS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| on-1 | 3662.6 | 4 | 215.99 | 192.59 | 223.12 | 181.14 |
| idle-1 | 3688.0 | 4 | 215.57 | 192.30 | 222.73 | 199.14 |
| on-2 | 3665.7 | 4 | 215.86 | 192.78 | 222.64 | 199.22 |
| idle-2 | 3679.6 | 4 | 215.35 | 192.10 | 222.59 | 199.00 |
| on-3 | 3806.3 | 4 | 215.56 | 192.31 | 222.60 | 198.99 |
| idle-3 | 3583.3 | 4 | 215.73 | 192.45 | 222.75 | 199.18 |

各阶段取最后三点中位数，数值为 RSS / PSS MiB。三轮独占 Terminal worker 已连接窗口的 PSS 分别为 25.62、25.63、25.62 MiB。整树 PSS 已含该 worker 和观察到的 shell。RSS 可能重复计算共享页，worker 的 PSS 不能作为会话净新增成本。

| 轮次 | 自然 Dormant 秒 | 释放前 | Dormant | Terminal 已连接 | Terminal 已断开 |
| --- | ---: | ---: | ---: | ---: | ---: |
| on-1 | 301.601 | 287.09 / 248.31 | 287.63 / 248.83 | 354.57 / 275.83 | 354.78 / 275.80 |
| on-2 | 300.529 | 283.76 / 245.22 | 284.12 / 245.55 | 347.75 / 269.29 | 347.95 / 269.27 |
| on-3 | 300.753 | 283.89 / 245.24 | 284.30 / 245.61 | 348.30 / 269.92 | 348.55 / 270.05 |

## Linux 联合探索及停止条件

首次联合探索使用冻结 System 模式宿主、安装服务身份和真实 Unix socket Helper，在私有挂载命名空间隔离了 Server 持久目录。该轮 PAM、UFW 只读、Git 与 Firewall 约 300.370 秒自然 Dormant、两项再读均得到实际回执。Terminal 未收到输出标记，关闭时 Server 报告 `terminal.exit_confirmation_missing`。该轮作为失败整组保存。

启动期间还有一条 `LinuxSystemProxyApply` 成功记录。私有挂载只隔离了测试 Server 的状态，既有 Helper 在宿主命名空间处理请求。随后只读核对发现 `/etc/environment` 与系统代理恢复目录在 11:30:15 UTC 更新，恢复文件已不存在。既有服务的持久配置仍标记系统代理启用和守护关闭，环境中无代理键。启动前的 OS 代理值缺少快照，后续批次改用无特权 User Mode。

收到用户授权后，经既有服务的 Proxy 设置 API 提交一次原有设置，得到 HTTP 204。读回持久设置完全一致，HTTP、HTTPS、SOCKS 环境端点与已保存配置匹配，大小写键及 `NO_PROXY` 对恢复，Helper 建立了新恢复台账。旧台账历史没有快照。该补救发生在基线联合失败之后，单独保留 `mb01b-linux-proxy-restoration.log`。防火墙与 Tailscale 未执行变更请求，核对时 Tailscale 仍为同一进程。

已安装的 0.2.7 Linux 服务另行通过真实 UFW 只读和 Terminal WebSocket 重连、回放、关闭。该次使用另一个版本的既有服务。Long Polling 探测的 `Start` 因缺少认证上下文失败，原记录保留。无 Helper 的冻结 System 模式 PAM 批次也单独运行了三轮认证 Git 和自然释放。

## 阶段验收与证据

- **实现且已验证。** 两平台各三轮认证业务和三轮同集合纯闲时对照，启动期树采样、Git 自然释放和第二代唤醒、Terminal 真实断开与重连回放全部通过。Windows 另完成三轮真实 Defender Helper 只读、自然释放与再读。正式探测器六次退出码均为 0，各项结果以逐条业务事件核验。
- **实现但完整实机矩阵未验证。** 已观测启动树和短窗业务树。亚秒峰值、共享外部 Helper 完整归属、停机后全树消失、35 秒业务释放稳态、长时间句柄和内存压力仍缺。Windows 父进程采样后结束，退出码 1 独立记录。ALC 或对象释放的短窗状态不足以推断生产内存变化。
- **仍有阻塞。** Linux 同制品 System 模式真实 Helper 联合三轮仍缺。单轮已读 UFW 并验证自然 Dormant 后再读，但终端退出证明缺失，且宿主 Helper 有共享代理恢复责任。后续联合矩阵需要让被测 Helper 和全部可写恢复状态处于同一隔离范围，并核验终端真实退出回执。MB-01、RES-01/03 保持开放。SystemPerformance S1 维持冻结范围。

正式 `HostProbe.dll` 两平台核对同一 SHA256 `83504e727c16b3a93d86149f1ecfc59749ce1786334119088c8295a6297c57b1`。当前提交补充失败与清理错误的分别记录。正式批次保持原探测器，失败诊断改动通过独立构建验证。原始 `result.json` 的释放占位字段保留，`probe.log` 与 Server 代次日志是业务释放验收依据。

| 本地原始证据 | SHA256 |
| --- | --- |
| `.artifacts/mb01b-windows-evidence.tar.gz` | `c2244f2e71e1087682a4733ef22f3184cf1091a6db9f9adceefd7896f82fc367` |
| `.artifacts/module-baseline-20261105/mb01b-user-v2-evidence.tar.gz` | `9fc17ccf6037e12ea80fb60a9319c545832d5303dce71de6cb7428d4b51cf577` |
| `.artifacts/module-baseline-20261105/mb01b-integrated-failed-evidence.tar.gz` | `fe2d4d3411530cea3963219d9a9de3ae8287db9101c66d0a51fa65830c063c1f` |
| `.artifacts/module-baseline-20261105/mb01b-auth.tar.gz` | `7a7242c8dfaee086776083d50d93a96f8b837c76aea26f2f3bd0d68e05fa542f` |

逐轮验证见 `.artifacts/mb01b-verification.log` 及 `.artifacts/mb01b-integrated-{windows,linux}-summary.json`。每个批次的 `environment.json` 保留 RID、配置、运行时与逐文件制品摘要。代理补救、现有服务探测、启动控制和失败探测保留于 `.artifacts`，各自与正式交错数据分开。凭据文件留在原忽略路径。
