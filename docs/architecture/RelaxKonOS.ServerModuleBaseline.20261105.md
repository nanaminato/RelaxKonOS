# 当前 Server 资源基线阶段验收（MB-01A，2026-11-05）

本阶段交付[全模块资源清单](RelaxKonOS.ServerModuleResources.Inventory.md)、[可复现测量方案](RelaxKonOS.ServerModuleBaseline.Protocol.md)与两类互不混同的实测切片。**MB-01/RES-01 不关闭**：完整宿主启动/闲时与局部生产服务业务夹具不能合并为完整宿主真实业务/自然释放基线。没有得出生产内存已下降的结论。

## 基线身份与边界

产品源码固定 `443b0b7e1143fca826a51239afd19ea17022ca6a`（master，开始时工作区干净）；本次只新增测量工具、测试驱动和文档，不改变产品资源策略。每类正式制品分别按两个 RID 一次 Release/framework-dependent/self-contained=false 发布，轮间不重建。业务驱动 v2 修正了 Git stage 成功断言和实际 PTY 输出确认；v1 探索结果不纳入业务验收。完整宿主 product publish 不依赖驱动改动。两RID分别核对Server.dll及Git/Firewall执行DLL，完整宿主与夹具发布的对应二进制SHA256均相同（product-fixture-binary-check.json）。

Windows：Windows 11 Home 10.0.26200、Core Ultra 7 265K、20逻辑核、32916312 KiB 可见内存、SDK10.0.300、运行时10.0.8。Linux：Ubuntu26.04、7.0.0-42-generic x86_64、普通 UID1000、VMware 2 vCPU（Core Ultra 7 265）、5620932608 B内存、.NET10.0.14；宿主机并行负载未受控，准确cgroup及发布文件逐项SHA256保存在原始环境证据。客户端/采样器不计进Server树。

完整宿主采用 Production/system、默认9个内置模块、默认IdleDelayed/300秒、每轮新空SQLite/content root、零认证会话、仅顺序healthz。冷启动指新进程，不清OS文件缓存。宿主在ready后每秒采样36秒，缺启动期树/峰值；5秒和35秒取首次达到该年龄的原始点。Windows额外全关是静态配置对照，不能称为休眠节省。

Core会读取固定主机路径，独立content root不是完整隔离。未部署或修改Helper/现有daemon；Helper可用性没有通过该基线验证。Linux静态全关由于无法核实Proxy固定路径下的operation/TUN/guard/Squid恢复责任而启动失败，原始stderr保留；未清台账、放宽权限、改网络或绕过门禁。Linux正式默认集合另跑三轮，没有合法全关对照。

业务夹具为真实Git runtime/coordinator、本地Git CLI及SQLite仓库端口，加一个真实TerminalSession/native PTY；它没有完整Host DI/HTTP认证/Hub/Helper。固定一个owner、一仓库、一份`tracked.txt`确定内容、一条80×24会话；运行依次git-first、terminal-first，重复三轮。Windows PowerShell与Linux `/bin/sh` 的shell不同，不能跨平台直接比较shell成本。每阶段驻留5秒，使用最后三点中位数；手动TrySleep不改300秒配置、不强制GC。它不等于自然闲时或35秒长期稳态测量。

## 数值与解释

数值使用MiB（1048576 B）；RSS合计含重复共享页，Linux优先看同时间PSS分摊合计。Windows私有提交不是私有驻留页，不与RSS相加。以下只纳入成功完整采样的对应切片；失败与探索批次不混入统计。

### 完整宿主：逐轮启动与ready+35秒

| 平台/集合 | 轮次 | healthz ms | 树RSS MiB | 树PSS MiB | 私有驻留 MiB | 私有提交 MiB |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| windows/off | 1 | 2290.1 | 153.50 | 缺 | 缺 | 69.20 |
| windows/off | 2 | 2277.5 | 152.42 | 缺 | 缺 | 68.90 |
| windows/off | 3 | 2269.4 | 153.63 | 缺 | 缺 | 69.91 |
| windows/on | 1 | 3031.4 | 173.89 | 缺 | 缺 | 88.45 |
| windows/on | 2 | 3093.1 | 174.14 | 缺 | 缺 | 89.17 |
| windows/on | 3 | 3111.7 | 174.86 | 缺 | 缺 | 89.80 |
| linux/on | 1 | 3771.0 | 223.32 | 199.69 | 186.79 | 缺 |
| linux/on | 2 | 3723.1 | 222.91 | 199.21 | 186.30 | 缺 |
| linux/on | 3 | 3630.7 | 222.33 | 198.87 | 186.18 | 缺 |

Windows默认集启动中位数3093.1ms，35秒RSS中位数174.14MiB、私有提交89.17MiB；全关分别2277.5ms、153.50MiB、69.20MiB。这些是静态启用集合控制值。Linux默认集启动中位数3723.1ms；其三个原值不能与Windows直接作性能比较。每个宿主采样末点仅观察到一个根进程，未调用业务并不代表Helper/外部服务成本为零。

### 局部Git业务：释放与再唤醒

每格为RSS / 平台主指标（Windows私有提交，Linux PSS），单位MiB；不是完整宿主。

| 平台/顺序 | 轮次 | 业务后 | 手动Dormant后 | 再唤醒后 | 再唤醒业务 ms |
| --- | --- | --- | --- | --- | ---: |
| windows/git-first | 1 | 100.21 / 39.75 | 100.39 / 39.72 | 98.72 / 36.76 | 162.7 |
| windows/git-first | 2 | 100.47 / 39.70 | 100.59 / 39.67 | 98.64 / 37.70 | 163.8 |
| windows/git-first | 3 | 100.26 / 39.48 | 100.40 / 39.45 | 98.91 / 37.45 | 157.3 |
| windows/terminal-first | 1 | 100.96 / 39.66 | 101.07 / 39.64 | 99.64 / 38.57 | 170.5 |
| windows/terminal-first | 2 | 101.18 / 40.04 | 101.30 / 40.02 | 99.49 / 37.79 | 163.9 |
| windows/terminal-first | 3 | 101.52 / 40.68 | 101.66 / 40.66 | 99.45 / 38.11 | 163.9 |
| linux/git-first | 1 | 145.71 / 125.22 | 145.80 / 125.32 | 147.46 / 126.98 | 60.4 |
| linux/git-first | 2 | 146.06 / 125.39 | 146.16 / 125.49 | 146.74 / 126.08 | 58.8 |
| linux/git-first | 3 | 145.91 / 125.23 | 146.02 / 125.34 | 147.63 / 126.96 | 57.7 |
| linux/terminal-first | 1 | 150.80 / 129.91 | 150.89 / 130.01 | 153.11 / 132.23 | 54.9 |
| linux/terminal-first | 2 | 150.70 / 129.83 | 150.80 / 129.93 | 151.70 / 130.83 | 59.0 |
| linux/terminal-first | 3 | 150.75 / 129.92 | 150.82 / 130.01 | 151.58 / 130.76 | 55.7 |

12次业务结果均核验成功，Dormant后再唤醒代次均为2；释放时旧ALC弱引用12次均仍存活，未强制GC。Linux释放后PSS反而小幅增加，Windows短窗变化也不足以归因。这里没有观察到足以证明生产内存下降的证据；不能从后续自然GC/JIT变化倒推卸载收益。所有RSS/私有驻留/私有提交的逐阶段原值在对应summary/JSONL中保留。

### Terminal：逐轮会话树与独占worker

Linux每行取同一阶段末三点中位数；worker是marker所报transport_owner_pid对应的独占.NET进程。无worker树PSS由同时间树PSS减该worker PSS，再取中位数；不是关闭worker后的反事实净成本。

| 顺序/轮次 | live树RSS | live树PSS | worker RSS | worker PSS | 同时间无worker树PSS | closed树PSS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| git-first-1 | 217.72 | 153.33 | 53.78 | 29.66 | 123.67 | 133.91 |
| git-first-2 | 216.93 | 152.40 | 53.76 | 29.65 | 122.76 | 132.83 |
| git-first-3 | 217.80 | 153.40 | 53.55 | 29.58 | 123.82 | 133.88 |
| terminal-first-1 | 195.60 | 133.39 | 53.55 | 29.71 | 103.68 | 114.80 |
| terminal-first-2 | 194.91 | 132.70 | 53.46 | 29.49 | 103.21 | 114.64 |
| terminal-first-3 | 195.71 | 133.27 | 53.80 | 29.71 | 103.56 | 114.75 |

Linux worker RSS约53.46～53.80MiB、PSS约29.49～29.71MiB，另有父夹具、shell和sleep。创建/关闭改变共享页分摊，worker PSS并非整树净新增内存；本次5秒窗不是峰值或生产稳定态。六次closed最终三点均检查原始树中是否仍可见已知owner，见measurement-verification.json；这不补足父进程退出后的未知后代核验。

Windows走原生ConPTY，没有独占.NET worker；下表为父夹具+PowerShell/ConPTY相关可观察后代的树合计，不把PowerShell称为worker。

| 顺序/轮次 | live树RSS MiB | live树私有提交 MiB | closed树RSS MiB | closed树私有提交 MiB |
| --- | ---: | ---: | ---: | ---: |
| git-first-1 | 176.63 | 98.93 | 98.79 | 37.21 |
| git-first-2 | 176.61 | 100.31 | 98.70 | 36.70 |
| git-first-3 | 176.95 | 99.73 | 100.04 | 39.42 |
| terminal-first-1 | 158.32 | 94.80 | 82.68 | 34.31 |
| terminal-first-2 | 158.39 | 94.59 | 82.25 | 33.88 |
| terminal-first-3 | 158.26 | 94.48 | 81.98 | 33.44 |

Terminal前后树差额只描述本次会话关联资源，关闭工作不构成模块休眠或生产内存优化。两种顺序同时保留：Git是否已加载明显影响父进程，不能跨顺序相减归因给Terminal。真实Hub传输/重连与多会话容量仍未测。

## 已实现且已验证

- 9个内置模块及两种安装运行模式的根引用、后台/连接、缓存预算、私有依赖、外部进程、持久恢复权威、释放层级已逐项审计，并链接实际源码。
- Windows原生API与Linux `/proc` 采样器实际父子树自检通过；原始内核字段、PID启动身份、缺失原因与非原子树限制保留。
- Windows完整宿主默认/静态全关交错各三次、Linux默认集三次启动/闲时采样完成；Linux全关失败得到真实门禁错误，没有当作通过。
- v2真实Git/Terminal夹具两平台各六次（两顺序交错三轮）均exit0；每个业务/释放/唤醒/live/closed阶段末三点的RSS及平台主指标完整，12轮代次均为2。Windows/Linux默认模块回归均通过；两平台采样器自检与Python语法检查通过。
- v2 Git工作负载核验真实init/register/stage/status/list/console，Dormant后再次读取并核验新代次。Terminal收到真实`MB01_READY`才报告live，明确关闭等待真实session任务与工作责任结束。

## 已实现但完整实机矩阵未验证

采样工具不提供Windows PSS/私有驻留、Linux私有提交、托管堆/GC或代码映射。启动峰值、短于1秒的子进程、外部共享服务/Helper全部成本、退出后全树核验、严格无干扰负载、电源模式/父cgroup与Windows作业上限完整采集和所有平台安装模式仍缺。Windows结束调用terminate是强制结束本次父进程；它发生在测量后，不是模块释放或正常停机证据。部分本机发布与早期宿主基线时间重叠，不能声称严控外部负载，也不据其范围指定生产SLO。

## 未实现或仍有阻塞

真实Firewall只读Helper端点业务/释放/唤醒未接入；夹具明确输出firewall-missing，不把受控端口替代成业务成功。完整宿主认证Git/TerminalHub、断线保留/重连、原300秒自然Dormant、业务后与释放/再唤醒35秒窗口、外部长期服务归属仍待补齐。Linux全关是恢复权威核实受阻，不能通过清账解决。当前不要求用户改变现有网络或提供新密码。

## 下一阶段与验收门槛

1. **MB-01B优先补正式宿主矩阵**：同源双RID、默认九模块、真实认证Git端口、Helper Firewall只读、TerminalHub；三轮配对业务后/自然Dormant/再唤醒，固定授权身份、数据、连接及35秒窗口，增加启动期采样和退出后owner树核验。固定路径责任核实继续fail-closed，缺失指标保留缺失。
2. **Terminal容量/worker候选**：再测1/多会话的同时间树PSS/私有提交、worker/shell/Helper明细、创建/重连延迟及关闭后残留。当前会话总数无整体门禁，每会话缓冲与独占.NET worker是规模风险线索。不得为减少开销取消身份/树所有权，必须保持断线保留、慢消费者和未知清理责任。
3. **Git/Firewall收益验证**：利用现成释放边界进行完整Host配对测量，真实业务成功/新代一致、自然GC待回收可见、无代次积累；收益须超过重复噪声。不能依据本次夹具或Windows静态全关差额指定模块节省量。Firewall真实写入故障矩阵继续开放，不为基线改规则。
4. **常驻领域后续**：Proxy先完成持久调度/安全owner交接，Docker/Certificate/WebServer先核实恢复与故障责任再拆执行图。Certificate历史/idempotency/gate规模是数据阶梯测量候选，源码增长线索不是泄漏结论。SystemPerformance S1继续冻结。

延迟/内存生产SLO需在完整基线和业务要求上预先登记；三轮仅报告原值/中位数/范围，不计算可靠p95。正确性门槛是授权、取消、未知结果、恢复和长连接零新增失败，不能以平均内存改善抵消责任丢失。

## 证据与复现

原始证据保留于Git忽略目录 `.artifacts/module-baseline-20261105/`，包括首次失败及v1探索，不覆盖或删除。测试机凭据仍在原忽略文件，未打印、复制进文档或纳入Git；SSH沿用原固定ed25519主机密钥，归档上传核验SHA256。正式Windows目录为`full-windows-v3/`、`workload-v2-windows/`，Linux回收副本为`remote-evidence-v2/{full-v2-linux,workload-v2-runs-linux}/`；远端保留`/home/nanami/relaxkonos-tests/module-baseline-v2-20261105`。`full-*-summary.json`、`workload-*-summary.json`及`measurement-verification.json`保存逐轮校验；`workload-manifest-*.json`、`final-source-provenance.json`及每完整宿主environment.json保存制品/源码摘要。旧宿主manifest中“isolated Helper”仅是未验证注释，实际Core仍探测主机固定路径；以本报告的可用性限制和原始日志为准。

Windows证据归档`evidence-windows.tar.gz` SHA256：`2234135d391fb2a330c07e21e76a03ec86fceda438149415690181a5d79144e9`。Linux证据归档`evidence-v2-linux.tar.gz` SHA256：`29350d6aa84e7e1f7dc69051dd46228cff64f5ad8a5d5dbeb46ef87b653cab4f`；上传制品归档SHA256：`5829abb79ff81e5d545d610c40969c13e18a1ae6b42967ea5942a5bf879ee91c`。`remote-v2-provenance.json`三条命令（完整宿主、预热、六次业务）均exit0；旧`remote-provenance.json`的全关失败保留且未计入。Windows宿主采样后terminate退出码1另列，不称正常停机；Windows业务六次均exit0。
