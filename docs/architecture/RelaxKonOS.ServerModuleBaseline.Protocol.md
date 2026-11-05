# Server 当前版本资源基线测量方案（MB-01）

状态：2026-11-05 的预定测量口径；文档和采样器的存在不代表业务矩阵已运行。实施结果由[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#8-实施顺序与跟踪项)记录。[资源清单](RelaxKonOS.ServerModuleResources.Inventory.md)负责资源所有者；本文负责测量方法，不改变模块策略或恢复权威。

## 固定输入与运行清单

每批开始前保存 `manifest.json`，运行后不能以调整口径挑选更漂亮的结果。至少包含以下字段：

| 项目 | 必须固定并保存的值 |
| --- | --- |
| 源码 | 完整 commit、分支、`git status --porcelain`；有工作区源码改动时保存 diff SHA256 和补丁（检查秘密后保存），不能只写 HEAD |
| 构建 | Release、`win-x64` / `linux-x64`、framework-dependent、self-contained=false、准确 SDK/runtime、publish 命令；两平台同一源码，分别只构建一次，各轮不重新编译 |
| 制品 | 发布树相对路径、大小、每文件 SHA256 和总清单 SHA256；跨机归档 SHA256 上传后复核；记录独立 Modules/ 路径及私有依赖 |
| 机器 | OS/内核、CPU/核数、RAM、容器/cgroup/作业内存限制、用户/权限模式、电源模式、并行负载、.NET/GC设置、时间与时区、文件系统 |
| 配置 | 完整有效模块 ID 集合、相同驻留策略、300秒宽限期、30秒唤醒时限、Production、回环地址、原有 Helper 模式、可用外部服务版本；秘密只记录是否提供，不保存原值 |
| 数据 | 独立工作目录中每轮从同一初始夹具复制：数据库/仓库/日志/文件大小与哈希；Git提交及工作树固定；禁止以生产数据副作用重放获得相同快照 |
| 身份/连接 | 同一授权角色和执行身份；预定会话一名用户、一条HTTP顺序请求通道、一条Terminal会话（80×24、相同shell/工作目录）；无性能订阅、无额外Hub；连接创建/关闭时间和回执另记 |
| 外部责任 | 现有 daemon/受管软件与原有连接保持运行；记录其 PID/启动身份、责任归属和采样权限。无法采到的独立服务标缺，不能假定成本为零 |

当前 `Measure-ServerModuleBaseline.ps1` 复现的是2026-10-28的静态开关口径：仅父进程、八模块集合、未认证healthz、无真实业务及动态释放。它保留为历史工具，不能直接作为本批验收器，也不能把旧全关差值当作当前可回收成本。当前集合以 `ServerModules.Catalog.cs` 与运行中的只读目录交叉核对。

当前工具分工：`run_server_baseline.py --publish <publish> --output <new-dir> --commit <full-sha> --rid <rid>` 测完整宿主新进程启动与就绪后闲时，默认仅开启集合；显式 `--cases on-off` 才增加静态全关对照。Core 仍读取固定主机路径，独立 content root 不等于文件系统/服务隔离，恢复责任核实失败不得绕过。`run_module_workload.py --workload <publish>/RelaxKonOS.ServerModules.Tests.dll --output <new-dir>` 每轮交错 git-first/terminal-first 共六次，仅测局部生产服务夹具；它不启动完整 Server、不连接真实 Hub、不覆盖真实 Firewall。其 `passed` 只表示业务断言和 CLI 阶段通过，不能作为指标完整性或 MB-01 整项通过。

`summarize_module_baseline.py <run-root> --output <summary.json>` 保留每轮结果与指标，不跨轮相减。宿主取首次达到 ready+5s/35s 的点；夹具每阶段取最后三点的中位数，缺失传播为 null。汇总者仍须检查每轮退出/业务结果、阶段样本数与完整指标，排除失败或不足采样的单元。当前宿主工具只在就绪后采树，启动峰值缺失；结束时只处理本次父进程，Windows 是强制终止，未另做退出后全树核验，不能作为正常停机或进程树释放证明。正式方案其余矩阵仍按下表补齐。

## 场景与交错顺序

每平台先进行一次不纳入统计的完整预热，再至少三轮独立冷进程。这里“冷启动”指新Server进程，不是清空操作系统文件缓存。不能为了制造磁盘冷态清理系统缓存。

使用同一发布制品交错 `G1,F1,T1,G2,F2,T2,G3,F3,T3`，其中 G 为 Git 实际本地仓库业务、F 为 Firewall 只读查询、T 为 Terminal 真实会话。每次新Server从同一夹具启动，默认有效模块集合相同。不得把两模块关闭的构建当作释放后状态。每个运行内都测初始闲时、业务后、真实释放后和再唤醒；同一轮对应阶段形成配对。仅三轮时报告三原值、中位数与范围；不从三个样本宣称稳定p95。

| 阶段 | 动作与必须确认的事实 | 采样窗口 |
| --- | --- | --- |
| 启动 | 在spawn前记单调时钟；首次healthz 200记录就绪耗时，进程提前退出或120秒未就绪为失败 | 从spawn到就绪持续1秒采样，捕捉可观测峰值；采不到启动早期须注明 |
| 初始闲时 | 就绪后5秒和35秒；只读目录确认Git/Firewall真实驻留状态；不通过业务页轮询唤醒 | 每点前后各2秒、1秒间隔；另外保留整个运行时间序列 |
| G业务 | 已认证调用真实Git端口：列仓库、打开固定本地仓库、读取固定提交/目录/文件和状态；记录成功回执、数据摘要、每个请求耗时 | 请求前、执行中每秒、完成后5秒/35秒；不以受控执行器代替实际CLI |
| F业务 | 已授权调用当前Firewall实际平台只读状态/规则入口；Helper不可用/权限拒绝记失败或缺失，不替换假成功 | 同上；禁止增加、删除、重排规则或重启防火墙 |
| T业务 | 按当前授权身份通过真实Terminal路径创建一条会话，等PTY就绪，执行固定 `pwd`/`printf`（Windows对应命令），核验唯一标记返回；保留会话35秒，断开但可重连阶段另采，再明确关闭会话并核验树结束 | 创建前、worker+shell稳定5秒/35秒、仅断线后、明确关闭后5秒/35秒；会话/树结束必须有原生证据 |
| 释放 | G/F无活动责任后，优先等待原300秒宽限期并观察真实Dormant；若单独测手动TrySleep，另列场景并保留原管理计划/回执，不能与自然闲时混合 | 持续采样至Dormant后5秒/35秒；超过预定观察预算仍未释放为阻塞，不以超时判成功 |
| 再唤醒 | 对G/F再次发同一只读业务，核验业务结果和新代次；Terminal重新建立新会话，核验新树与唯一输出 | 实际请求延迟、唤醒前后持续采样与5秒/35秒点 |
| 结束 | 通过正常退出关闭本次独立Server；核验所有自有后代，失败保留现场与责任 | 父进程退出与整棵树消失分别记录，不将退出后0当成闲时节省 |

以上是最小可复现业务集合，不声称覆盖真实Docker/BuildKit、Proxy网络发布、CA/Nginx或tailnet变更。若实际运行使用较小集合，报告精确差异并将未执行单元列缺失。请求不能因为超时而自动重放；未知副作用保留原operationId及原恢复权威。测量不安装、更新、修复或回滚受管软件。

## 进程树与指标

`Tests/Performance/sample_process_tree.py` 使用Python标准库只读采样。可重复传 `--pid` 纳入经owner证明属于本次运行、但不在父子树上的Helper或ModuleHost根。它不读取命令行、环境变量或令牌，不修改GC/工作集，不终止进程。输出文件必须不存在，避免覆盖旧证据。

```text
python Tests/Performance/sample_process_tree.py --pid <server-pid> --output <run>/tree.jsonl --samples 480 --interval 1 --label G1
```

采样器应从Server启动后尽早持续运行；spawn到采样器就绪的盲区写入manifest。一个调用期间以PID+启动身份跟踪，已观察的后代在重父化后继续采样，PID复用拒绝归属。快照并非原子操作，短命进程可能落在采样间隔之外；权限不足或身份未知时保留缺失，不能以空树证明清理完成。由Job/cgroup/subreaper/模块owner确认的后代清单与采样清单须交叉核验，不能只依赖PPID发现树。采样进程本身及客户端不计入Server树，但记录其观察开销。

| 指标 | Linux来源与口径 | Windows来源与口径 |
| --- | --- | --- |
| RSS/工作集 | `/proc/PID/smaps_rollup` Rss，缺失时status VmRSS | GetProcessMemoryInfo.WorkingSetSize |
| PSS | smaps_rollup Pss，按映射比例分摊共享页 | 本采样器不提供，JSON为null并写缺失原因 |
| 私有驻留 | Private_Clean + Private_Dirty（不含另行核算的大页） | 本采样器不提供，不能用PrivateUsage替代 |
| 私有提交 | 不提供，不用VmData伪造 | PROCESS_MEMORY_COUNTERS_EX.PrivateUsage；这是提交虚拟内存，不能称物理RSS |
| 线程/CPU | stat num_threads、utime+stime除CLK_TCK | Toolhelp线程数、GetProcessTimes内核+用户时间 |
| 句柄/FD、IO | fd条目数、io read_bytes/write_bytes；权限失败记缺失 | GetProcessHandleCount、GetProcessIoCounters传输字节；Windows IO口径与Linux物理IO不可直接比较 |
| 托管堆/GC/代码映射 | 另行只读诊断采集并记录工具/采样开销；本采样器不提供 | 同左；不能从工作集反推存活堆或ALC回收 |

JSON保留每进程原始status/smaps_rollup/io或Windows结构字段及采样时间。每指标的 `complete_sum` 只有所有纳入进程都可读取时才有值；`observed_sum` 是不完整可见小计，必须同时展示缺失进程数。RSS之和只称“各进程RSS合计，含重复共享页”；优先用Linux PSS合计表达树分摊占用，PSS也不等于系统释放的独占物理页。绝不把RSS、PSS、私有提交、私有驻留相加成一个“总内存”。不同时间的各进程峰值也不能相加伪造同时树峰值。

Terminal分列父Server、独占.NET worker、shell、其后代、sudo运输进程和实际Helper；Windows还需列ConPTY有关自有进程。Linux当前本地worker来自 `LinuxUserTerminalPty` 的 `RelaxKonOS.Server terminal-worker`，与Helper路径不同，不能将sudo的小RSS当Helper成本。报告单会话稳定窗口的worker原值、含/不含worker的同时间树值、配对创建前后差异及关闭后残留；共享页仅以同一时点PSS比例归属，净新增成本仍受其他进程共享与系统活动影响。

## 证据、缺失与决策

原始证据放Git忽略的 `.artifacts/module-baseline-<日期>/`，保留manifest、环境信息、制品哈希、完整日志、JSONL、带单调时钟的业务/驻留/会话事件和每轮结果。日志先检查秘密；测试机凭据既不复制进证据、文档，也不提交Git。连接复用原凭据与已核验主机密钥；不修改测试机防火墙、Tailscale连接、账号/服务或生产数据。

报告应分别列“实现且已验证”“实现但实机未验证”“未实现或仍有阻塞”。每个缺失项写明原因（权限、平台不提供、工具未部署、服务不可用、业务失败），不补零、不造数字。对象释放、ALC弱引用消失、父进程退出与受控分配检查分别是对应边界证据，均不能自动变成生产内存下降。

下一模块优先级依据真实业务后的配对稳态范围、必要责任比例、释放能力、冷路径延迟和工程风险。新优化实施前预先登记目标：树PSS或私有提交改善必须超过本批重复波动；冷启动/唤醒延迟与错误预算由业务要求给定，样本量足够后才计算p95；权限/取消/恢复/长连接正确性零新增失败。三轮数据只足以初步排优先级，噪声内结果不指定收益、不扩大SystemPerformance S1范围。没有真实业务和全树证据时，优先补齐缺口而不是依据历史单点给模块排名。
