# MB-04 Proxy 读取执行图与配对测量

认证管理读取、流式投影、共享采样、读取租约与隔离负载的配对资源测量。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [MB-04 Proxy 管理面的配对驻留基线](#mb-04-proxy-管理面的配对驻留基线)
- [MB-04 Proxy 概览与启停执行边界](#mb-04-proxy-概览与启停执行边界)
- [MB-04 Mihomo 连接快照的流式读取](#mb-04-mihomo-连接快照的流式读取)
- [MB-04 完整认证 Server 的 Mihomo 活动连接](#mb-04-完整认证-server-的-mihomo-活动连接)
- [MB-04 Mihomo 512 条真实连接的配对测量](#mb-04-mihomo-512-条真实连接的配对测量)
- [MB-04 Mihomo 快照解析代码的闲时释放](#mb-04-mihomo-快照解析代码的闲时释放)
- [MB-04 Mihomo 概览的单次观测](#mb-04-mihomo-概览的单次观测)
- [MB-04 受管 Mihomo 概览的配对驻留测量](#mb-04-受管-mihomo-概览的配对驻留测量)
- [MB-04 Mihomo 概览和连接详情的 HTTP 工作责任](#mb-04-mihomo-概览和连接详情的-http-工作责任)
- [MB-04 Mihomo 连接计数与详情投影随代次释放](#mb-04-mihomo-连接计数与详情投影随代次释放)
- [MB-04 Mihomo 概览按 JSON token 计数](#mb-04-mihomo-概览按-json-token-计数)
- [MB-04 读取代次闲时卸载与原发布恢复](#mb-04-读取代次闲时卸载与原发布恢复)
- [MB-04 Mihomo 连接详情读取执行图](#mb-04-mihomo-连接详情读取执行图)
- [MB-04 Mihomo 流量与日志读取租约](#mb-04-mihomo-流量与日志读取租约)
- [MB-04 Mihomo 流量累计量使用有界连接快照](#mb-04-mihomo-流量累计量使用有界连接快照)
- [MB-04 Mihomo 连接详情按需解码评估](#mb-04-mihomo-连接详情按需解码评估)
- [MB-04 Mihomo 高连接数流量轮询基线](#mb-04-mihomo-高连接数流量轮询基线)
- [MB-04 桌面 Mihomo 流量轮询排空](#mb-04-桌面-mihomo-流量轮询排空)
- [MB-04 Mihomo 运行时与控制面读取责任](#mb-04-mihomo-运行时与控制面读取责任)
- [MB-04 Mihomo 控制面读取配对测量](#mb-04-mihomo-控制面读取配对测量)
- [MB-04 Mihomo 路由和 DNS 单次采样](#mb-04-mihomo-路由和-dns-单次采样)
- [MB-04 Mihomo 配置合并读取配对测量](#mb-04-mihomo-配置合并读取配对测量)
- [MB-04 Mihomo 重叠流量采样](#mb-04-mihomo-重叠流量采样)
- [MB-04 Mihomo 重叠概览采样](#mb-04-mihomo-重叠概览采样)
- [MB-04 Mihomo 跨路由运行时采样](#mb-04-mihomo-跨路由运行时采样)
- [MB-04 Mihomo 重叠连接详情采样](#mb-04-mihomo-重叠连接详情采样)

## MB-04 Proxy 管理面的配对驻留基线

冻结产品提交 `b3ceadcd5f0c9ed3249ce0a454c6de543fea06a9`，在同一 Linux 测试机的私有挂载、网络及 PID 命名空间运行完整 Release Server。非 root Server 使用独立生产 Helper 和 PAM。每轮先启用、再停用 Proxy 管理面，其他管理模块关闭。每次启动使用新内容根，Core 保留原安全门禁。六个测量进程均正常退出，宿主 Nginx、Docker、Mihomo、sing-box PID 及代理环境/日志指纹一致。

下表为 Server 进程树 PSS，单位 MiB。35 秒闲时列为启用减停用。读取列为相对前一阶段的变化，再扣除同轮停用对照的变化。每组认证阶段采五点中位数。

| 轮次 | 35 秒闲时差 | 设置增量 | 概览与运行时增量 | 档案与订阅增量 |
| --- | ---: | ---: | ---: | ---: |
| 1 | 4.58 | 1.88 | 6.79 | 0.84 |
| 2 | 3.10 | 1.64 | 7.62 | 0.66 |
| 3 | 5.79 | 0.21 | 7.49 | 0.81 |

概览与运行时组在三轮中增量最大，下一项优先拆开概览组合读取、控制器健康查询和运行时观测，定位各自的执行依赖。固定顺序会累积 JIT、路由、SQLite 和 HTTP 缓存，现有数据只能定位请求组。此轮使用空私有状态，未启动 Mihomo，未涵盖活动连接、非空档案或订阅转换。专属 Helper 未计入 Server 树。静态启停差用于候选比较，Proxy 继续 resident，动态释放及生产内存收益尚未测量。

复现和口径见[测量方案](../RelaxKonOS.ServerModuleBaseline.Protocol.md#固定输入与运行清单)。日志为 `.artifacts/mb04-proxy-measure-final-live.log`。产品与采样包 SHA-256 为 `1033667d69d1bd0a5dc55b8adc83d97d7c4823979245764367e0e7557fedd854`，原始样本包 `.artifacts/mb04-proxy-measure-raw.tar.gz` 的 SHA-256 为 `8c546a81f2340cb28f8da0540d560414e98750258f512c7b572634998fc72c02`，配对摘要 `.artifacts/mb04-proxy-measure-summary.json` 的 SHA-256 为 `33776e04b576ce3fc4c40984b657e366b72dfcb00d1280b2b9d257eb655353b6`。首轮因测试包目录缺少 Python 导入所需的读取权限在采样前退出，修正后完整复跑。

## MB-04 Proxy 概览与启停执行边界

概览与启停分成独立请求和后台执行图。启停协调器只依赖运行时状态和固定特权动作。概览通过 SQLite 直接查询一个活动档案，连接数复用详情的解析与记录筛选，最多统计前 2,000 条记录中的有效连接 ID。读取仍使用原请求取消、控制器认证和 Core 恢复状态。该阶段的完整缓冲由下节流式快照路径替代。

Linux Release 私有 systemd 173 项通过。生产概览服务在真实 Mihomo HTTP 代理连接等待源站响应期间读取同一活动档案，计数与连接详情一致。源站交付响应后，真实转发完成，配置派发次数未增加。停机排空原操作 `fdb15096-a0b8-495a-9c18-8b2b6a078a9b`、完整 Server 的独立 Helper 原失败回执 `8f271099-5dde-4e9e-a2c3-1f40acbc98a0` 和原中断补偿 `a9dc3553-2757-47ed-8d8c-f660add07521` 同批通过。宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-mihomo-overview-live.log`，归档 SHA-256 为 `105f4018620b9a1b3f70e66a43109aef2ba115d5a78460658613d7a0492cd654`。

Windows Release 控制器、宿主档案库、配置、Core Proxy 安全和模块边界回归通过。旧档案库夹具仍预期迁移 16，已对齐现有迁移 17。活动连接专项直接调用生产概览服务，完整认证 HTTP 联验与改动后同口径驻留复测仍开放。Proxy 保持 resident。

## MB-04 Mihomo 连接快照的流式读取

概览与详情共用一个增量 JSON 读取器。读取器保留当前待解析片段，并在需要映射时只解析当前连接记录。概览保留计数，详情最多保留 2,000 条投影。请求使用原控制器凭据和取消信号，配置的控制器期限覆盖响应体。待解析片段和已投影的单条记录上限为 256 KiB，总响应上限为 32 MiB。超过投影数量后仍验证原 JSON 至响应结束，截断、超限及重复连接字段不返回部分成功。

Windows Release 控制器专项覆盖分片、记录上限、截断、超长记录、重复字段、响应体超时和请求取消，模块边界回归通过。Linux Release 专属 systemd 的 173 项实测通过，真实持有的代理连接在流式计数与详情中的结果一致，源站交付后继续转发。原发布排空 `c1958e4c-7637-4520-8130-ca4f1c06d83a`、独立 Helper 原失败回执 `f7424d8f-25af-49e4-ad6c-8302e714bf55` 和原中断补偿 `60f92769-7b13-4e96-af50-102e0d4e5988` 同批通过，宿主服务及代理文件指纹一致。日志为 `.artifacts/mb04-mihomo-stream-live.log`，归档 SHA-256 为 `7be3fb280a983e91fd9513bc1868ecc3ab20d96a177bc0d5541ecc030c808b26`。

本阶段实机路径直接驱动生产概览和控制器。完整认证 HTTP 见下节，真实高连接数配对测量和动态休眠继续开放。片段上限为读入边界，本阶段未测量生产 PSS。

## MB-04 完整认证 Server 的 Mihomo 活动连接

私有 systemd 夹具在专属 Mihomo 代理已经转发请求后暂缓源站响应。完整非 root Server 通过生产 PAM/JWT 读取概览与连接详情，匿名请求返回 401。管理 Server 被强制终止并重新认证后，两个读接口继续报告同一个真实连接。夹具核对 Mihomo PID、系统代理所有权日志与 sing-box 安装修订，然后释放源站响应，验证原连接完成转发。

最终 Linux Release 同批通过原入站发布回执 `431c7040-91cd-44af-b062-dfbae93210c4`、原中断补偿 `095d574d-84d1-4b71-b9a6-b759ede28f5d` 和 Helper 重启后的原到期回执 `93a01e49-81f6-4cef-a095-0a9b2d27d17d`。TUN 路由恢复、系统代理与真实认证转发也通过。专属 cgroup 清理完成，宿主共享服务和代理文件指纹一致。日志为 `.artifacts/mb04-auth-overview-final-live.log`，归档 SHA-256 为 `411d0d0178654114a9c5e0f7fbff63c747a60ab0fedabc47c6315ec47f999049`。

首轮测试客户端未使用仓库的 JSON 枚举选项，在解析已收到的概览时失败。改用正式客户端选项后全组通过。Windows Release 注册 23 项与模块边界回归通过。高连接数配对测量见下节。

## MB-04 Mihomo 512 条真实连接的配对测量

专属 Linux systemd 中保持同一 Mihomo 和 512 条已抵达源站的代理连接。缓冲版和流式版各启动三次非 root Server，经真实 PAM 访问概览和连接详情。每次都验证原连接 ID 集合及 sing-box 修订。第二对交换顺序，六次结束后释放源站响应，512 条均通过原连接完成转发。

下表 PSS 单位为 MiB。增量为同一运行的观测峰值减去认证预热后采样点。延迟为 24 组概览加详情顺序读取的客户端中位数，单位 ms。

| 轮次 | 缓冲 PSS 起点 / 增量 | 流式 PSS 起点 / 增量 | 缓冲 / 流式延迟 |
| --- | ---: | ---: | ---: |
| 1 | 180.51 / 15.29 | 176.20 / 2.91 | 64.40 / 47.11 |
| 2 | 178.56 / 11.19 | 176.42 / 3.41 | 44.29 / 49.32 |
| 3 | 179.66 / 11.02 | 176.96 / 3.62 | 38.64 / 45.84 |

在该负载下，流式版的 Server PSS 增量每轮低 7.40–12.39 MiB，私有驻留增量同向。两组的请求延迟有重叠，尚无一致延迟改善。六轮共用私有状态和运行时，Server PSS 不包含 Mihomo、Helper 或压测客户端。进程样本反映此隔离负载，动态休眠和生产内存验收保持开放。

缓冲制品含 `6368616a` 的产品改动，DLL SHA-256 为 `9408eff90603893466102cb3b86e9f4aa12ef0c3c91e200650f9625eb4237d57`。流式制品含 `b6da7423` 的产品改动，DLL SHA-256 为 `89a9327aad197658a2cb8d8d5589c7bc0230cd8791a50075f9b6a2c276ec00f4`。两者沿用原验收前构建，程序集信息版本记录各自父提交。原始逐点数据和单组时延见 `.artifacts/mb04-proxy-connection-bench-live.log`，归档 SHA-256 为 `1c8d8a9a814820c4904332f1833bcc2edeade457ce7fbaffa5b9604ce3b05dfd`。

同批原发布 `59540474-8d65-4793-871a-98732e4a3195`、原补偿 `17fc8f80-7bd6-4362-9275-0f56378b3724` 和到期回执 `73157e88-1a6a-4289-a747-a83b9b77f0f4` 通过。TUN 与系统代理恢复同批验证，宿主共享服务及代理文件指纹一致，专属 cgroup 已清理。

## MB-04 Mihomo 快照解析代码的闲时释放

连接快照 JSON 解析移入现有 Proxy 独立程序集，跨边界使用框架缓冲、取消令牌和同步投影回调。Host 保留控制器认证、真实响应流、管道、返回 DTO 和错误映射。一次读取持有解析代次直到响应体和异步清理结束，并发读取复用同一代。最后一次归还后闲置 30 秒可卸载，再次读取创建新代。活跃及待回收代次合计上限为四个。旧代尚未回收时保留最后一个可用代次，继续接受读取并定期复核。生产路径不强制 GC。

此边界只回收解析代码。Core 系统代理、TUN、发布恢复及授权到期保留原持久责任。Proxy 整体保持 resident。

Windows Release 控制器专项验证并发读共享代次、实际异步清理阻止闲时卸载、旧代可回收及预算满时继续服务。Core Proxy 和模块边界回归通过，程序集依赖守卫 16 项通过。Linux Release 私有 systemd 最终 175 项通过。生产概览读取真实 Mihomo 连接后自然等待 30 秒卸载，测试 GC 确认旧 ALC 可回收。Mihomo 持续转发，后续真实连接唤醒新代，原配置未重发。

同批原发布排空 `4492521f-9520-4f11-a1a9-b3503bda6118`、独立 Helper 原失败回执 `0cb059c0-7a5d-4328-b2d2-deee3960ed8f` 与原中断补偿 `87b33f2b-d44e-4649-ad3b-48325d470b07` 通过。专属 cgroup 已清理，宿主共享服务和代理文件指纹一致。日志为 `.artifacts/mb04-proxy-parser-alc-final-live.log`，归档 SHA-256 为 `a678895dec4489408d566301e0247e625da9c823ce845c7d81a4f364672d8e19`。

完整 PAM/JWT Server 随后在 Linux 私有 systemd 复验。杀掉并重启 Server 后，通过认证 HTTP 读取同一条真实 Mihomo 连接的概览和详情，匿名请求仍返回 401。在重启后的同一 Server 进程中停止读取，测试启动钩子观察原解析代次的卸载事件。再次读取时观察不同代次加载，原连接 ID、Mihomo PID、发布修订和系统代理所有权日志均保持一致，原 HTTP 转发成功收到上游正文。启动钩子只记录加载与卸载事件，该路径不强制 GC。弱引用回收由上一轮专项验证。整个管理面休眠和生产内存仍待验收。

Linux 同批复验原发布 `c3736273-43a5-46a1-bab1-edba453528e4`、原补偿 `f7b20917-f4d9-4071-ac9c-6e38fa48ac79`、到期回执 `037a4904-8444-4f44-970b-5caac8a71fe8` 及 TUN 原操作 `3ea56d53-b983-4ca8-90a0-5a5f456319ec`。专属 cgroup 已清理，宿主共享服务与代理文件指纹一致。日志为 `.artifacts/mb04-auth-parser-idle-live.log`，归档 SHA-256 为 `6cb6959dc0a705a85afabfd1150f75408bd9970883d949bf11977326e869536c`。Windows Release 注册 23 项和模块里程碑回归在沙箱外通过。Docker 跨进程恢复读回原 `cf59a730-2f4d-448e-826c-e52e9e155ea6` 的 Interrupted 回执，副作用计数为一。首次沙箱运行遇到 TUN 测试控制器等待超时及证书台账恢复断言失败。

## MB-04 Mihomo 概览的单次观测

概览直接使用当前请求的控制器，省去完整管理引擎注册表。运行时在原所有权门禁内读取安装事实，若需确认监听状态，就复用同一请求的控制器观测。健康与概览使用同一份 Core TUN 恢复状态。观测只在当前调用有效，独立运行时接口继续即时探测。真实 401 保留原本的监听与认证区分，未核验的受管状态仍沿用原未知状态。该改动只调整读取执行图，Proxy 继续 resident。

Linux 私有 systemd 中的完整认证 Server Release 验证了概览与独立运行时接口一致。Server 重启及随后解析器闲时卸载与重载期间，同一 Mihomo 连接继续转发。原发布 `f6bd88d6-a0a8-40ca-9389-c361f09b391b`、原补偿 `368512f1-2501-4618-a8c5-0c9686a6d838`、到期回执 `d7176156-bcad-4029-88da-366a7034159e` 和原 TUN 操作 `58bdc3eb-86cb-4ec2-a484-d679f6adc742` 保持各自原回执及安全责任。专属 cgroup 已清理，宿主共享运行服务及系统代理文件保持原状态。日志为 `.artifacts/mb04-overview-observation-auth-final-live.log`，归档 SHA-256 为 `c04d37752f1a6e1d8b8ac1335190b8d7a9a3083463a1b0e4ef5faba27fe58cbb`。

Windows Release 的控制器、Core 安全、运行时注册 24 项及模块边界专项通过。MB-05 跨进程回归从原 operationId `b5e6d001-d454-451e-8271-e09a51e74db6` 读回 Interrupted，受控副作用计数为一。证书 HTTP 原回执排空 `adc9d7ff-3e0c-41a7-a91c-8a4899999282` 通过。

Linux 真实受管 Mihomo 在私有 systemd 中通过 177 项。连续两次独立状态查询发出两次 `/version`。有活动连接的概览只发出一次 `/version`、读取一次 Core 恢复状态，解析器唤醒后的请求重新采样。给夹具内控制器客户端使用临时错误凭据时，真实 Mihomo 返回一次 401，运行时仍为 Running，管理健康报告认证失败，代理转发及发布保持原状态。原发布排空 `13da3374-a827-4bc4-953b-810a43e3b68b`、原失败回执 `f0d32f11-fbca-4501-b13e-7cd0c563c2d8` 及原中断补偿 `57c17fd1-15ad-47b8-8fff-b05598a01e0b` 同批通过。日志为 `.artifacts/mb04-overview-observation-native-disk.log`，归档 SHA-256 为 `f93fb8366b7f432c047c045a186d8dd0249efb63bf0c3a8c9f2e70b0d20c13a7`。

首轮外层脚本混用了两套验收的通过标记。拆开后，受管专项先遇到临时文件系统上的归档前置检查失败，随后遇到长临时路径超过 Unix 套接字限制。只在私有挂载命名空间内为 `/tmp` 绑定独立磁盘目录后复测通过。本阶段只确认请求执行图及网络安全连续性。下一轮驻留复测应使用已核验的受管 Mihomo 和相同活跃连接负载。Proxy 整体动态休眠与生产 PSS 验收继续开放。

## MB-04 受管 Mihomo 概览的配对驻留测量

同一私有 Linux systemd 内复用受管 Mihomo PID `9842`、独立生产 Helper、PAM 和 512 条已到达源站的代理连接。基线和候选各启动三次非 root Release Server，第二对反转顺序。每次预热后读取 24 组概览及详情，随后闲置 35 秒采五点并再次核对连接。各次概览均返回 `Managed / Running / IntegrityVerified`。全部原连接收到源站响应，受管修订、发布身份及原 `16ea012a-0af1-456d-8403-5e08a2e34374` Interrupted 回执保持一致。测量期间 Helper 未收到配置或补偿请求。真实受管套件 177 项通过，专属 cgroup 已清理，宿主共享服务及代理文件指纹一致。

PSS 为 Server PID 的 MiB 值。峰值增量为请求窗口采样峰值减去预热单点，闲置值为 35 秒后五点中位数。延迟为每组概览与详情的客户端中位数，单位 ms。

| 轮次 | 基线 / 候选 PSS 峰值增量 | 基线 / 候选 35 秒 PSS | 基线 / 候选延迟 |
| --- | ---: | ---: | ---: |
| 1 | 22.31 / 20.58 | 196.77 / 194.82 | 322.87 / 320.18 |
| 2 | 21.29 / 21.03 | 195.13 / 194.94 | 307.32 / 326.64 |
| 3 | 16.86 / 15.22 | 195.59 / 190.60 | 314.22 / 310.05 |

候选的闲置 PSS 在三对中低 0.19–4.99 MiB，峰值增量低 0.26–1.73 MiB。差值波动较大，延迟未一致改善。采样范围限管理 Server PID，Helper、Mihomo 和客户端均不计入。未强制 GC。该结果只描述隔离负载，Proxy 整体继续 resident，生产环境与动态休眠未验收。下一项沿概览与详情 HTTP 的请求边界验证从控制器读取到响应、请求清理的工作责任，再考虑扩大可回收执行范围。

基线源码为 `26b050cd`，候选为 `11daf211`。使用各次提交前已发布的测试制品，程序集信息版本分别记录其父提交。Server DLL SHA-256 分别为 `12c8fc0a827f94acad0b3604b576d2f60aee8f8a96fd98ccdce514091803ae0b` 和 `558124c4171f8bcc1a7d683b896cea1f8045fc4dbf7fff46b117e6a2a7d38bea`。原始样本见 `.artifacts/mb04-managed-overview-paired-live.log`，归档 SHA-256 为 `16947bfd658254594498e8b50fb09bd8f6c2fee05818ac2f95254d2857023d1d`，配对摘要为 `.artifacts/mb04-managed-overview-paired-summary.json`。Windows Release 注册 24 项和模块边界回归通过，Docker 跨进程夹具按原 ID `e693812c-5443-4fd4-808b-0175a930a748` 返回 Interrupted，副作用计数为一。

## MB-04 Mihomo 概览和连接详情的 HTTP 工作责任

两个认证 GET 在解析读取服务前从原驻留协调器取得工作租约，按请求及宿主停止信号取消控制器操作。租约覆盖实际读取、响应写入及完成和独立读取作用域的异步释放。客户端断开只发出取消信号，清理确认后归还租约；清理失败保留租约并让原协调器记录恢复阻塞。只读查询没有待提交的操作台账，Core 继续负责系统代理、TUN、授权到期和发布恢复。

Windows Release 使用实际 Kestrel 和驻留协调器。可控控制器传输分别挂起读取和响应启动，断线后等待实际任务结束及异步 scope 释放。匿名请求未创建读取图。另注入清理失败，检查原工作计数和恢复阻塞仍在。模块边界同批通过，MB-05 跨进程夹具按原 ID `b8a51393-3f2a-4f61-98fd-720208df2137` 返回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-observation-lease-final-win.log`。

Linux 私有 systemd 下，完整 PAM/JWT Server 经回环中继读取真实 Mihomo。中继收到包含原活跃连接 ID 的原生详情后暂缓响应，模块目录显示一份活动租约。客户端实际断线后，中继观察到控制器请求取消，模块目录最终回到零。认证重读仍观察到同一连接和运行 PID，发布修订一致，原连接完成真实转发。同批概览、Server 重启及解析代次卸载与唤醒通过。受控响应写入与异步清理故障由 Windows 专项单独验证。

原入站发布 `0ea3e69f-92f3-46ee-a796-bb21744e3bf3`、补偿 `3a661d97-e365-4279-b762-1f6b70f1a998`、授权到期 `736faad2-3208-4e6f-9ee7-5b513e987a3e` 和 TUN 恢复 `f902de54-8e11-4401-819e-77a682262402` 同批通过。专属 cgroup 清理完成，宿主共享服务及代理文件指纹一致。日志为 `.artifacts/mb04-observation-lease-final-live.log`，归档 SHA-256 为 `7fe39a883b528bb9ae030c9bf7a1a8bdfef6c61744c67a9aadb08512f836f52c`。Windows Core 安全及控制器专项也通过。本轮未扩大可回收代码范围，Proxy 保持 resident，整模块动态休眠及生产 PSS 仍开放。

## MB-04 Mihomo 连接计数与详情投影随代次释放

连接记录的 ID 筛选、计数、端点格式化和详情投影迁入已有可回收 Proxy 执行程序集。计数请求只读取计数，详情请求从共享值构建原协议 DTO。JSON 记录在代次内解析并释放。Host 仍负责控制器鉴权、流和管道的完成、错误映射与 HTTP 工作租约。投影代次一直持有到流清理结束，现有 30 秒门禁和代次预算继续适用。

Windows Release 控制器专项验证分片、上限、坏数据、取消及超时，也核对 IPv6 源地址、主机名、规则、链和开始时间的投影。Host 保留共享投影值后旧代次仍可回收，新计数唤醒新代次，满预算继续使用当前代次。Windows 模块边界回归通过，MB-05 跨进程按原 ID `4d1e4b9f-e502-4c13-bfef-6a7a32a3707d` 返回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-projection-win.log` 和 `.artifacts/mb04-projection-boundaries-win.log`。

Proxy 程序集边界守卫通过 16 项。完整 PAM/JWT Server 在 Linux 私有 systemd 中重启后继续读取同一真实 Mihomo 连接，解析代次卸载与唤醒时原连接仍可转发。截留真实控制器响应并取消请求后，原租约正常归还。原发布 `9dfa7fdd-11f4-4ae9-8df0-2691406377c1`、补偿 `01a0d9f9-c3c6-4c91-a5ee-5730b9f04eda`、授权到期 `c0fd364e-f038-44b4-969b-bc3faa05f69b` 和 TUN 恢复 `b1afdb4d-fc42-450e-a0cc-f89edd2b0448` 同批通过。专属 cgroup 清理完成，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-projection-auth-live.log`，归档 SHA-256 为 `d438c52e688dda5c20a784799e5859d25724f6a9de69481618319f1cbafe28c2`。

真实受管 Mihomo 套件通过 177 项。Host 保留真实详情 DTO，代次自然闲置卸载后通过测试 GC 与弱引用确认回收。之后的原生转发及新代次读取成功。原发布排空 `8203808c-3e16-42f2-b4fa-8a612542ebb9`、失败回执 `642e94c0-ccfe-4d37-8118-d2715c17192c` 及原中断补偿 `29449349-a407-4e4f-972e-1a9c7f69ebc2` 同批通过，补偿一次且未重发配置。专属 cgroup 清理完成，宿主服务及代理文件指纹一致。日志为 `.artifacts/mb04-projection-native-live.log`，归档 SHA-256 为 `47cae6ce81f171ad657aecf0509f772b7ce49d777824ee51177b19cace89085a`。

本轮证据覆盖读取代码回收与网络连续性，整模块动态休眠和生产内存仍开放。下一项验证计数路径能否省去逐条详情 JSON 对象，同时保留完整快照验证，再用同一真实连接负载配对测量。

## MB-04 Mihomo 概览按 JSON token 计数

概览计数在现有可回收代次中扫描 JSON token，只解码每条记录最后一个顶层 `id`。详情继续使用原记录投影。计数仍检查完整快照，跨片段重读时只在记录完整且合规后累加。保持 2,000 条计数范围、记录与响应大小上限，以及取消、超时和错误映射。Windows Release 控制器专项验证跨片段、重复和转义字段、嵌套与数组 ID、大小限制及截断。模块边界回归通过，MB-05 跨进程原 `63940d23-7544-4e4d-9308-3aa380608732` Interrupted 回执的受控副作用计数为一。

本地 Release 分配探针预热 200 次后，对 512 条记录的固定内存快照同步计数 1,000 次。线程托管分配由每快照 78,096 字节降至 24,848 字节。该数据只覆盖受控解析调用。日志为 `.artifacts/mb04-token-count-alloc.log`。

Linux 私有 systemd 复用同一受管 Mihomo PID `9841`、Helper、私有台账及 512 条真实连接，执行六轮完整 PAM/JWT Server 测量。两组发布树逐文件比对，只有 Proxy 执行 DLL 不同。基线及候选 Proxy DLL SHA-256 分别为 `d112f66fc955462456dadc6bf7006c358991c425cc333131035b3aaf78f5f4f9`、`5f4d714f506e974a4d5f4412dd2c8739b97abeddeddcf4f9cf7ad6c77115e251`。Server DLL 共用 `6f7e08f01eaee863aff6152f35fb0b0d41bd4eb509bd9f4247e3321d868c2108`。

| 轮次 | 基线 / 候选 PSS 峰值增量（MiB） | 基线 / 候选 35 秒 PSS（MiB） | 基线 / 候选延迟（ms） |
| --- | ---: | ---: | ---: |
| 1 | 23.92 / 21.15 | 197.42 / 195.54 | 348.83 / 322.75 |
| 2 | 21.08 / 20.95 | 195.37 / 195.11 | 316.66 / 306.39 |
| 3 | 21.21 / 21.53 | 195.03 / 194.98 | 307.63 / 313.14 |

候选 35 秒 PSS 在三对中低 0.05–1.88 MiB，峰值增量和请求延迟均无一致改善。采样只覆盖 Server PID，延迟为概览加详情的顺序请求中位数。全部原连接收到响应，原 `64672698-7947-45e9-aa9b-d08914ea71b2` Interrupted 回执及发布修订保持一致。测量期间无配置或补偿派发。真实受管套件通过 177 项，专属 cgroup 已清理，宿主共享服务及代理文件通过核验。原始日志为 `.artifacts/mb04-token-count-live.log`，归档 SHA-256 为 `f18dd56fde63fab8e5c5374a7cc05055ea89c60eb9662b43c01193c4e8031cc3`，摘要为 `.artifacts/mb04-token-count-summary.json`。

Proxy 继续 resident，整模块动态休眠及生产内存仍开放。下一项复验完整 Server 在真实 Helper 原回执等待期间收到 SIGTERM 时，Core 是否继续承担发布恢复与排空。

## MB-04 读取代次闲时卸载与原发布恢复

在同一完整认证 Server 中，专属 Unix 中继暂扣生产 Helper 对原发布的实际查询响应。通过真实 Mihomo 建立到测试源的 HTTP 连接，源站先保留响应。认证详情路由在 Core 持有原 pending 时读取该连接，随后观察解析代次的实际卸载事件。再次查询加载不同代次，读到同一连接 ID，原发布仍 pending，Mihomo PID 保持。Server 收到 SIGTERM 后先关闭监听，继续等原 Helper 响应。放行后原发布收尾并正常退出，原连接收到源站正文。重启核对同一持久回执、配置身份和修订，恢复期间只查询原 Helper 回执一次。

Windows Release 的控制器流和连接专项、31 项 Proxy 驻留专项及模块边界专项通过。MB-05 跨进程夹具按原 `fd100c77-d236-41ac-9987-ef84d6efc4b5` 读回 Interrupted，受控副作用计数为一。首轮 Linux 已通过该联合场景，后续订阅夹具重复读取一次性 PAM 输入导致整套失败，修正为测试进程内复用一次读取的输入。

最终 Linux Release 原发布 `1019960f-134b-4fe7-b479-736f86d89269` 和原连接 `5df3369e-17a5-494e-a80a-d2ca8b634f3b` 联验通过。Helper 恢复阶段无配置或补偿派发，Mihomo systemd 套件共 177 项通过，包括活动订阅的成功、断线和失败恢复。专属 cgroup 清理，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-parser-recovery/live.log`，归档 SHA-256 为 `36631e5edb98d8568562843908edab26d8f4308c94af341605e2542f19afee0d`。本轮 Linux 直接观测卸载事件和新代次，Proxy 模块整体仍 resident，整模块动态休眠和生产驻留指标保持开放。

## MB-04 Mihomo 连接详情读取执行图

依据概览与运行时基线，连接详情路由直接从原工作作用域取得 Mihomo 控制器。该路径无需实例化管理引擎、配置验证器或引擎注册表。工作租约覆盖控制器任务、实际 HTTP 响应和作用域清理，原控制器认证、错误分类及可回收解析代次继续负责各自工作。Windows Release 使用实际 Kestrel，配置构造即失败的引擎注册表来验证连接路由的依赖边界，并覆盖控制器认证错误返回 HTTP 503、客户端断线、响应排空和清理失败。

Linux 私有 systemd 使用同一批 512 条真实连接、三对交错制品和每轮 24 次概览加详情请求。候选的峰值 PSS 在三对中高 0.10、2.27、6.99 MiB，35 秒闲置 PSS 差分别为 −1.65、−3.37、+0.40 MiB，延迟变化方向不一致。采样限管理 Server PID，原 512 条连接均收到源站响应，原回执 `1bb7d583-4ddb-4d0d-a6e1-41459fcd280b`、原生 PID 和发布修订保持，测量期间配置与补偿派发均为零。保留有确定证据的执行边界调整，隔离数据未显示稳定的内存下降。

同批原发布 `ed2602cc-e610-401e-a337-ea9df28d4e01` 在解析代次闲时卸载、唤醒及 Host 排空期间完成恢复。真实 Mihomo systemd 套件通过 177 项，专属 cgroup 清理，宿主共享服务及代理文件指纹一致。Windows Release Core 安全 37 项、驻留和生命周期作用域各 31 项通过，MB-05 跨进程按原 `9576e451-5cfb-4771-9cf7-4404bb69fbac` 返回 Interrupted，受控副作用计数为一。首轮实机夹具编译暴露一个仍调用旧引擎读取方法的测试，更新后完整运行。日志及摘要在 `.artifacts/mb04-connection-read-graph/`，Linux 归档 SHA-256 为 `ff2b275528460a3cba6904d051c14e33b5ed25e123decd1396a55cc1d7e14a8e`。Proxy 整体继续 resident。下一项审计流量与日志读取的工作租约和控制器响应生命周期。

## MB-04 Mihomo 流量与日志读取租约

流量和日志路由复用连接详情的工作租约与实际响应作用域。流量按原顺序采集控制器流量与内存样本。日志保留控制器采样、脱敏、本地诊断合并及条数限制。两条路由从作用域直接取得读取依赖。Windows Release 的 Kestrel 夹具在控制器请求和实际 HTTP 响应两个位置分别注入客户端断线，核对原任务及异步作用域清理结束前租约持续，匿名请求无需构造读取执行图。

Linux 私有 systemd 的完整 Server 在 Core 等待原发布 `0699bc25-8445-49d4-ace6-70052ed23b47` 时取得真实 Mihomo 日志，并保留原流量错误回执。原连接 `e04036b8-a270-4058-9f9c-1a200d042c66` 跨解析代次与 Server 排空收到源站响应。首轮流量成功断言失败，复核实际字段确认 1.19.10 的 `/traffic` 只有速率，1.19.30 同时带累计量。原适配器要求累计量，因此旧版返回 `proxy.controller_response_invalid`。两版 `/connections` 均提供累计量及内存字段，后续按共同数据源设计有界读取。Windows 的受控完整流量样本和日志诊断回退通过，旧版真实流量完整指标仍开放。

最终 Linux 真实 Mihomo 套件通过 177 项，原发布和订阅恢复未重放，专属 cgroup 已清理，宿主共享服务与代理文件指纹一致。Windows Release 模块边界、控制器和 Core 安全专项通过，MB-05 原 `d25a004b-3a7f-42bc-9c3c-ff8ecdab9e97` 回执为 Interrupted，受控副作用计数为一。日志和实机字段探测位于 `.artifacts/mb04-traffic-log-drain/`，Linux 归档 SHA-256 为 `f80e488513d80ffb7910ee646f63237e735ae1d78f4bac6ebee32119e5a0ff9c`。Proxy 继续 resident，生产内存未测。

## MB-04 Mihomo 流量累计量使用有界连接快照

流量路由从两版共有的 `/traffic` 字段读取上传与下载速率，从同一份 `/connections` 快照读取累计上传、累计下载和内存。统计分支复用已有可回收执行代次和流清理，校验快照结构、字段唯一性、非负整数、32 MiB 响应及 256 KiB 连接记录上限。它扫描连接数组而不解码 ID、创建连接详情或构造记录 DOM。原控制器身份校验、HTTP 回执和工作租约覆盖两个请求及作用域清理。流量轮询期间也会使用该解析代次。

Windows Release 控制器专项覆盖分片传输的 2,001 条连接、内存字段位于数组之后，以及字段缺失、重复、负数和响应截断。模块边界、Core 安全 37 项、驻留及生命周期作用域各 31 项通过。Core 安全首轮受默认临时目录的文件移动权限阻塞，切换到工作区内的测试目录后复验。MB-05 跨进程按原 `b2bfeebb-8cd4-4a4e-88c7-b3e6a427043b` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 实机 178 项通过。Mihomo 1.19.10 在完整 Server 等待发布 `eb57a883-ab66-432c-b6e6-7357fe3ee82d` 时取得真实日志和完整流量回执，累计上传 442 字节、下载 365 字节。原连接 `bf9d3468-a732-47af-9846-9d2ab0710c0f` 跨解析代次及 SIGTERM 排空收到源站响应。重启保留原 Failed 回执，无重复发布或补偿。1.19.30 使用同一读取路径取得真实流量，累计上传及下载各 73 字节。两版原生内存样本均为 0，首轮错误地要求正值，复验接受原生非负值并继续要求实际流量。订阅崩溃、响应丢失、补偿同批通过。专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。

日志位于 `.artifacts/mb04-traffic-statistics/`，Linux 归档 SHA-256 为 `e6d5bcd9e028903e3942db5c5044c06e8bbe2dfe7db219d4c4a221bb7cea31d7`。流量轮询仍需扫描有界快照。Proxy 整体继续 resident，生产 PSS、整模块动态休眠和统计轮询在高连接数下的资源成本仍待验收。下一项优先验证连接详情按需解码，并沿用 512 条真实连接负载配对测量。

## MB-04 Mihomo 连接详情按需解码评估

候选在现有可回收代次内保留所需属性的位置，完整校验记录后解码最后匹配的属性，省去逐记录 JSON DOM。交付保留原有有界详情投影和字段回归。候选源文件、受控测量及原始样本保存在 `.artifacts/mb04-token-details/`。

Windows Release 控制器专项通过。分片回归覆盖重复和转义字段、IPv6 回退、数组字段和最终元数据类型错误。固定 512 条记录的受控分配测量使用相同输入、两组交错和每次 1,000 份快照。旧投影每份分配 524,664 字节，候选每份 213,368 字节。该数据限当前解析线程的托管分配。

Windows Release 模块边界、Core 安全 37 项、驻留及生命周期作用域各 31 项在候选上通过。模块边界首轮在受限环境无法连接 Kestrel 回环端口，开放测试所需的本地回环后通过。保留生产解析器后复跑控制器及模块边界通过，MB-05 跨进程按原 `8ecf44f0-d7a6-4c21-a4d2-78d8a32712c1` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 用同一 Host 制品，只替换 Proxy 解析程序集。三对交错运行各执行 24 次概览加详情请求，保持相同 512 条真实连接。下表为候选减基线，采样仅含管理 Server PID。全部连接收到原源站响应。原回执 `5ab8a083-eb8b-413a-9683-da1617e0b8ee`、原生 PID 和发布修订一致，测量期间配置与补偿派发均为零。

| 轮次 | 峰值 PSS 差（MiB） | 35 秒 PSS 差（MiB） | 请求中位延迟差（ms） |
| --- | ---: | ---: | ---: |
| 1 | +0.10 | −0.43 | −0.51 |
| 2 | +3.27 | +1.25 | +13.14 |
| 3 | +0.12 | +0.58 | +1.83 |

候选未达到配对负载的内存目标，继续使用上阶段已验证的生产解析器。候选 Linux 原生套件报告 178 项通过，原发布 `5b369c07-88c6-4529-b085-7c8ae662c183` 跨解析代次与 SIGTERM 排空确认。专属 cgroup 清理，宿主 sing-box 和代理文件指纹一致。外层驱动误将订阅模式的标记用于配对模式，退出码为 1。订阅故障验收沿用上阶段单独套件的证据。配对归档 SHA-256 为 `28397fd000d2928180e424e12385d6694acd7ba7ac20b2ca1295db0165b215b0`。Proxy 整体保持 resident，生产 PSS 与动态休眠仍开放。

## MB-04 Mihomo 高连接数流量轮询基线

隔离 Linux 使用同一个 Release Server 和同一批 512 条真实连接进行三对交错测量。两组都先通过认证概览及详情读取确认原连接。20 秒窗口内，轮询组按桌面端两秒节拍执行十次流量请求，静置组等待相同时间。轮询回执累计量单调，三组请求中位耗时为 1.014–1.015 秒。表中为轮询减静置，PSS 只采管理 Server PID，CPU 为窗口内进程 CPU 时间。

| 轮次 | 峰值 PSS 差（MiB） | 35 秒后 PSS 差（MiB） | Server CPU 差（ms） | Mihomo CPU 差（ms） |
| --- | ---: | ---: | ---: | ---: |
| 1 | +2.39 | +1.28 | +720 | +40 |
| 2 | +4.69 | +4.28 | +1180 | +30 |
| 3 | +4.49 | +2.55 | +1160 | +30 |

512 条原连接均收到源站响应。原发布回执 `9ef35a38-4eef-46c7-b4cc-88163dd482e1`、原生 PID 和发布修订一致，期间配置和补偿派发均为零。Linux 原生套件通过 178 项，同批按原 `2e9495ee-558b-4b60-9962-d8d6e4e1195b` 验证解析代次切换和 SIGTERM 排空。专属 cgroup 已清理，宿主 sing-box 与代理文件指纹一致。Windows Release 注册及模块边界回归通过，MB-05 跨进程原 `1cc6e310-b3f8-421c-bd16-36c6783ae271` 为 Interrupted，受控副作用计数为一。

复验通过私有 systemd 内设置 `RELAXKONOS_TEST_MIHOMO_TRAFFIC_POLLING=1` 选择轮询负载。原始采样和摘要在 `.artifacts/mb04-traffic-polling/`，Linux 归档 SHA-256 为 `be3aa7ae91f1e1c6720b19fae0e96a1dfbbc6f74d8d1c355500de16bba2bffee`。结果限单认证客户端和隔离负载。多客户端、慢控制器导致的重叠轮询、生产资源及整模块动态休眠继续开放。Proxy 保持 resident。

## MB-04 桌面 Mihomo 流量轮询排空

桌面概览在进入页面后开始两秒流量轮询，通用工作区刷新不再并行发起同一路径。每个工作区只允许一个流量 HTTP Task。慢请求跨过计时周期时跳过该周期，离开概览取消原调用，重新进入仍等待原 Task 完成。已离开页面的迟到结果不会更新当前显示。Server 继续用原工作租约保护控制器任务、响应与异步清理。

Windows Release 的 Avalonia 无头回归使用实际 Dispatcher 计时和页面 attach/detach，验证慢请求、取消、迟到结果丢弃和重新进入后的下一次请求。首轮夹具只运行待处理任务，未驱动计时器，改为实际 Dispatcher 循环后通过。完整桌面回归通过 105 个页面布局及本专项。模块边界回归通过，MB-05 跨进程原 `09c5a30c-f7e7-4215-a174-7cc946c80930` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 通过真实 Mihomo 连接和完整认证 Server 联验。HTTP 中继收到并暂扣真实 `/traffic` 样本，生产模块状态接口显示一个工作租约。客户端取消后，实际控制器请求中断，清理后状态接口显示零租约，新请求取得样本。原连接 `c8ced245-b35e-4de9-b483-1a7336e7349e` 持续转发，原发布 `0d11a773-00da-4ae9-864f-f76c1cb32a69` 跨 SIGTERM 排空及重启保持原回执，发布和补偿均未重放。Linux 原生套件通过 178 项，专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。

证据位于 `.artifacts/mb04-traffic-polling-cancel/`，归档 SHA-256 为 `fa9489a66edaa9f789958013784bf1a4c1e56d7555f77f29b27c28b3c682adf5`。并发上限按桌面工作区计算，多工作区共享采样、生产内存和整模块动态休眠仍开放。下一项核对同一概览仍使用的运行时、代理组、路由和 DNS 读取，补齐实际任务和响应的工作责任。

## MB-04 Mihomo 运行时与控制面读取责任

运行时、代理组、路由及 DNS 的认证 GET 接入原 Proxy 工作租约。先准入，再在独立作用域解析读取依赖，最后等待实际响应和异步清理。运行时继续由 Core 原管理器读取。代理组、路由及 DNS 直接使用控制器，代理组顺序复用已有配置读取。原接口继续在控制器认证失败时分别返回空代理组、路由 HTTP 503 和带问题码的 DNS 回执。

Windows Release 模块边界和控制器回归通过。实际 Kestrel 夹具逐路由覆盖匿名拒绝、控制器任务阻塞及响应阻塞时断线、异步作用域清理和租约归还。MB-05 跨进程原 `264ca97b-7f91-45ed-873f-87350fa10b22` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 使用完整认证 Server 和真实 Mihomo。暂扣 `/proxies` 和两条 `/configs` 的实际响应，分别取消代理组、路由和 DNS 请求。原发布待确认时，运行时返回公开 `RuntimeStateUnknown`。原发布对账后，在重启进程中暂扣真实 `/version` 并取消运行时请求。每条请求持有一个租约，控制器连接中断、清理后归零，随后新请求成功。原发布 `4b244d12-b158-48bd-974f-1f2b39cee684` 保留原 Failed 回执，原连接 `2bebae61-8023-4e87-b5aa-2dc4d17c5708` 完成真实转发。原生套件通过 178 项，专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。

前两轮夹具分别误等候待确认状态下的 `/version`，以及误断言内部问题码。复验以公开 DTO 核对待确认状态，并把运行时控制器断线放到对账完成后。Windows 的代理组排序、TUN 34 项、Core 安全 37 项、驻留及生命周期作用域各 31 项也通过。日志在 `.artifacts/mb04-control-observation/`，Linux 归档 SHA-256 为 `267021248d17e526f2b3683449434592ef42a6e7d40cc77c8a608fe0dcf28adb`。Proxy 整体保持 resident，读取图配对资源复测和生产内存仍开放。

## MB-04 Mihomo 控制面读取配对测量

基线为 `6f735426`，候选为 `5981a0e0`，使用各自发布的完整 Server 制品。私有 Linux systemd 保持同一受管 Mihomo PID 和同一批 512 条真实连接。三对交错运行先预热，再依序请求运行时、代理组、路由和 DNS 共 24 批，逐次比较 JSON 语义，采集管理 Server PID 的 PSS、CPU 时间和每批延迟。闲置 35 秒后复查原连接集合，原发布回执与发布修订由生产 Helper 和 Server 台账共同核对。通过 `RELAXKONOS_TEST_MIHOMO_CONTROL_READS=1` 与既有配对基线变量选择该负载。

| 轮次 | 峰值 PSS 差（MiB） | 35 秒 PSS 差（MiB） | 批次中位延迟差（ms） | Server CPU 差（ms） |
| --- | ---: | ---: | ---: | ---: |
| 1 | +0.14 | −0.35 | −8.86 | +470 |
| 2 | +1.69 | +0.57 | +8.09 | +310 |
| 3 | −0.22 | −0.24 | −1.30 | −200 |

差值为候选减基线。两份制品各自编译，含不同二进制摘要；生产源码差异限四条读取路由和代理组投影，测量口径为完整 Release 制品。四条接口逐次返回相同的 JSON 语义，512 条原连接均收到源站响应。原回执 `4074de47-1761-4679-873c-27523ab2b562`、原生 PID 与发布修订一致，配置和补偿派发均为零。各项资源差值方向不一致，保留已验证的工作租约，生产内存与整模块动态休眠继续开放。

同制品复验四路取消和原发布 `1c5776cf-cb18-4031-9277-8454dfa4893a`，Linux 原生套件 178 项通过。专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。Windows Release 注册和模块边界回归通过，MB-05 跨进程原 `286c684d-ce4f-457e-ab8b-2cea434d0237` 为 Interrupted，受控副作用计数为一。原始数据、制品摘要和配对结果在 `.artifacts/mb04-control-read-paired/`，Linux 归档 SHA-256 为 `a8ce68a15d95ae48984354b9ced4ff4624dc8b8ee96599ff87b0a1afa10f57dc`。

桌面刷新当前同时请求路由和 DNS，Server 分别读取相同控制器 `/configs`。下一项验证同一配置样本能否提供两项结果、减少请求并保持授权及工作责任。

## MB-04 Mihomo 路由和 DNS 单次采样

桌面刷新改为认证 `GET /api/v1.0/proxy/controller-config`。Server 在一个工作租约内读取并解码一份 Mihomo `/configs`，返回路由和 DNS 两项结果及各自的问题码。路由字段无效时仍返回有效的 DNS 投影，桌面保留原路由显示。合并接口接替原有两条 GET，路由写入继续使用原 PUT。

Linux 私有 systemd 通过完整认证 Server 和真实 Mihomo 核对请求计数。合并读取、取消和随后新请求各触发一次 `/configs`，控制器清理后原租约归零。原发布 `0c93c1a2-5128-4766-b6f1-2a76dd473d37` 跨 SIGTERM 及重启保留原回执，原连接 `aed2c9a6-9fee-4094-9024-21e3ec04c20c` 完成转发，发布与补偿未重放。原生套件通过 178 项，专属 cgroup 清理，宿主 sing-box 和代理文件指纹一致。日志位于 `.artifacts/mb04-single-config/`，归档 SHA-256 为 `0de8c26494f62fb18703c721391f0728d4b956260e3571272d2e1e86f93210bc`。

Windows Release 模块边界、控制器和桌面布局回归通过。Kestrel 夹具覆盖一次采样、认证失败及无效路由字段下的独立 DNS 投影。MB-05 跨进程原 `289f23ad-2b74-410a-bf43-684ddace71f5` 返回 Interrupted，受控副作用计数为一。Android 代理 HTTP、仓储和 ViewModel 共 46 项通过，设备测试源码已编译。Android 首轮编译发现未决路由写入的核对仍调用旧读取，更新后复验确认 DNS 成功不能解除未知路由标记。Android 工作流见[应用文档](../../../Client/RelaxKonOS.Client.Android/docs/features/Proxy.md)。

此项验证实际请求数从两次降为一次。Proxy 保持 resident，配对内存及整模块动态休眠仍开放。

## MB-04 Mihomo 配置合并读取配对测量

基线 `5981a0e0` 的桌面负载并发读取路由与 DNS，候选 `0fdbb4ca` 从合并接口读取。两份完整 Linux Release 制品在私有 systemd 中完成三对交错运行，每轮 24 批读取运行时、代理组、路由和 DNS。每批配置 HTTP 请求由两次降为一次，四项 JSON 语义逐批相等。使用 `RELAXKONOS_TEST_MIHOMO_CONTROL_READS=1`、`RELAXKONOS_TEST_MIHOMO_COMPARE_SPLIT_CONFIG=1` 和 `RELAXKONOS_TEST_MIHOMO_OVERVIEW_BASELINE` 选取旧制品负载。

| 轮次 | 读取期间峰值 PSS 差（MiB） | 35 秒 PSS 差（MiB） | 批次中位延迟差（ms） | Server CPU 差（ms） |
| --- | ---: | ---: | ---: | ---: |
| 1 | +0.39 | −5.61 | −0.83 | +570 |
| 2 | −0.34 | −2.43 | +4.15 | +90 |
| 3 | +1.37 | −1.13 | +12.12 | +300 |

差值为候选减基线。三轮 35 秒 PSS 均较低，读取期间的峰值方向不一，Server CPU 三轮均增加。结果限本次隔离负载，Proxy 整体继续 resident，生产内存和动态休眠开放。原生 PID 9830 与原回执 `ea2bf217-a5a4-4d2a-a045-7f3285587632` 保持，配置及补偿派发均为零，512 条原连接均收到源站响应。

同制品复验 `/configs` 单次请求、取消排空及原发布 `a06ae54c-70b0-4c64-b066-566b702a3e1e` 的 SIGTERM 与重启恢复。Linux 原生套件通过 178 项，专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。归档 SHA-256 为 `9ef3033da678a4a40977d5c82f413d9831e62e00bafda5d5eadac8c22d3bebbe`，原始样本与两份制品摘要见 `.artifacts/mb04-single-config-paired/`。

Windows Release 模块边界与 Mihomo 注册专项通过。MB-05 跨进程按原 `23bf6ea0-6b10-4486-a7f0-393c53236d0a` 返回 Interrupted，受控副作用计数为一。ManagedRuntimes 广跑停在伪造制品布局断言，父提交发布制品的 `runtime-lifecycle-only` 复现同一失败。

## MB-04 Mihomo 重叠流量采样

依据 512 连接轮询基线，同一 Server 内通过认证的重叠流量请求共享一次 `/traffic` 样本、一次连接快照读取和一个控制器作用域。采样拥有独立工作租约，清理失败沿原驻留协调器保留恢复责任。各请求分别持有响应租约。单个客户端取消后其余读者仍可接收结果，最后一个客户端取消后，中止采样并等待实际任务和清理。新请求等待该采样排空后读取新样本。共享范围为在途请求，完成后无样本缓存和后台轮询。

Windows Release 模块边界回归验证 Kestrel 的匿名拒绝、重叠请求、最后一个客户端取消、异步清理期间的新读者等待及响应排空。MB-05 跨进程按原 `bb848743-bdbb-4130-8aa7-2344c239620f` 读回 Interrupted，受控副作用计数为一。首轮 Windows 沙箱拒绝 loopback 连接，使用允许本地监听的执行权限复验通过。

隔离 Linux 由完整 PAM/JWT Server 通过真实 Mihomo 验证。首个客户端取消时，另一客户端取得原采样，实际 `/traffic` 与 `/connections` 各调用一次。最后一个客户端取消时，控制器请求断开，工作租约归零后新请求成功。原发布 `ee01ad12-71b0-468d-93ed-86101d4c8fd9` 跨 SIGTERM 按原 ID 读取 Failed，原连接 `30cef037-cfed-4cfb-a0f2-dbaef93a37f7` 跨解析代次完成真实转发。原生套件 178 项通过，专属 cgroup 已清理，宿主运行时和代理文件指纹一致。日志在 `.artifacts/mb04-shared-traffic/`，原生验收归档 SHA-256 为 `0c3d4663bbb13d439f2aac0a1feff3ab96baacbbd4321d34864b7e0e1028b3a5`。

同机配对保持 Mihomo PID 9855 和同一批 512 条真实连接，基线与候选各运行三次，第二对反转顺序。每轮四个重叠认证请求组成一批，按两秒节拍执行十批。真实控制器中继在三对均记录每轮 `/traffic` 和 `/connections` 各由 40 次降为 10 次，候选每批四个回执相等。512 条原连接均收到源站响应。原回执 `a21dbf06-6f4e-4195-ae66-ccdb9d9c64e3`、原生修订和发布身份一致，测量期间没有配置或补偿派发。

| 轮次 | Server 峰值 PSS 差（MiB） | Server 35 秒 PSS 差（MiB） | 批次中位延迟差（ms） | Server CPU 差（ms） | Mihomo CPU 差（ms） |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | −6.72 | −5.88 | −10.75 | −1230 | −110 |
| 2 | −3.85 | −4.56 | −5.81 | −530 | −60 |
| 3 | −4.88 | −4.21 | −7.17 | −950 | −90 |

差值为候选减基线。基线为 `2837ebcd`，两份发布树仅替换 Server DLL，使用同一个 Proxy 解析 DLL 和 Helper。以 `RELAXKONOS_TEST_MIHOMO_SHARED_TRAFFIC=1` 和 `RELAXKONOS_TEST_MIHOMO_OVERVIEW_BASELINE` 选择该测试。隔离归档 SHA-256 为 `925b64534f3a9c2f9d5879dc502dace107ce850ebbbe907dacda8e1f1f59f154`。六轮数据、制品摘要和复核结果见 `.artifacts/mb04-shared-traffic/{paired.log,summary.json}`。本轮测量限重叠请求和隔离负载，生产资源、Windows 原生控制器断线与 Proxy 整体动态休眠继续开放。

## MB-04 Mihomo 重叠概览采样

认证通过的重叠概览请求共用一次在途采样。活动档案、原 Core 恢复状态、受管运行时、控制器版本、TUN 和连接计数在独立采样作用域读取。采样租约覆盖实际任务与异步清理，每个 HTTP 请求仍持有自己的响应租约。最后一个读者离开时才取消原采样，新请求等待排空后重新读取。完成的采样不进入缓存。独立运行时接口和现有安全 worker 沿用各自责任。

Linux 私有 systemd 以 `2f8b2bc1` 的完整 Release 制品为基线，对本阶段制品完成三对交错运行。每轮使用同一个受管 Mihomo PID 和 512 条真实连接，发送 24 批四路并发概览。每批四份 JSON 与基线语义相同，版本探测和连接扫描各从每轮 96 次降为 24 次。所有原连接均收到源站应答，受管修订、原 PID 与原 `53dcea0a-c79d-4622-a33b-a08b2f0046f7` 回执保持，配置及补偿派发均为零。以 `RELAXKONOS_TEST_MIHOMO_OVERVIEW_BURST=1` 和既有基线变量选择负载。

| 轮次 | PSS 峰值增量（MiB，基线 / 候选） | 35 秒 PSS（MiB，基线 / 候选） | 批次中位延迟（ms，基线 / 候选） |
| --- | ---: | ---: | ---: |
| 1 | 35.69 / 23.08 | 213.93 / 199.40 | 1147.02 / 379.78 |
| 2 | 30.52 / 23.59 | 211.81 / 199.65 | 1420.91 / 311.83 |
| 3 | 29.04 / 22.22 | 210.10 / 197.16 | 1569.17 / 329.31 |

峰值增量相对各自预热 PSS，35 秒 PSS 为五点中位数。候选闲置 PSS 低 12.16–14.53 MiB，峰值增量低 6.82–12.61 MiB，Server CPU 每轮低 1.12–1.85 秒。记录限本次四路并发隔离负载。

真实控制器在原发布 `714ffe59-4a7c-44e1-9622-457a4b4fdcee` 仍待核对时暂扣连接快照。两个读者共用一次版本探测和连接扫描，取消其中一个后另一个取得原样本。取消最后一个读者时原任务与响应排空，后续请求成功。原生套件 178 项通过，专属 cgroup 已清理，宿主运行时与代理文件指纹一致。原始样本、两份制品摘要和汇总为 `.artifacts/mb04-overview-burst/linux.log` 与 `.artifacts/mb04-overview-burst/summary.json`，归档 SHA-256 为 `ee1a18ab0064ed1e89e4d78ce3e299cadf543f12d1c4b5b25a2d21ce06563d37`。

Windows Release 控制器、Core 安全、ManagedRuntimes 和模块边界回归通过。Kestrel 夹具核对匿名拒绝、单读者与重叠取消、响应及作用域清理。MB-05 跨进程夹具按原 `214c07a3-6300-43f0-bbd1-867bc3b1d82b` 返回 Interrupted，受控副作用计数为一。可信下载探针因缺少进程级测试 CA 覆盖跳过。

Proxy 整体继续 resident。配对负载只涵盖隔离测试机的 Server PID，生产内存与整模块休眠继续开放。

## MB-04 Mihomo 跨路由运行时采样

概览与独立运行时 GET 共用一次在途受管运行时判定。采样在原运行时锁内取得公开 DTO，以及判定实际读取的控制器可达性。概览在已有可达性时直接复用，待核验等跳过版本探测的状态只由概览按需读取健康。概览仍负责档案、恢复状态、TUN 和连接计数，独立运行时 GET 只返回运行时 DTO。已完成样本随读取作用域排空。

共享运行时采样、概览采样与两条 HTTP 响应各持有原工作租约。取消一路只释放该路的参与，最后一个读者离开时才取消实际运行时任务。原发布、到期、TUN 和 guard 继续由原所有者处理。

Linux 私有 systemd 用 `b055b481` 和本阶段完整 Release 制品完成三对交错运行。每轮 24 批，每批同时请求一条概览和三条独立运行时状态。两条 API 的响应语义一致，版本探测每轮从 96 次降至 24 次，连接扫描维持 24 次。受管 Mihomo PID `9837`、发布修订和原 `01f77a7f-cec0-40fc-87fd-c081cc2a1b9c` 回执保持，512 条原连接均收到源站应答。配置及补偿派发为零。使用 `RELAXKONOS_TEST_MIHOMO_OVERVIEW_BURST=1`、`RELAXKONOS_TEST_MIHOMO_MIXED_RUNTIME_READS=1` 和基线变量复现负载。

| 轮次 | PSS 峰值增量（MiB，基线 / 候选） | 35 秒 PSS（MiB，基线 / 候选） | 批次中位延迟（ms，基线 / 候选） |
| --- | ---: | ---: | ---: |
| 1 | 26.14 / 32.04 | 203.85 / 200.81 | 1040.81 / 526.66 |
| 2 | 24.17 / 22.91 | 202.26 / 197.09 | 1000.13 / 461.53 |
| 3 | 24.13 / 22.25 | 201.69 / 197.95 | 952.24 / 383.98 |

候选的 35 秒 PSS 低 3.04–5.17 MiB，Server CPU 每轮低 0.85–1.20 秒。峰值增量一轮高 5.90 MiB，两轮低 1.26–1.88 MiB。峰值相对各自预热值，闲置取五点中位数。

原发布 `ddf4061e-e24e-4d6f-8d55-0dc3f669aa47` 仍待确认时，独立运行时读取返回 RuntimeStateUnknown，版本探测为零。原回执排空并重启后，暂扣真实 `/version`，取消独立运行时读者。概览继续完成同一次采样并读取一次连接快照。随后最后一个运行时读者取消、作用域排空与新请求通过。原发布和 SIGTERM 复验使用同一回执和原生进程。Linux 原生套件 178 项通过，专属 cgroup 已清理，宿主共享运行时和代理文件指纹一致。日志与摘要在 `.artifacts/mb04-mixed-runtime/`，归档 SHA-256 为 `a0f84b665d90e888e4f79fd82183b3fcba1db337901957f7d389a190d4204487`。

Windows Release 控制器、Core 安全、ManagedRuntimes 及模块边界回归通过。Kestrel 夹具覆盖匿名、跨路由取消、响应和作用域清理。MB-05 跨进程夹具按原 `39352789-34a9-4497-976b-bae3caf4520c` 返回 Interrupted，受控副作用计数为一。可信下载探针因缺少进程级测试 CA 覆盖跳过。

Proxy 继续 resident。数据只覆盖隔离负载，生产驻留与跨休眠交接仍开放。

## MB-04 Mihomo 重叠连接详情采样

512 条真实连接的基线已覆盖流式连接详情与概览/运行时轮询。本阶段合并重叠的认证详情 GET。在途采样拥有一次控制器请求、详情投影与异步作用域清理，各 HTTP 请求保留独立响应租约。单个读者取消不影响其余读者。最后一个读者取消后，新请求等待实际采样与清理结束，再读取新快照。结果只在同一次在途采样内共享。概览仍只取计数，流量仍独立读取统计字段。

Windows Release 验证了实际 Kestrel 的认证准入、控制器错误 HTTP 503、请求断线、响应及清理。注入采样作用域清理失败时，HTTP 返回 500，原驻留协调器保留一个工作租约和恢复阻塞。控制器、Core 安全、驻留和模块边界回归通过。MB-05 夹具用原 `6341146d-c7c6-4494-8fb0-4c5c0a299647` 跨进程读回 Interrupted，受控副作用计数为一。日志在 `.artifacts/mb04-connection-burst/`。

私有 Linux systemd 的配对复测使用 `82936786` 与本阶段改动，两份完整 Server 均用 Release `linux-x64 --self-contained false` 发布，运行配置和依赖清单一致。首轮对照使用了自包含制品，资源数据排除。复测以 `RELAXKONOS_TEST_MIHOMO_CONNECTION_BURST=1` 和 `RELAXKONOS_TEST_MIHOMO_OVERVIEW_BASELINE` 启用，每轮在同一批 512 条真实连接上发送 24 批、每批四条认证详情 GET，比较全部公开详情字段和原连接 ID。

| 轮次 | 控制器 `/connections` 次数（基线 / 候选） | PSS 峰值增量（MiB，基线 / 候选） | 35 秒后 PSS（MiB，基线 / 候选） | 批次中位延迟（ms，基线 / 候选） |
| --- | ---: | ---: | ---: | ---: |
| 1 | 96 / 24 | 31.10 / 0.96 | 217.79 / 185.97 | 36.02 / 14.89 |
| 2 | 96 / 24 | 30.42 / 0.57 | 215.02 / 185.98 | 28.28 / 15.78 |
| 3 | 96 / 24 | 30.98 / 0.92 | 215.95 / 186.15 | 31.19 / 15.15 |

PSS 仅采样管理 Server PID，无强制 GC。受管 Mihomo PID `9843`、发布修订和原 `77e73d8b-88cd-4e1b-98f0-422613ab0157` 回执跨六轮保持，零配置或补偿派发，512 条原连接均收到源站响应。

原生取消联验中，首个读者断线后，另一读者从同一次实际控制器读取取得原连接 `2868ecac-de2e-423f-abc4-3e7fb395b3f2`。最后一个读者断线触发控制器断开并排空租约，下一次请求完成一次新读取。原发布 `c6ebf3b1-05ed-4cda-a141-627ba5039d58` 在恢复期间保持可追踪，原连接跨解析代次闲时卸载、唤醒及 Server 停机排空后收到真实响应。Linux 原生套件 178 项通过，专属 cgroup 已清理，宿主运行时和代理文件指纹一致。证据为 `.artifacts/mb04-connection-burst/linux-matched.log`，归档 SHA-256 为 `7a2e06675560a09f0a889425006f9212710363234ebf1c7fceac19a86258a216`。

Proxy 保持 resident。系统代理、TUN、授权到期和发布补偿仍由原 Core owner 负责。详情解析代次的闲时释放继续独立于外部运行时。生产内存和整个模块动态休眠仍待验收。

## MB-04 活跃具名 TUN 的管理面配对测量

**已实现且已验证。** Linux x64 专属 systemd 使用同一 Release Server 制品和确认后的具名 TUN 执行。原发布 `c826bce073c340a8a8800c2187434990`、执行 `8c81044966fc4a68b7cc3a757e040150`、Mihomo PID `947`。每轮从同一 Server 内容根快照启动，保留同一 Helper 与 Mihomo 进程身份。先执行启用、禁用预热对，排除预热，再交错执行启用→禁用、禁用→启用、启用→禁用三对。每轮使用生产 PAM/JWT，模块目录与 TUN 读取分别核对启用状态和 200 或 404。登录后每五秒探测真实 HTTP 与分流 DNS，最后在第 31 至 35 秒逐秒采样。每个点同时含 Server、Helper、Mihomo 及其可归属后代，本批 40 个点各记录三个进程，指标齐全。每轮结束核对原队列、原生台账、配置镜像和原具名执行检查点逐字节一致。

各点的单位为 MiB。先对每轮五点取中位数，再计算同对启用减禁用。

| PSS 范围 | 第一对 | 第二对 | 第三对 | 三对中位差 |
| --- | ---: | ---: | ---: | ---: |
| Server | +13.480 | +12.580 | +14.529 | +13.480 |
| Helper | −9.794 | +3.616 | −7.823 | −7.823 |
| Mihomo | −0.832 | +0.082 | −0.008 | −0.008 |
| 整树 | +2.854 | +16.279 | +6.698 | +6.698 |

RSS 的整树中位差为 +6.586 MiB，私有驻留为 +6.758 MiB。同一 Helper 的每轮 PSS 中位数从预热首轮 79.000 MiB 上升到最后一轮 112.478 MiB，跨顺序的正负差值不一致。该漂移使整树差值的幅度无法稳定归因于管理面。此轮为持续原生执行下的静态启用与禁用对照，加载路径、认证状态和 200/404 的处理差异均纳入观测。

Windows Release TUN 激活与 38 项边界回归通过。Linux Release 构建、八轮 Server 进程退出及整个专属 cgroup 清理通过。宿主共享服务、代理及 resolver 文件指纹一致，采样期间无原配置或具名启停重派。运行记录位于 `.artifacts/mb04-tun-full-zero-dns/`。原始日志 `linux-named-start-baseline.log` SHA-256 为 `f3e2929c87757adc00fc19796ca292bc910bf667a80e3a67c163541a29aac9e7`，摘要 `named-start-baseline-summary.json` 为 `c51f3c28b92930b17e6143f3a405efde6df6ba1002910e21d8f09525e12043cb`，预先冻结的源码及制品清单 `named-start-baseline-manifest.json` 为 `963dfb57f2e8823a8f4f6f9e75e69a9971885ba49deeda5ef287498e0d6360d0`，运行归档为 `ce5425420f3c353be0cff62e067a5122a6f341805e78d1387703274069df1143`。

**尚待独立验证。** Helper 的时间对照及只读查询分解见下一节。对象释放、ALC 回收、动态休眠和生产内存收益仍分别验收。Proxy 保持 resident。

### 活跃 TUN 的 Helper 只读成本分解

Linux x64 私有 systemd 先确认原发布 `0c9fc9100729475b807a5983b11e6faa` 的具名再启动 `943d22ed4efa4341b8bd37c9c02351fb`，再正常关闭 Server。固定 Helper PID `1040` 和 Mihomo PID `951`，交错执行三轮空闲、PAM 与原 ID 的 `MihomoRuntimeQuery`。每个窗口 22 秒，主动窗口由同一非 root 身份经真实 Helper socket 连续执行六次，凭据仅走专属进程 stdin。三个空闲窗口的 Helper 完成事件为零。主动窗口分别通过 PAM 或匹配原发布与原执行的只读回执，审计各六次，无其他命令。54 个采样点均保留两进程身份和内存指标，窗口前、中、后核对同一真实 HTTP、分流 DNS 与管理选路，原配置、镜像、队列和 Helper 台账逐字节一致。

下表为各窗口末三点中位数减首三点中位数，单位 MiB。

| Helper PSS | 第一次 | 第二次 | 第三次 | 中位差 |
| --- | ---: | ---: | ---: | ---: |
| 空闲 | −0.183 | −0.251 | −0.233 | −0.233 |
| PAM | +2.095 | +0.926 | +6.559 | +2.095 |
| 原具名运行时查询 | +7.281 | +7.744 | +0.886 | +7.281 |

Helper PSS 从 98.617 MiB 到 123.438 MiB。私有驻留的变化与 PSS 方向一致。三个查询窗口消耗 1.17–1.46 秒 Helper CPU，PAM 为 0.07–0.10 秒。首个查询窗口结束时文件描述符从 168 增至 432，在随后 PAM 窗口保持，后两次查询后分别降到 302 和 164。Mihomo 的每窗 PSS 差值为 0–0.203 MiB。固定窗口内，仅空闲未复现增长，两种调用都增加驻留。当前证据可以定位到调用组，仍需区分本机子进程输出管道、托管对象和其它本地资源的持有时间。

Windows Release TUN 激活与 38 项边界检查、Linux Release 构建通过。Server、Helper、Mihomo 及专属 cgroup 退出清理完成，宿主共享服务、代理和 resolver 文件指纹一致。记录位于 `.artifacts/mb04-tun-full-zero-dns/`。原始日志 `linux-named-start-helper-profile.log` SHA-256 为 `fa1c47f2aa6f7e23b7e0e734c75fd7289b11d7ccfdc895979fce3cfcde497d4a`，摘要 `named-start-helper-profile-summary.json` 为 `d1b2f447474aa8e538e10df4fe5c5c2722ceb615b7b35ba071e2e976babd44a2`，冻结清单 `named-start-helper-profile-manifest.json` 为 `62a25b0d9f2dfcc93ba12ce877d4111785089e8f31b544f0ed38795961842f5d`，运行归档为 `17ab58ad40ffe6ae75be98db5ad76954601dd8bae77908b1c2a71d5a1d1a04fd`。

原具名查询所调用的本机子进程输出管道见下一节。整模块交接、对象释放、ALC 回收和生产收益分别验收。Proxy 保持 resident。

### 本机查询的输出管道关闭

**已实现且已验证。** `NativeRuntimeProcess` 同时读取并在调用退出时显式关闭 stdout 和 stderr。保留有界输出和诊断隔离。该 .NET 版本将已取得的输出流交由调用方关闭。[.NET 10.0.14 Process 实现](https://github.com/dotnet/runtime/blob/v10.0.14/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.cs#L831-L852)

Linux 私有 systemd 使用同一夹具和六次原具名执行查询，对照归档只将独立 Helper 替换为上一轮原二进制。旧版在六次 PAM 请求后保持 46 条管道，在六次原具名查询后为 310 条，其中 264 条为该批开始时不存在的管道。新增管道断言按预期失败，专属 cgroup 仍完成清理。对照日志 `linux-named-start-helper-pipes-control.log` 的 SHA-256 为 `40abdd64e173efdb0a6435ee55e72e6e24951422ea103b203d70b560a7a1f2aa`，归档为 `a86299203d0b7fc8ba16256e34b848facd9500ac6604db29a6ad6f1fb75a09f8`。

修正版原发布 `efc45321e3dc4539b00f43ca2ce8f8dd`、具名执行 `79c1e225701b4013bd8ae8b9a7b5bb9c`、Helper PID `1042`、Mihomo PID `952`。完成同序的九个 22 秒窗口。三批各六次查询的管道数均为 4→4，管道身份无新增。54 个采样点的 Helper 文件描述符总数均为 126。原回执、管理选路、配置身份、真实 HTTP 和分流 DNS 全程核对，审计无原生变更重派。

修正版 Helper 查询窗口 PSS 增量依次为 +7.211、+7.918、+3.863 MiB，中位 +7.211 MiB。前轮完整同负载中位为 +7.281 MiB。此次证据确认管道关闭，查询驻留增长仍存在。Windows Release TUN 激活、38 项边界检查和模块边界聚合回归通过。Linux Release 构建、专属进程退出和 cgroup 清理通过，宿主共享服务、代理及 resolver 文件指纹一致。

原始记录位于 `.artifacts/mb04-tun-full-zero-dns/`。`linux-named-start-helper-pipes.log` SHA-256 为 `2b7c8a2e73fd8d6b14897af25c572aa0e8409dba820aa177d75b2d8fe4bd3f2c`，摘要 `named-start-helper-pipes-summary.json` 为 `5bd168bebedabda64d5c89f06274c228c2a9351bb146e5606ca1751b60143f47`，冻结清单 `named-start-helper-pipes-manifest.json` 为 `20d86bb2027dd287c61d66d241994df29f22f346b5b0d2f498f171061780daee`，运行归档为 `691002d9955ab2641636a63118bb0b7f5ebb71bf8c8c80cf39a0515847b9c143`。

**尚待独立验证。** 具名运行时查询的重复原生核验见下一节。对象释放、ALC 回收、动态休眠和生产收益仍分别验收。Proxy 保持 resident。

### 复用具名 TUN 查询的进程核验

**已实现且已验证。** 运行中的受管 TUN 查询直接使用同一次原生归属核验得到的注册与进程身份。该路径仍核对进程和调用 ID、运行程序摘要、已加载凭据、已安装制品、原配置、管理选路、设备、规则及 DNS。保留网络核验后的进程身份复核。停止路径继续使用原停止核验，外部单元与未确认执行依原门禁拒绝。

Linux x64 私有 systemd 两版分别运行同序的九个 22 秒窗口。每批六次原具名查询，夹具只统计直接由 Helper 派发的固定单元 `systemctl show`。对照使用上一轮已修复管道的 Helper，三批均为 18 次。候选三批均为 12 次，空闲和 PAM 仍为零。六次查询的 Helper CPU 原值为 1.48、1.32、1.20 秒，候选为 1.12、1.04、0.81 秒，中位数减少 0.28 秒。两版的查询窗口 PSS 中位增量分别为 +6.012 和 +4.613 MiB，三批差值方向不一致。两版各 54 点的 Helper 文件描述符均为 126，查询完成后无新增残留管道。

候选原发布 `65755d3832bf4774874505f088b972cc`、具名执行 `7f53cf637a284b1ba109a64fabda3ed2`、Helper PID `1038`、Mihomo PID `946`。原回执、管理选路、配置及真实 HTTP 和分流 DNS 流量通过核对。具名执行停用接续 `cc9e79649fc5437a83abf3b8304aa36f`、原发布停用门禁 `d6299318032c4ec183db278d996800a9` 复验通过，包含检查点损坏、缺失或不匹配见证，以及原生停止的待核验保护。所有专属进程与 cgroup 清理完成，宿主共享服务、代理和 resolver 文件指纹一致。

Windows Release TUN 激活、38 项边界与模块边界聚合回归通过，Linux Release 构建通过。日志及冻结清单位于 `.artifacts/mb04-tun-full-zero-dns/`。对照日志 `linux-named-start-helper-observation-control.log` 的 SHA-256 为 `27f61ccb3a027902992ab2ea011e59126e4e5fd15d1fae64a1d5cfc7a1ad22b4`，归档为 `54ee65aa9dccbe51b96041a5b06dbd0a9130b390c1560187dccb3d7a05faed0c`。候选日志 `linux-named-start-helper-observation.log` 的 SHA-256 为 `b876dbae26d69580d1d353048263a0d5f2f13a7cc71157220c97217f1a2575af`，归档为 `9f77a780738ae100facd247e80bd5570974c71b0bfe90c51e78d7a857c1e2f0a`。

**尚待独立验证。** 当前数据支持隔离负载下的命令次数和 CPU 降低，PSS 收益仍待确认。对象释放、ALC 回收、整模块安全交接和生产收益独立验收。Proxy 保持 resident。

### 原回执查询与安装环境探测

**已实现且已验证。** `MihomoRuntimeQuery` 用于原回执、状态概览、组件状态和当前归属核验。原生身份、配置摘要、回执、提交见证与发布锁的判断沿用原逻辑。安装 HTTP 通过专用 `MihomoRuntimeInventoryQuery` 返回环境报告，安装计划继续独立核验依赖。

Linux Release 的 40 项生命周期边界验收中，受控缺席安装对同一原生状态核对了两份响应。原回执查询未调用磁盘、目录和 IPv6 探测，安装清单各调用一次，仍返回平台、服务宿主等共五项观察。除环境报告外两份快照的 JSON 一致。Helper 响应正文原回执为 686 字节，安装清单为 1357 字节。字节数取解码后的正文，范围限该受控样本。

2026-11-10 在既有 Linux 私有 systemd 复验真实 TUN。原具名启动 `57ab8a70-cbbe-40b9-95d5-19f3c15c282e` 在恢复应答中断后按原 ID 只读结案。当前管理选路漂移时，实际认证安装 HTTP 仅发出一次清单查询，返回完整五项报告和同一配置身份。原历史成功回执保持，停用计划与禁用启动仍因发布 `24cf9591-1c49-4c65-984e-4453e6b143da` 的当前归属不可核验被拒绝。恢复管理选路后通过禁用启动。原 Mihomo PID `952`、路由表 `1397118064`、真实 HTTP、分流 DNS 和 Helper 台账保持。只派发一次原恢复。专属进程和 cgroup 清理完成，宿主共享服务、代理和 resolver 指纹一致。

实机日志 `.artifacts/mb04-tun-full-zero-dns/linux-tun-query-environment.log` 的 SHA-256 为 `9cb1cd78f56814b2641e64334fd7cfcdef3309c9fcd13f9d1dfe8dbde81116b4`，归档为 `f5c13129228e4ccca1cbccefd021455f50bb2e00581453e7f9a4912d15534d08`。Windows Release Mihomo HTTP 57 项、注册 26 项、发布 113 项、安装 88 项、移除 92 项及组件、Proxy Core、TUN、模块边界回归通过。

**仍待验收。** 本轮单列探测次数、响应正文大小和实际进程退出。配对 CPU、延迟、隔离内存、对象释放、ALC 及生产收益仍待测。Proxy 保持 resident。

### 活跃 TUN 的回执与安装清单配对测量

2026-11-10 在既有 Linux x64 私有 systemd 中，对同一 Helper PID `1043` 与活跃 Mihomo PID `954` 比较两种生产 IPC 请求。原发布 `14163dfb-16c0-461b-8f4a-a620cc0c009f`、原执行 `1838669f-e0fe-4511-a283-de1839130cb8`、配置摘要 `093258c4aa495a812416d4fe255545db074bded69cc459acc5cb4505e48bbfc9`、专用路由表 `1406349471`。测量前正常关闭 Server，Helper 与 Mihomo 跨所有窗口连续运行。

四对窗口以「回执、清单」和「清单、回执」交替。每窗 22 秒，以相同非 root 身份串行执行六次请求。回执请求携带原执行 ID，清单请求读取当前安装及环境。两者都核对原发布及配置身份。延迟为 AF_UNIX 往返中位数，Helper CPU 为第 21 秒与第 0 秒累计量的差。PSS 增量为第 19 至 21 秒三点中位数减第 0 至 2 秒三点中位数。该采样覆盖窗前与收尾驻留，执行期间的峰值待单独测量。

| 配对 | Helper CPU 差（秒） | 往返中位延迟差（ms） | Helper PSS 增量差（MiB） | 两进程树 PSS 增量差（MiB） |
| --- | ---: | ---: | ---: | ---: |
| 1 | +0.41 | +28.75 | −7.08 | −7.08 |
| 2 | −0.08 | −2.21 | −0.20 | −0.38 |
| 3 | +0.13 | −1.03 | +9.22 | +8.99 |
| 4 | −0.08 | −8.65 | −0.11 | −0.25 |
| 中位差 | +0.025 | −1.62 | −0.15 | −0.32 |

差值为回执减清单。每窗均有 12 次原生 `systemctl show`，调用结束后未发现新增管道。回执正文 1266 字节，清单正文 1618 字节。四对 CPU、延迟和 PSS 的差值均有正有负。结果支持正文缩减，CPU、延迟和驻留收益尚无法稳定归因。

48 个采样点的 Helper 与 Mihomo 进程启动身份一致。各窗前、中、后核对真实上游 HTTP、分流 DNS 与管理选路，原队列、Helper 台账、检查点与配置逐字节保持。Linux Release 夹具构建、Windows Release Proxy Core、TUN 边界和模块里程碑通过。实际专属进程及 cgroup 已清理，宿主运行时、代理和 resolver 指纹一致。原测量日志的结尾沿用了三窗的旧文案，清单、48 个采样点和八条请求记录确认四对，夹具现按实际窗口数输出。

原始日志 `.artifacts/mb04-tun-full-zero-dns/linux-tun-helper-query-pairs.log` 的 SHA-256 为 `d4b47a419a45250df174f5854ab9f98bcba407675d4e8438bd782232439a4981`，归档为 `1e61e074a27b127edb282f69c35d8746483f84a1b9cdfb36ef477950196e3e0d`。配对摘要 `.artifacts/mb04-tun-full-zero-dns/tun-helper-query-pairs-summary.json` 的 SHA-256 为 `a337cb1592900a7dacdc45d11c1c3a16387969e39b0d9c2e4cd1a223dddb7b1a`。

**仍待验收。** 本轮单独记录了实际进程退出及隔离负载差值。对象释放、ALC、执行期间峰值、整模块安全交接和生产内存收益仍待独立验证。Proxy 保持 resident。

### 平台能力与订阅下载适配器作用域

**已实现且已验证。** 概览的平台能力适配器与订阅下载适配器从根单例移到原执行作用域。前者由概览读作用域解析，后者由导入请求或后台工作作用域解析，下载调用结束时释放本次创建的 HTTP 客户端。Windows Release 注册检查验证同一作用域复用、跨作用域分离，Core 安全注册不引入两项管理对象。

2026-11-10 Linux x64 私有 systemd 完整 PAM/JWT Server 在活跃 TUN 下读取概览和下载选项，从私有订阅源执行两次真实下载并保存两个草稿。源站收到两次请求，概览能力和活动档案保持。随后复验 [Squid 写回故障与 Core 连续性](Proxy-Squid.md#squid-恢复写回故障与-core-tun-连续性)。原 TUN 发布 `39eb917c178f4c809d75f3e716688205`、Mihomo PID `416`、配置身份、管理选路、上游 HTTP 和分流 DNS 连续通过，原配置只派发一次。重启后仍可读取概览。首轮夹具重复配置已有订阅源地址，改为直接使用私有地址后复验通过。

日志 `.artifacts/mb04-tun-full-zero-dns/linux-scoped-management.log` SHA-256 为 `046667bfecc81f7648428aa03607e0c59ad1602c3e102280d703c52198c7701f`，归档为 `2e2484a4d0b33994f5bfd34cff6112a917be9904619a3457b6f7a96c5216079c`。专属进程与 cgroup 已清理，宿主指纹一致。

Windows 订阅回归更新了上一阶段恢复写回故障的断言。故障代次仍可投影中断状态，持久台账字节保持。只有新恢复代次保存原批次中断结果。41 项订阅边界检查通过。

**已实现但实机未验证。** 本轮验证适配器不由根单例持有，以及真实请求和进程退出。对象 GC、ALC 回收、隔离负载内存与生产收益分别待测。

**未实现或仍受门禁保护。** 整模块动态驻留尚未验收，Proxy 保持 resident。

### 只读合并器的代次归属

**已实现且已验证。** 根引用审计确认 `ProxyOperationStore` 是唯一的托管队列，负责准入、原操作、在途任务、清理故障和原终态。其发布台账、订阅仓储和运行时管理依赖属于原持久权威。根中的 `ProxyAuditStore` 串行追加审计文件，`SquidConfigurationStore` 串行保存发布投影，两者仅持有宿主路径和各自的写入锁。禁用启动从原持久状态检查责任。

运行时、概览、连接及流量合并器移入当前 Proxy 代次。根服务注册保留工厂描述，HTTP 响应单独持有租约。采样租约绑定同一实例，覆盖控制器任务及异步作用域释放。概览调用同代次的运行时合并器。最后一个读者取消时，新采样等待原采样排空。作用域清理失败在原代次保留工作租约及隔离。

Windows Release 控制器、Proxy 核心安全及模块里程碑均通过。实际 Kestrel 验证根容器无法解析四个合并器，概览与运行时共用在途观测，各响应独立保留工作。还核对并发取消、采样排空、审计和异步清理失败隔离。模块回归日志 `.artifacts/mb04-tun-full-zero-dns/windows-read-owner-module-boundary-milestones.log` 的 SHA-256 为 `973c7247b8eee0406e5445893769f27e45d22756756630d160b4cca43461812c`。

Linux Release 私有 systemd 的原具名启动 `1514233f8eba4f28a6ec0b72a22c8826` 丢失原应答及首次查询终帧，恢复期间观察两项工作租约和新键门禁。原发布 `e071c41cc9594bff8d8b6fe9b2aff127` 的配置摘要、管理选路、原生检查点与 Mihomo PID `946` 跨 Server 重启保持，HTTP 上游及分流 DNS 通过。原发布、停止、启动各一次派发，原回执及同键保持。另一用例在 Squid 原回执 `5887a15c-b425-4d9d-84fd-aec077327ae1` 写回故障时保留原队列门禁，Core guard 完成修复，受管 TUN 及真实流量持续，Squid 只重载一次。两个用例均确认专属进程和 cgroup 清理，宿主服务、代理及 resolver 指纹一致。日志 `.artifacts/mb04-tun-full-zero-dns/linux-read-owner-tun.log` 的 SHA-256 为 `030e5b8f138b6e2ede055d94c3d835dcb9bf6581a6ad7c428b23bf370af31465`，归档为 `d769243ea0cf3d6ff94a0ff6874dd5d2794c2448d762814046177fad57f92fae`。

Linux Release 受管 Mihomo 专项通过 180 项。原发布恢复 `0706705f-f3cf-4d4f-b8f6-f51ea86a5ded` 排空原回执及作用域。恢复用例 `82e538ff-0b9a-48ba-b64f-5c2dbc418496` 实测流量、概览、连接和运行时的并发共享及独立取消，保持原生连接 `a7031d32-4061-4275-b5fd-5e761e0c6ac6` 和实际转发。连接解析器也观察到原有的闲置卸载和新代次唤醒。日志 `.artifacts/mb04-tun-full-zero-dns/linux-read-owner-managed-mihomo.log` 的 SHA-256 为 `3f18d3e12e251346b806b1e7529a7d551d1e8b824bf96dbea1792ea8c1ce0e6f`，归档为 `e6bec83d02c4961614863f29e41a70caa17ef4f8723d7968c6a2155576e6a073`。

完整 PAM/JWT 复验核对跨重启原连接和发布修订、真实控制器连接取消的两份租约，以及同次停机的原 TUN `0be5c634-7eb3-471a-8b2c-270dec1e887c`、系统代理 guard 和授权到期。首次试跑使用旧测试程序集和依赖清单，分别在协议绑定及无关模块的停用检查处退出。换用同一源码的 Release 输出后，旧夹具的单租约断言暴露，更新为同时核对采样和响应两份租约。修正后完整用例退出成功，宿主指纹一致。复验日志 `.artifacts/mb04-tun-full-zero-dns/linux-read-owner-auth-recheck.log` 的 SHA-256 为 `a4416433360a31beeb6e5b77ce32432b18d0e496e557b2888b8f648c73db3902`，归档为 `eec292641deafa5578a5e070e73223f60ced4f9dfd9b33b4df666443c5bc9e02`。试跑原始记录同目录下的 `linux-read-owner-auth.log`、`linux-read-owner-auth-final.log` 和 `linux-read-owner-auth-current.log`。

**已实现但实机未验证。** 本轮核对根引用和实际进程退出。合并器代次对象 GC、整模块 ALC 回收、隔离负载内存差值及生产收益分别待测。

**未实现或仍受门禁保护。** 实际读取作用域仍使用 Host 服务工厂。整模块交接需要验证窄契约和剩余管理执行图，Proxy 保持 resident。

### 概览执行器的代次边界

**已实现且已验证。** 概览采样器创建时由当前 Proxy 代次创建执行器。执行器只保留同代次运行时采样入口。每次采样从作用域借用活动档案读取、Core 恢复状态读取、平台能力，以及可达性、TUN 状态和连接计数三项 Mihomo 读取。作用域拥有控制器实例及其清理。

Windows Release 控制器、Proxy 核心及模块里程碑回归通过。Kestrel 夹具使用生产概览注册，核对根容器无法解析概览执行器，响应仍包含原活动档案及连接计数。重叠概览及运行时调用复用原采样，原任务、响应及清理各按已有租约排空。模块日志 `.artifacts/mb04-tun-full-zero-dns/windows-overview-executor-module-boundary-milestones.log` 的 SHA-256 为 `38a96e95985b6f7c5b846ac4eb52062f1ab0dfa070b1bc0f73d26075d046c29f`。

Linux Release 私有 systemd 在活跃 TUN 下完成真实概览及订阅草稿和刷新。原 Squid 发布 `d2218036-373a-4c98-be53-b2cf89cebcf5` 写回故障期间，Core guard 完成修复，原 TUN 和实际转发保持。独立用例的原具名启动 `2b064614c9684d54a0eb1a9d78d79b75` 丢失原终帧与首次查询终帧。按原 ID 核对终态，同键返回同一回执，新键在待核验期间受阻。启动仅派发一次。管理选路、配置摘要、原生检查点与 Mihomo PID `945` 跨 Server 重启一致，真实上游 HTTP 和分流 DNS 均通过。两次均确认专属进程及 cgroup 退出，宿主服务、代理和 resolver 指纹一致。日志 `.artifacts/mb04-tun-full-zero-dns/linux-overview-executor-tun.log` 的 SHA-256 为 `cb0a8f7bfd214bdb243a9256c53e637176195c3d8bd94bb207aca3b24feffb22`，归档为 `ecdab7e2a4f4c3159964b3eb51b6e13a2460a3a61dbc54761d86eb0c2becbd3a`。

Linux 受管 Mihomo Release 全量通过 180 项。实际概览读取原活动档案和持有连接，单次概览只发一次原生 `/version` 且只读取一次恢复状态。控制器拒绝凭据时只收到一次真实 401，原发布和实际流量持续。恢复用例 `e10c9501-f68e-43f1-bae5-169b978dc312` 复验概览和运行时共享观测、各调用独立取消及原生连接保持。既有连接解析器的闲置卸载与新代次唤醒亦通过。日志 `.artifacts/mb04-tun-full-zero-dns/linux-overview-executor-managed.log` 的 SHA-256 为 `141e0d8cb156f83b6d27419c4ce9af13e09aa5a8d981152d55bb16267b1d5fa5`，归档为 `900c1580a9c96ce809ba9fff0c750942ae820082372282ddb35136f8aba745b5`。

**已实现但实机未验证。** 本轮确认概览的根引用边界和专属进程退出。概览对象 GC、整模块 ALC 回收、隔离负载内存差值及生产收益仍分别待测。

**未实现或仍受门禁保护。** 概览执行器仍位于 Server 程序集，采样借用 Host 作用域。跨程序集的可回收边界和整模块安全交接仍开放。Proxy 保持 resident。

### 概览与连接读取共享可回收代码

**已实现且已验证。** 概览采样把档案、恢复状态、运行时及控制器只读观测编排迁入独立 Proxy 执行程序集。程序集仅依赖共享契约与框架，跨边界传回调和读取结果。Host 继续持有活动档案、恢复台账、控制器作用域和协议 DTO。概览和连接解析共用代码代次、30 秒闲置窗口及未回收代次上限。概览先取得作用域内的代码租约，待读取依赖完成清理后释放。HTTP 响应租约独立覆盖实际响应。

Windows Release 控制器、Proxy 核心安全和模块里程碑均通过，包含真实 Kestrel 并发采样、取消和作用域排空。模块日志 `.artifacts/mb04-tun-full-zero-dns/windows-overview-code-module-boundary-milestones.log` 的 SHA-256 为 `5f2e7a33ec7119e5bb3952566cb3ab688baa33d9cbe318c5a6e44acca37d71d8`。

Linux 私有 systemd 在活跃 TUN 下通过概览与真实下载。Squid 原发布 `153cf90e-3118-4fe1-970d-bcb80d98f957` 写回故障时保留原队列门禁，Core guard 与受管 TUN 实际流量继续。独立用例的具名启动 `f9730a663ff7425aa5890365a6f172e5` 丢失原终帧及首次查询应答，两项租约及新键门禁通过。原发布、停止和启动各派发一次。按原 ID 恢复后，同键终态、管理选路、原生检查点、配置身份和 Mihomo PID `942` 跨 Server 重启保持，真实上游 HTTP 和分流 DNS 通过。专属进程及 cgroup 退出，宿主服务、代理及 resolver 指纹一致。日志 `.artifacts/mb04-tun-full-zero-dns/linux-overview-code-tun.log` 的 SHA-256 为 `e5c4a58d9e2e67724e22ecae5bd0dc647d40ea14458ac6ba8df748e72f033ace`，归档为 `50148d907f1d3790f7f6aa824aa1858bda321a34c6a934735849e516b803e3d2`。

Linux 受管 Mihomo Release 全量通过 180 项。Server 保留原概览和连接 DTO 后，旧共享代码代次经闲置释放及测试 GC 的弱引用检查确认回收。下一次真实连接读取唤醒新代码代次。原生恢复用例 `6d8205ea-bb7f-4495-9535-f9bac2a34c3e` 通过四类共享读取及独立取消，原连接 `31f16515-2b9d-4e98-aca6-5e7d47626f59` 及实际转发持续。原发布恢复 `885ffadf-c0f9-4359-a337-9b5e0491644f` 排空原 Helper 回执和作用域。日志 `.artifacts/mb04-tun-full-zero-dns/linux-overview-code-managed.log` 的 SHA-256 为 `aad5f13acc5291b3fdfaaff43ef47c75c1d5f769dcd9a07052c4a46c598af387`，归档为 `daad08960f3ba2140ae1a186d3fbe8b09dd65bb791b39e8f34835c665276cb1b`。

**已实现但实机未验证。** 共享读取 ALC 回收及专属进程退出分别通过。驻留 Proxy 对象的回收、读取异常和清理故障下共享代码的释放证据、隔离负载内存差值及生产收益分别待核对。

**未实现或仍受门禁保护。** 整模块安全交接仍开放。Proxy 保持 resident。

### Mihomo 控制器前台管理作用域

**已实现且已验证。** 分组选择、路由切换、延迟探测和连接关闭使用当前 Proxy 代次的前台作用域。Windows Release 核对内部路由切换只发送含 `mode` 的原生 `PATCH /configs`。旧调用会触发原生完整重载并返回 204，实际模式仍保持旧值。故障通过 Linux 读回发现，已按 Mihomo 的模式更新端口修正。[Mihomo 配置路由源码](https://github.com/MetaCubeX/mihomo/blob/v1.19.27/hub/route/configs.go)

Linux 专属 systemd 在故障代次拒绝四条管理路由，启用重启后通过真实回环控制端口验证选择组切换及恢复、路由模式切换及读回、到专属源站的正延迟和关闭已观测连接 `8ef93fe7-ec99-4552-b38f-1e452f66d2d1`。四条路由排空后，原 TUN 发布 `cb3132bb2f6f450f94c68d037b0b385f`、原生台账和配置摘要保持，实际 HTTP 与分流 DNS 通过。夹具试跑先补齐具名选择组和专属控制端口，再验证实际原生效果。

Windows Release 控制器、Core 安全和模块里程碑通过。Linux 专属进程和 cgroup 已清理，宿主服务、代理及 resolver 指纹一致。实机日志 `.artifacts/mb04-tun-full-zero-dns/linux-controller-foreground-confirmed.log` 的 SHA-256 为 `4146ad12f924c7dda36ff188dc45e0d40e1f7bb147cb642bc6c97a9c0f1ad14f`，归档为 `0201f782bc45613f169159d1d421beb22342c4130c6aefe8eb083bd1354dbd18`。

**仍待验收。** 此轮确认实际进程退出。控制器同步调用丢失应答后的原请求确证、对象释放、整体 ALC、隔离内存及生产收益仍独立开放。Proxy 保持 resident。

### 概览读取失败与清理隔离

**已实现且已验证。** 概览私有执行栈的异常转换为独立 Host 错误。调用取消保留原取消令牌。清理成功后释放代码租约。清理结果不明时采样器保留原作用域和工作租约，原 Proxy 代次进入隔离，后续读请求受准入门禁保护。

Windows Release 控制器、Proxy 核心及模块里程碑通过。保留 Host 错误时，旧可回收 ALC 的弱引用消失。实际 Kestrel 注入概览异步清理失败，工作租约及原代码超过闲置窗口仍保留，GC 后原作用域资源仍可达。后续读返回 503。最终模块日志 `.artifacts/mb04-tun-full-zero-dns/windows-overview-fault-module-boundary-final.log` 的 SHA-256 为 `b231066af65b72a6bd5d44b858193798a88fe64bd16831af882f4131b06995cc`。

Linux 私有 systemd 在原 Squid 发布 `14b3f594-e16b-4b64-ac55-3560ec8714a4` 完成恢复后，阻塞并令概览作用域清理失败。阻塞时保留响应和采样两项租约，失败后保留一项采样租约，新读取返回 503。强制退出并重启 Server 后按原 ID 读取同一 Squid 回执，真实重载仍只有一次。原 TUN 发布 `dc989fb80ff74762b1b6159db5ef9c11`、Mihomo PID `416`、管理选路、配置和原生记录保持，实际 HTTP 和 DNS 均通过。初次试跑达到隔离断言后被夹具误判预期 SIGKILL，原日志 `.artifacts/mb04-tun-full-zero-dns/linux-overview-fault-tun.log` SHA-256 为 `d4836cdc983ac10162927f416fecb3f5154df3730d004dbf524ab30b942a9b5c`。

独立 Linux 用例的具名启动 `3d283b44f3514fa5b22364780cc23631` 丢失原终帧和首次查询应答，原 ID 及同键终态保持，待核验期间拦截新键，原启动只派发一次。两项均确认专属进程与 cgroup 退出，宿主服务、代理和 resolver 指纹一致。最终日志 `.artifacts/mb04-tun-full-zero-dns/linux-overview-fault-tun-final.log` 的 SHA-256 为 `91b72b5a11d292bc7442833510912af8bb6d9ce73e38222267431a08f233219d`，归档为 `aa79ddda4c7bb4f37010207aca59fcacb6b821902e62773f4f4a88a61f9f6470`。

Linux 受管 Mihomo Release 矩阵通过 180 项。保留中性 Host 异常和概览 DTO 后，旧共享 ALC 经测试 GC 确认回收，原生转发保持。恢复用例 `4ed7ca87-09d5-4811-8abd-1e83cd8dc230` 继续通过真实共享读取与独立取消。日志 `.artifacts/mb04-tun-full-zero-dns/linux-overview-fault-managed.log` 的 SHA-256 为 `22a4c78a9e4215631e48231b3c98d79c4ac2855f500239d6f6d0e83328f7e884`，归档为 `d68e156fc38a21af91dcd72be2feafa1eb14589d1162d5e628c9c3004311d0e0`。

**已实现但实机未验证。** 本轮分别验证读取 ALC 回收、清理故障时的资源保留和专属进程退出。Proxy 整体对象与 ALC 回收、隔离负载内存差值及生产收益分别待验。

**未实现或仍受门禁保护。** 整模块安全交接仍开放。Proxy 保持 resident。


### Mihomo 观察 HTTP 作用域与故障隔离

**已实现且已验证。** 运行时、概览、代理组、控制器配置、连接、流量和日志的 HTTP 外层作用域由当前 Proxy 代次持有。四个采样器继续独立持有采样和异步清理租约。Windows Release 模块里程碑和控制器回归验证原鉴权、响应与取消排空。

Linux 专属 systemd 的真实 Mihomo 控制器配置读取已返回 200，异步清理仍持一份工作租约。故障后同代保留原作用域和一份租约，七条观察路由均返回 503，原匿名订阅仍可读取。强制退出并启用重启后，原生配置读回一致，七条路由恢复，租约排空。原 sing-box 发布 `667e9ae5-5304-4fc0-9b7b-51ab372be8f5` 的终态及队列字节一致，原生应用派发一次。原 TUN 发布 `4d4f59d8d6e44cdf91d5a4ff3912f75e` 的台账一致，实际 HTTP 和分流 DNS 通过。专属进程与 cgroup 已清理，共享服务和网络文件指纹一致。首次试跑缺专属控制器端口配置，返回降级状态，补齐后完成实测。

日志 `.artifacts/mb04-tun-full-zero-dns/linux-controller-observation-final.log` SHA256 `6168e4eeeeb1947d2897754a579908669ccdcfd588519be24c132f576d9e83cd`，归档 SHA256 `a34bb2a330ecb60b9652e3f13fdbd871111fdb18ae86e5a5cca4aee5869172f4`。

**仍待验收。** 本轮确认进程退出。整模块对象释放、ALC 回收、隔离负载内存及生产收益分别开放。Proxy 保持 resident。


### 外部 Mihomo 版本探测的取消边界

**已验证的失败路径。** 本地 Release 诊断复用当前 Server 的 `MihomoRuntimeProbe.GetVersionAsync`。既有 Linux 测试机的专属、非 root systemd 单元中，固定夹具收到 `-v`，写入自身 PID 后以同一 PID 进入 `sleep`。取消调用后探测返回空值，原 PID `3962858` 的命令行保持，进程仍存活。专属单元退出才清理该进程，实测原 PID 和 cgroup 消失，共享 Mihomo、Squid、sing-box 与 resolver 单元状态一致。本轮直接调用生产探测实现，覆盖进程取消失败。HTTP 鉴权、真实 Mihomo 版本输出和已退出父进程的子孙场景需单独验证。

日志 `.artifacts/mb04-tun-full-zero-dns/external-probe-repro/linux.log` SHA256 `2eb3031a8600f5a547a031898af43fe3dd7ba4c4cc5e3f968dd57dcd3f184eda`，探测使用 Server 制品 SHA256 `c2990c5c17b4b4a9021641619b0ccdae2634bdb6e30e2635d806b167f697b1ad`。

**未实现或仍受门禁保护。** 外部探测需要独占原进程树、stdout 和 stderr，所有权须先于执行建立。取消或超时必须等原生退出与两路输出完成。缺少确证时保留原工作及准入门禁。后续实现与验收见下一节。现有受管安装台账仍负责原受管运行时操作。Proxy 保持 resident。


### 外部 Mihomo 版本探测的独占执行与重启门禁

**已实现且已验证。** Linux 探测使用独占 subreaper，在原进程退出后用原生进程身份终止并回收脱离会话的后代。原执行者在进程树清空及 stdout、stderr 均到达 EOF 后返回。Server 等待原执行者退出及外层两路管道排空。派发前的持久检查点绑定操作 ID、执行者 PID 和启动时间。取消、停机或超时期间由原 Proxy 工作租约持有任务和清理。工作在途拒绝并行探测。清理不明时保留原记录和租约，同代隔离。启动启用时拒绝新探测，禁用启动拒绝隐藏原责任。

Windows Release 模块边界和控制器回归通过。Linux 专属 systemd 通过匿名拒绝、实际 Mihomo 版本、前台工作租约与客户端断开。原执行者 `2056`、探测进程 `2068` 及独立会话后代 `2083` 全部退出，输出排空后释放租约和门禁。再次探测时强制终止独占执行者，原记录 `68bd19d0-f1b3-48bc-8d80-57be99c50a33` 保留。夹具按 PID 描述符清理测试进程。Server 同代保留一份租约，禁用启动拒绝，启用重启返回 503，原记录和派发见证字节保持。原入站 `d42b6030-90c9-41d3-b5b7-d77f7e10d447` 的回执和队列保持，原生应用派发一次。受管 TUN `820f580936ee4de3bfd6e19b7d20d048` 的配置身份和原生台账保持，真实上游 HTTP、分流 DNS 和管理路由通过。专属 cgroup 清理完毕，共享服务及网络文件指纹一致。

复验使用 `run-proxy-systemd-linux.sh --tun-fd <tests.dll> <Helper-apphost> ipc-http-inbound-tun`，并在测试程序集旁放置同一 Release 构建的 Linux 测试 apphost，启用脱离会话的探测夹具。实机日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-final.log` SHA256 `38b75cc659ecc0dc9a81812431da2e47a7596e21e371bf786fe593f222f57bcb`。归档 SHA256 `577637cfb8e1e2360371b01e413450011c128ec08c47c2a3a6d4f04fb19db9d0`。

**已实现，实机证据有缺口。** Windows 在发送路径前将执行者绑定原生 Job，并在确认 Job 退出和两路管道排空后释放门禁。本轮 Windows 通过 Release 回归，原生 Job 故障窗口待专项验收。

**未实现或仍受门禁保护。** 生产中的未知探测仍缺原执行者的可信清理确证入口。Server 单独中断的实测见下一节。已单独实测 Linux 原进程退出。整模块对象释放、ALC 回收、隔离负载内存及生产收益分别待验。Proxy 保持 resident。


### 外部 Mihomo 版本探测的 Server 单独中断

**已实现且已验证。** Windows Release 模块边界回归通过。Linux 专属 systemd 中，生产 HTTP 启动原探测 `255abb5a-95f7-4edc-945a-9aee9690928d`。夹具在派发后核对一份工作租约、原执行者身份和脱离会话的后代，用 pidfd 绑定 Server、原执行者及两代探测进程，仅向 Server 发送 SIGKILL。隔离夹具以 subreaper 收取原执行者 `2237` 的实际退出码 0，原探测进程 `2250` 和脱离会话的后代 `2265` 均已回收。执行者成功退出的生产路径等待原进程树及探测进程的 stdout 和 stderr 排空。外层 Server 的输出收尾随原进程中断而失去见证。

禁用启动按原 ID 拒绝。启用重启后原探测返回 503，原门禁及派发见证字节保持，未产生第二次派发。Server 中断期间和启用重启后，原 TUN `ce678ec0ba0b4d3f9e792b5396ea7171` 的管理选路、进程身份、配置摘要、真实 HTTP 与分流 DNS 均通过。原生台账与原入站 `634254c2-28c2-480a-8404-7129faa4ea4f` 的回执一致，原发布仅派发一次。专属 cgroup 清理完成，共享服务及网络文件指纹一致。

复验沿用 `ipc-http-inbound-tun` 和同构建 Linux 测试 apphost，并在测试程序集旁放置空文件 `external-version-server-cut.fixture` 以选择仅 Server 中断的分支。实机日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-server-cut.log` SHA256 `9c8c5b018a6580826c400eb5a2fd89233dcb1c1956c6495fa1e805531a666bfc`，归档 SHA256 `0d9542df9f8518e36063f35a76a2c3e945d11b0c3baf10c0c6e8c728f0e390cc`。

**未实现或仍受门禁保护。** 隔离夹具的父进程证据只用于实测。生产仍需由独立可信来源保存绑定原操作、原执行者、进程树及输出排空的见证，之后才能按原 ID 只读对账。Windows 原生 Job 故障窗口待验。此轮确认实际进程退出，整模块对象释放、ALC 回收、隔离负载内存及生产收益分别待验。Proxy 保持 resident。


### 外部 Mihomo 版本探测的 PID 1 退出见证试验

**已验证的宿主能力。** Linux 专属 systemd 另起一个固定试验单元，运行同一 Release 的原版本探测执行者和专属二进制夹具。单元 `rk-mihomo-version-witness-6f0fe7d0eb5f49f8ad41d957dcee7024.service` 以非特权身份启动执行者 `2440`。在关闭控制管道前，通过管理器记录原 MainPID、启动时刻及 invocation `71b21f2d1a334042b16ddc7a4b6de585`。执行者完成进程树和两路输出排空。PID 1 的日志中只有一条该 boot、单元与 invocation 的成功终态，时间晚于原进程启动。另一 invocation 查询为空。使用 Server 夹具账号及相同组权限查询时不可见。共享日志权限保持原状态。

试验单元使用独立操作。生产 HTTP 原探测 `2e724c7f-a608-49ec-9d44-529d7991c891` 的门禁与派发见证字节一致，重启返回 503。活跃 TUN `ae72c9de7ec14fbd80fcbaac398bf071` 的管理路由、配置、原台账、HTTP 和分流 DNS 均通过，原入站回执一致。另一个私有单元复验了原 TUN 补偿作业。[受管 TUN 原作业续验](Proxy-Safety.md#原补偿作业与独立宿主见证联合复验)

复验沿用 `ipc-http-inbound-tun`、同构建 Linux 测试 apphost 和空文件 `external-version-server-cut.fixture`，另在测试程序集旁放置空文件 `external-version-manager-witness.fixture`。Windows Release 模块边界回归通过。Linux 联合日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-manager-witness.log` SHA256 `7afd7f96ad1dc6abfc965eee455f17176f4b7c4c3139aa3f8d48609e73627d8d`，归档 SHA256 `f8338b5e5f8a27dae1886c9f38cc078aaa3c107c8a0fc1b467af3b0bc6ed2247`。

**未实现或仍受门禁保护。** 下一项将固定低权限执行者登记到独立宿主权威，并仅开放查询已登记的原记录。要验证匹配的原操作、执行者、boot、启动时刻、invocation 和排空终态后再解除门禁。日志缺失、权限不足或身份不匹配时继续拒绝新派发。当前结果确认试验进程退出与专属 cgroup 清理。整模块对象释放、ALC 回收、隔离负载内存和生产收益独立待验。Proxy 保持 resident。


### 外部探测的派发退出语义与原响应

**已实现且已验证。** 固定执行者的私有请求包含原操作 ID。正常版本响应带回该 ID，Server 拒绝缺失、歧义或不匹配的响应。执行者在收到有效请求前退出或无法启动原探测时返回 65。退出码 0 表示已派发，原进程和两路输出均已排空。Linux Server 存活时仍须等待原执行者及外层管道结束。中断造成原 Server 无法取得外层响应时，原持久门禁继续生效。

Linux 私有 systemd 实测真实版本与原生探测失败的 HTTP 结果，客户端取消后原进程树和两路输出均已排空。只中断 Server 的原探测 `18d458e8-fb96-4f74-875f-c2bc3a97d71e` 的原执行者以 0 退出，重启仍保留原门禁并拒绝重派。两个独立见证单元分别验证已派发 invocation `5517ae3db7b54bbb941795c5999b75e5` 的 PID 1 成功终态，以及输入管道提前关闭的 invocation `0ec284837d364651bcebf6deae608e62` 的退出码 65、无探测子进程和无成功终态。首轮直连夹具发送紧凑 GUID，未启动见证探测子进程。将夹具改为与生产相同的标准格式后复验通过。

原入站 `d18a9751-75ae-4889-973c-272c7c4390e1` 与活跃 TUN `ddcc3a983c7848a0858d43af355432b4` 的原台账、配置身份、管理选路、HTTP 和分流 DNS 均通过核对。独立补偿作业用例 `1a80a68c95a84a6586b1fa7d194c0ddf` 按原 job `39`、PID `510` 和 invocation `04c9fd5bd0874f9bab48ce596e04c646` 完成对账，同键原终态和租约排空一致，只派发一次 StartUnit。两个专属 cgroup 清理完毕，共享服务及网络文件指纹一致。

Windows Release 模块边界及控制器回归通过。Linux 联合日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-dispatch-witness-final.log` SHA256 `d8bc99f128f02bda82ed62e8c77e2784abbdb109d9a92ce5b0c5e177a166998d`，归档 SHA256 `a0cb03e5aa3d8d142a4865cc42c04817a8773b0de0b7aa0fed33eb5baa6db1f5`。

**仍待验收。** 生产需持久登记固定低权限执行者的派发身份，宿主只读入口仅查询已登记的原记录。Helper 的闭合协议禁止把通用可执行路径、任意命令参数、工作目录或进程环境用作执行输入。独立见证缺失时保留原门禁。Windows 原生 Job 的故障窗口、整模块对象释放、ALC 回收、隔离负载内存及生产收益分别开放。Proxy 保持 resident。


### 固定探测执行者的宿主登记与原终态

**已实现且已验证。** 固定安装路径的 Linux Helper 复用同一版本探测执行者。工作进程必须为非 root、启用 NoNewPrivs，并在读取请求前禁止普通进程转储与调试。执行者按启动参数校验原操作 ID。宿主仅接受本地 root 登记，核对固定安装路径、进程参数、UID、权限与同一 systemd 进程身份。原登记以不可覆盖文件持久保存，绑定 boot、PID、进程启动 ticks、原 invocation 与单元启动时刻。

只读查询要求原进程已退出，且原 invocation 的保留单元仍提供相同 PID、启动时刻、正常退出类别与状态码 0。采用 systemd 的进程级状态，因为单元的通用成功状态也可能涵盖信号退出。查询不写登记。原单元释放或身份、状态证据缺失时继续待核验。

隔离 Linux 以同一 Release 的固定 Helper apphost 登记原探测 `66e566cd-7d86-42c0-88a5-1775fbe59f30`，在发送请求前读回 root 专属记录并拒绝再次登记。执行者 `2553` 排空后，按原 invocation `2af0a2362928440ca7c78c629935212a` 读回同一登记。运行中、错误 UID、其他操作 ID、未派发、信号退出、原单元释放和非 root 查询均拒绝确认。root 身份直接进入版本执行者也被拒绝。试验未解除原 HTTP 探测 `b905adee-1adf-4b22-943e-839d001d1ba8` 的门禁，重启仍拒绝重派。

活跃 TUN `bcf8f374d597451d869c603d1c169c48` 的管理选路、配置、原台账、真实 HTTP 与分流 DNS 均保持。独立 TUN 补偿 `a6c4a5dd8c344011ad45fbc24069b764` 以原作业 `39`、PID `515` 和原 invocation `c1c5a26de0ba4553a0f083a8a1c1b2b2` 完成对账。只派发一次 StartUnit，同键原终态与租约排空通过。两个专属 cgroup 清理完成，共享服务与网络文件指纹一致。Windows Release 模块边界及控制器回归通过。

实测入口为 `ipc-http-inbound-tun` 和 `ipc-http-restored-job-cut`。前者在测试程序集旁放置同构建 Linux 测试 apphost、`external-version-server-cut.fixture` 与 `external-version-manager-witness.fixture`。Linux 日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-registered-witness.log` SHA256 `08c042a72b888c0addd8a16370211609e000078a6abc35e089f0b7e884c5bc86`，归档 SHA256 `59172f9a61b998288e705078daa3d3155578ae817cf78725c68222a48ab4ae3f`。

**仍待验收。** 生产 HTTP 尚未使用该固定宿主执行者。下一项接入认证 Helper 传输，按可信同伴身份登记并只读核验原操作。需要在释放原单元前保存独立终态，并为自动登记定义有界保留。缺少证据时保留原门禁。Windows 原生 Job 故障窗口、整模块对象释放、ALC 回收、隔离负载内存与生产收益分别开放。Proxy 保持 resident。


### 探测原终态封存与认证读取

**已实现且已验证。** Linux Helper 的认证 Unix 传输只接收原操作 ID，从内核对端凭据取得 UID，登记固定执行者或读取同 UID 已封存终态。调用方无法提交执行路径、单元名或目标 UID。宿主 root 在原单元释放前复核原 boot、PID、启动时刻、invocation 及正常退出状态，先同步临时文件，再以不可覆盖方式发布终态并同步目录。读取既有回执时不执行 systemd 命令，也不生成回执。

Linux 私有 systemd 经实际 AF_UNIX 以 Server 夹具账号登记操作 `188c2098-fe1e-480e-919d-3958775222b8`，原执行者 PID `2548`，invocation `1148a67ace5e49d0ba6e5904da6ad140`。运行中和封存前拒绝读取。封存后释放原单元并重启 Helper，两次同 ID 查询返回同一终态，记录字节一致。额外路径字段、root socket 调用、错误 UID、其他操作 ID、重复登记、未派发和信号退出均被拒绝，后两者未生成终态文件。原 HTTP 探测 `dafaaf37-9296-4d3a-a596-0b225cd34973` 继续保留待核验，未重派。

活跃 TUN `9122749742cd49eeb868eb5c482ba873` 与原入站 `b662e9c7-f422-4016-aa4a-c8d7010aef5c` 在 Helper 重启前后保持原配置、管理选路、回执、真实 HTTP 与分流 DNS。跨代日志确认原生应用及原配置各派发一次。独立受管 TUN 补偿 `5a4461ad7b824f9692c143a5373b91f6` 按原 job `39`、PID `513` 和 invocation `1051c8c3aa6e432697248881be2fe4c9` 核对原终态并排空租约，仅一次 StartUnit。两组专属 cgroup 均已清理，共享服务与网络文件指纹一致。

首轮读回封存终态后，夹具只检查新 Helper 日志，导致原派发计数断言失败。改为跨代日志核对后，两条私有 systemd 用例通过。Windows Release 模块边界与 Unix socket 契约回归通过。Linux 最终日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-sealed-witness-final.log` SHA256 `2292bdb417f36589930c148a92c9a975726d54795b11cf609bd558801d8b7645`，归档 SHA256 `313e7f016aef1f00abcfeffc11cce6b46b08cb99487460df96085fca85578d12`。

**仍待验收。** 生产 HTTP 派发、宿主自动封存与原门禁对账仍待接入。原单元进程范围的联合证明见下一节。当前只验证同一 boot 下原单元释放和 Helper 重启后的封存读取。Proxy 保持 resident，整模块对象释放、ALC 回收、隔离负载内存与生产收益独立待验。


### 封存前的原探测 cgroup 排空门禁

**已实现且已验证。** Helper 仅登记固定的非委派 cgroup v2 单元。原登记持久保存范围路径、inode 和挂载 ID。封存继续要求原 invocation、原 PID、启动时刻以及主进程正常退出和状态码 0。原范围仍存在时，复核同一挂载、inode 和内核 `cgroup.events` 的 `populated 0`。原范围已释放时，须能核对原挂载和原 invocation，且固定 cgroup 退出策略已报告单元退出。原记录缺范围身份、范围仍有进程或读取证据不明时保留门禁。[systemd 退出策略](https://github.com/systemd/systemd/blob/main/man/systemd.service.xml#L2656-L2676) [Linux cgroup v2 排空通知](https://docs.kernel.org/admin-guide/cgroup-v2.html#un-populated-notification)

Linux 私有 systemd 登记原探测 `692e361b-e8c3-4462-a0da-bbc4f9c10a16`，原执行者 `2554`，原 invocation `8ab1ebb28fb442e8bd66718d6813d1ba`。夹具将一个不属于原执行者子树的独立进程放进同一原 cgroup。PID 1 确认原执行者正常退出，原子树已回收。独立进程仍存活且内核报告 `populated 1`，Helper 拒绝封存与读取，未写终态。夹具以 pidfd 清理该独立进程，原范围清空且管理器报告 `exited` 后按同一原 ID 封存。释放单元并重启 Helper 后读回同一记录。仅采用主进程退出策略的单元拒绝登记，未派发与信号退出仍无法封存。

活跃 TUN `ebd32bbd143440f782b7fa217fd5aacb` 的原入站回执、配置身份、管理选路、真实 HTTP 与分流 DNS 保持。独立 TUN 补偿 `3c4604bfc93c4aa3a997993c4b737950` 按原作业 `39`、PID `510` 和 invocation `7e888ed5759443d791baa54f59fbefbe` 对账，租约排空且仅一次 StartUnit。两个专属 cgroup 均已清理，共享服务和网络文件指纹一致。Windows Release 模块边界与 Unix socket 契约回归通过。

初次实机因夹具在 Server 启动窗口过早检查 UID 而退出。改为等待预期非 root UID 后，复验通过了范围仍有进程时的拒绝断言，但在观察窗口内未等到管理器更新为 `exited`。最终实现复核内核范围身份与排空，最终两个用例通过。Linux 日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-cgroup-drain.log` SHA256 `6b20a6d2b99410f164273edf5fb6fe3bc91ef5a329966133d8d6485130b73edc`，归档 SHA256 `f332a49920a551b962348f251319484865a95ad03b3db4711c484346b68befbd`。

此轮分别确认原进程与范围退出。生产 HTTP 派发与原门禁对账见下一节。Proxy 保持 resident，整模块对象释放、ALC 回收、隔离负载内存及生产收益分别待验。

### Linux 系统模式的固定宿主派发与原门禁对账

**已实现且已验证。** 完整 Server 通过认证 Helper 为原探测 ID 派发固定非特权 systemd 单元。Helper 在启动前持久写入原派发登记。Server 在交付外部程序路径前持久保存原宿主身份，并核对专属 Unix 对端的 PID、UID 和启动时刻。原单元使用非委派 cgroup、只读系统路径和无 capability 的执行者。常规成功、合法 ELF 的失败结果和取消均通过认证 HTTP 验证。取消保持原代次租约，等待原树与两路输出排空后才归还。

在私有 Linux systemd 仅强退 Server 时，原探测 `4e028ad0-a0ff-43bd-af6c-db6c19d5028b` 的固定宿主执行者通过连接关闭结束原工作。夹具按 pidfd 核对原执行者、根进程及脱离会话的子进程均已回收。禁用启动因同一原记录被拒绝。启用重启后，首次有效探测以 503 返回，只查询原 ID。Helper 先封存原退出终态再释放单元，Server 核对原身份后删除门禁并归还请求租约。跨代 Helper 日志只记录一次该 ID 的启动，恢复请求未启动任何新探测。

反向实测强退原探测 `a02f0432-0af0-495c-91e6-45c2b83294c3` 的固定执行者。存活的 Server 保留原工作租约并隔离原 Proxy 代次。禁用启动拒绝越过原门禁。启用重启后，恢复查询仍未取得终态，原记录逐字节保持，同一原 ID 只派发一次。专属资源已清理且宿主指纹一致。反向日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-host-dispatch-unknown-final.log` 的 SHA-256 为 `090a39debb7e2d995dcbf08bd14e92f13a291d71c91d12b622f09464a7c0f925`，归档为 `72f4c1a537c611cc2ac2ef321a039bba06ceefae2d70f4dcd6df392947764a0a`。

活跃 TUN `fee7b9e7ec4d4a6c97e5ff96bde3d391`、原入站 `1d8b990b-4ef7-40d8-b5d6-c29e44b3cd1b` 与配置摘要 `2595bc4aac326f2eeed7575bc6cffeaf6fbf8e76f822c53804442474f1e63cd5` 保持。真实 HTTP、分流 DNS 和管理选路连续。独立补偿 `56b68905ec4640b3844831c1aca5633a` 按原作业 `39`、PID `510` 和 invocation `0d987250a7894df89737730dd3119081` 归还租约，只有一次 `StartUnit`。专属 cgroup 已清理，宿主共享服务、代理和 DNS 文件指纹一致。实测日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-host-dispatch-pidfd.log` 的 SHA-256 为 `e904c5aefe1786fa15c9707d863deb9567954bd34031473661ba79af1cd776a3`，归档为 `4bb567a7e40e7d458a6277d6c3cd14eca890867eae680e3a1ae40d6b1fa7b8b9`。

Windows Release 模块边界、Proxy 常驻、TUN、Helper socket、Squid 和组件专项及 Linux x64 Release 构建通过。额外全量回归在既有文件提权测试的 `RequestServices` 为空时中止。实机首轮夹具把进程退出与回收混为一项，复验改为观察原 pidfd 的回收事件。[Linux pidfd 手册](https://www.man7.org/linux/man-pages/man2/pidfd_open.2.html)

**已实现但实机未验证。** 固定宿主生产路径在同一 boot 的 Linux x64 私有 systemd 已验收。跨整机断电和 Windows 原生 Job 故障窗口需要各自独立实测。此轮确认原进程退出与回收。对象释放、ALC 回收、隔离负载内存及生产内存收益分别待验。

**未实现或仍受门禁保护。** 本轮按启用重启后的首个有效探测请求对账。宿主派发回执未到达 Server、执行路径尚未交付的实测见下一节。只有可信原终态能区分未派发与清理结果未知。宿主证据目录有容量上限，已结案记录的安全保留与回收策略待完善。Proxy 保持 resident，整模块动态驻留另行验收。

### 宿主派发应答丢失与未派发原终态

**已实现且已验证。** 固定宿主区分未派发和已执行排空的原生退出状态。Server 没有收到宿主身份时，按同一原 ID 只查询未派发终态。Helper 必须复核持久原派发登记、同 UID、boot、invocation、PID、启动时刻、原生退出状态 65 和原 cgroup 排空。先封存该类别终态，再释放原单元。另一类终态、信号退出、缺失登记或未排空范围继续拒绝确认。

在既有测试机的私有 systemd，使用相同非特权 UID 的 AF_UNIX 中继实际截留 `MihomoProbeHostStart` 的成功终帧。原操作 `78f373e9-e5c7-470e-ae8a-18318c497267`、invocation `3e245a4f-e0f0-4eaa-922f-b14448db912d`、执行者 `3137` 已登记。Server 检查点尚无宿主身份，原 cgroup 只有固定执行者，请求租约为一。只强退 Server 后，原执行者 pidfd 报告已回收，禁用启动继续拒绝同一原操作。启用重启后，首个有效探测返回 503，只查询该 ID 的未派发终态。Helper 封存原记录，Server 清理门禁并归还请求租约。重复读取保持回执字节一致，已执行并排空的原 ID `cfe8ecdb-e92c-42ca-abef-c04c7686baf4` 被未派发查询拒绝。原操作只启动一个固定单元，未派发外部程序。

受管 TUN `9dd99d55a12e49ad908bd10d4a4c5a8f` 及原入站 `5ed707fe-17e9-4ff9-8ebc-b60f563eff4b` 的配置、管理选路、真实流量及分流 DNS 持续。专属资源已清理，宿主共享服务、代理和 DNS 文件指纹一致。Windows Release 模块边界、Helper socket、Proxy 常驻及 TUN 专项通过，Linux x64 Release 构建与完整认证 Server 实测通过。日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-start-reply-cut.log` 的 SHA-256 为 `ecb1a0b2c7192f85da2293641d30633dc2edff0a62e0136abd77504b5d599b64`，归档为 `2de6f01169206cc28a8012eb0034fc532627b3dbbe050be5d95a80d3039820ae`。

**已实现但实机未验证。** 跨整机断电和其他平台故障窗口仍需独立环境。本轮确认原进程退出与回收。对象释放、ALC 回收、隔离内存差值与生产收益分别待验。

**未实现或仍受门禁保护。** Server 存活时丢失派发终帧的原代次收口见下一节。宿主登记缺失、原进程异常退出或原范围缺可信排空证据时维持原门禁。Proxy 保持 resident。

### Server 存活时丢失固定宿主派发终帧

**已实现且已验证。** Server 在宿主派发终帧丢失时关闭原执行连接，在独立清理时限和同代请求租约内只核验原 ID 的未派发终态。Helper 未确证时保留原记录。确认原固定执行者已回收及原 cgroup 排空后，Helper 按原 ID 封存并释放单元。Server 清理原门禁，请求返回 503，响应及作用域清理结束后租约归零。

既有 Linux 测试机的私有 systemd 中，非特权 AF_UNIX 中继丢弃原 `MihomoProbeHostStart` 终帧。原操作 `a73526b9-7816-41e8-b54a-d0f2100bbbb3`、invocation `74b9d120-34e8-4931-b0df-1b271ea2ecfb`、执行者 `3113` 已登记，原请求持有一份工作租约。存活的原 Server 对同一 ID 的首查仍待核验，随后读回原未派发终态。全程只启动一次固定单元，未派发外部程序。原代次仍 resident，原请求返回 503，租约归零。正常重启后重复读回同一原终态，记录字节保持。受管 TUN `611ada341d474f8a8a70180bd0771397` 与原入站 `3be348be-1ff5-4ace-849b-022bc37784b0` 保持原配置、真实上游流量及分流 DNS。专属资源已清理，宿主共享服务、代理和 DNS 文件指纹一致。

Windows Release 模块边界、Helper socket、Proxy 常驻及 TUN 专项通过，Linux x64 Release 构建通过。最终日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-start-reply-drop-final.log` 的 SHA-256 为 `c655ec330bf39794cfdaf9b2f8db800f51ef6cf59dd3ed6ee9599286e9b57bea`，归档为 `c4fff543e13833ed186007a35b902ee633d70488ac6ab2987ce9a2449d063ea0`。首轮夹具读到尚在写入的门禁文件并退出，日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-start-reply-drop.log` 的 SHA-256 为 `44720fa821d1c891f00facb5b3962aa0a725abb0eafb9b31271e7995638f618f`。复验等待完整原记录。

重启窗口回归的原 ID 为 `6854e36e-bb70-4fe8-8b8f-7aa18a7bfe4e`，再次确认原未派发终态与单次启动。回归日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-start-reply-cut-regression.log` 的 SHA-256 为 `7bc758ddc7f9ae0c1559fd83e824e553d54167f3db52651f203caacbd7df3b42`，归档为 `d92da4860285d4b04fe9f5c44e499cf0cadb0fcef694897f6108368cc29b86f6`。回归复用了上一轮本地打包路径并覆盖该文件，上一轮原日志和归档摘要仍保留。回归包已另存。

**已实现但实机未验证。** 同代有界查询无法取得可信未派发终态时，保留原门禁及清理责任。该窗口的独立实测见[原固定执行者异常退出时的同代门禁](#原固定执行者异常退出时的同代门禁)。已确认实际进程退出与回收，对象释放、ALC 回收、隔离负载内存和生产收益分别待验。

**未实现或仍受门禁保护。** 原完成查询终帧丢失后的同一请求接续见下一节。整模块动态驻留与整机掉电仍待独立证据。Proxy 保持 resident。

### 固定宿主原完成查询终帧丢失

**已实现且已验证。** 完整认证 Server 在私有 systemd 对真实 Mihomo 执行版本探测。原操作 `6417a68b-c5ed-4fc8-918e-ebbf751b0236`、invocation `c028b918-5189-4d14-9de6-127def96fe53`、执行者 `3133` 已退出，Helper 已按原 ID 封存退出状态 0 并释放原单元。非特权 AF_UNIX 中继截留首条成功完成查询终帧。此时 Server 原宿主检查点还在，原请求仍有一份工作租约。丢弃终帧后，Server 的首次查询报告 `InvalidProtocol`，随后按原 ID 重查同一封存终态。原请求返回 200 和真实版本，检查点清理，原代次仍 resident，响应和清理后租约归零。

跨进程日志确认该原 ID 只启动一个固定单元。完成查询前、重查后与正常重启后的封存记录逐字节一致。真实受管 TUN `69eaaed6b27f436eba9011ac63d091e0` 及原入站 `c0ca98e8-47f6-4722-9e7b-59aa4aa29ce0` 的配置、路由、上游和分流 DNS 保持。专属 cgroup 已清理，宿主共享服务、代理和 DNS 文件指纹一致。

Windows Release 模块边界、Helper socket、Proxy 常驻专项复跑通过，Linux x64 Release 构建及完整认证实机通过。首轮 Windows 模块边界曾在 Proxy 写入审计持久化断言处失败，复跑通过。首轮日志 SHA-256 为 `b146a020aff0eb1c00bfa9476e3c791bd1a61da47cf6504f7a9b067968a3abd8`。实机日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-complete-reply-drop.log` 的 SHA-256 为 `f7a441184994aaf89930eb233fc2026d7caae3e7ee803a493042183a62d354bf`，归档为 `39fd26cf49c649211cdc6791702ee79175909085431ae687db7b95f461d1f9d8`。

**已实现但实机未验证。** 本轮确认进程退出及清理。对象释放、ALC 回收、隔离负载内存和生产内存收益分别待验。同代未知终态门禁的独立验收见下一节。

**未实现或仍受门禁保护。** 缺少原登记、原退出及范围排空证据时维持原门禁。整模块动态驻留和整机掉电仍待独立证据，Proxy 保持 resident。

### 原固定执行者异常退出时的同代门禁

**已实现且已验证。** 在既有 Linux 私有 systemd 截留原 `MihomoProbeHostStart` 的成功终帧后，只向原固定执行者的 pidfd 发送 SIGKILL，确认原进程已回收，再丢弃原 IPC 终帧。原操作 `8aa3c107-b9ca-4bb9-bca7-e9f9dce69e6f`、invocation `d2bfd9b8-ec99-4fc0-8f75-7ec182497b51`、执行者 `3109` 已持久登记，原 cgroup 在中断前只有固定执行者。

存活的 Server 只按原 ID 查询，始终无法确认未派发终态。原代次进入 faulted 并保留一个工作租约，原待核验记录逐字节保持，新探测返回 503。禁用启动拒绝越过该原记录。启用重启后，触发对账的请求仍返回 503。原记录保持，Helper 未封存终态，全程仅一次宿主启动。活动 TUN `1dccc19365ae4637af62a77e1a83cbcb` 及原入站 `63859810-d4fb-4a68-bd83-a478518a63dc` 的配置、真实流量和分流 DNS 连续。专属 cgroup 已清理，宿主共享服务、代理及 DNS 文件指纹一致。

Windows Release 模块边界、Helper socket 和 Proxy 常驻专项通过。Linux x64 Release 完整认证实测日志 `.artifacts/mb04-tun-full-zero-dns/linux-external-version-start-reply-unknown.log` 的 SHA-256 为 `28337dfe57d6b13f5a68d92b225fc989caf4d5e845635d6ef59b867119d1f2d5`，归档为 `6906c02d8d825a6fb2b0c610361590dd1ce2dfea25d9a428477357efd302e7e2`。

**已实现但实机未验证。** 整机掉电与其他平台故障窗口需独立环境。此轮验证实际进程退出和回收。对象释放、ALC 回收、隔离负载内存和生产收益分别待验。

**未实现或仍受门禁保护。** 信号退出或缺少可信原登记时保留待核验。整模块安全交接及已结案见证的长期回收策略继续开放。Proxy 保持 resident。

### TUN 与恢复状态的管理响应租约

**已实现且已验证。** 管理 GET `/api/v1.0/proxy/tun` 与 `/api/v1.0/proxy/recovery` 在当前 Proxy 代次取得请求租约并借用 Core 的同一安全服务。租约覆盖实际读取、响应和作用域清理。Linux 私有 systemd 的完整认证 Server 在活跃发布 `d93bba41f19e4d9c9f5c04bd42005bf3` 与具名启动 `198c9c774200425b87ec7cbbfb7087cd` 下并行执行两条读取。两次真实 Helper 查询已完成，两个 HTTP 响应在发送前暂扣，当前代次持有两项租约。放行后两个 200 响应相同，租约归零。原队列、原 Helper 台账和配置镜像逐字节保持，原管理选路、真实 HTTP 及分流 DNS 保持。

原具名停止 `80884c02c74a497c88243affd92499fb` 的清理故障隔离原代次后，两条管理读取都返回 `503 / module-load-failed`，Helper 查询计数未增加。模块诊断仍显示 `module-recovery-blocked`，原队列和原 Helper 台账保持，停用申请与禁用启动拒绝越过原门禁。启用重启按原停止 ID 只读结案，后续具名启动恢复真实 HTTP 和分流 DNS。原停止只派发一次。

Release 25 项注册、38 项 TUN 边界、48 项 Core 安全、37 项工作作用域和 31 项驻留检查通过。两轮专属进程与 cgroup 已清理，宿主共享运行时、代理和 resolver 指纹一致。正向日志 `.artifacts/mb04-tun-full-zero-dns/linux-tun-status-scope.log` SHA-256 为 `490b95998d55e21101f083ed6d0452dbac36391237ba06efe85973614b997960`，归档为 `30d9e1717a455b085e1cc834440806b1bd758c205cf2c1c7395b958da9450ac4`。故障复验日志 `.artifacts/mb04-tun-full-zero-dns/linux-tun-status-fault-retest.log` SHA-256 为 `2925901e6019a1d739335e905a189116b7303e38716e31174ad406cdb74c1f03`，归档为 `6c8c872850edd5083ff46fa215895ae1a814c6ecd61e15e6446ea89f65c08eb3`。早期反向夹具混淆诊断与 HTTP 错误码，随后新增循环变量遮蔽路由核验函数，均修正后复验通过。

**已实现但实机未验证。** 本轮确认专属进程退出。故障对象释放、整模块 ALC 回收、隔离负载内存和生产收益分别待验。

**未实现或仍受门禁保护。** 整模块安全交接仍开放，Proxy 保持 resident。

### 运行时概览与组件状态的只读发布投影

**已实现且已验证。** 概览和组件状态使用独立只读投影，叠加原 Server 发布台账与 Helper 的轻量原生归属查询。组件归档规划仅在实际规划时解析原变更服务。Windows Release 通过 26 项注册、113 项原发布、60 项运行时 HTTP，以及组件、Core 安全、TUN 边界和模块里程碑回归。专项保留 Server 待核验 ID、冲突的 Helper ID 及台账不可读时的未知所有权。

Linux 专属 systemd 的完整 PAM/JWT Server 在具名启动 `03acd4a1-d431-446f-9f44-9fdc73058836` 原恢复应答丢失后重启。选路漂移期间，按原 ID 和同键读回同一成功终态，概览仍报告当前恢复门禁，组件状态与原安装投影一致。恢复选路后，概览和组件状态报告相同版本，当前恢复门禁解除。两组读取都发出轻量 Helper 查询，安装清单查询计数保持。原发布 `ed0d474e-f8b2-4b61-b9d0-7ac15d17bfcc` 保留原 Mihomo PID `945`、原选路、配置镜像、真实 HTTP 和分流 DNS。一次原对账，重启后未重派。独立禁用夹具继续核对 Core guard、受管 TUN 和授权到期责任，原 Mihomo PID `422` 跨 Server 重启保持。两轮均确认专属进程退出及 cgroup 清理，宿主共享服务、代理与 resolver 指纹一致。

首轮夹具错误地要求复用的 Helper 日志从未出现安装清单查询。修正后按两条路由调用前后的计数差验收。首轮日志 SHA-256 为 `abdd8a417f9fbe7fc7a94ec864c81fbfdb22eff8b2d7eb48e7a7f6f7bf88c907`。最终日志 `.artifacts/mb04-tun-full-zero-dns/linux-publication-observation-final.log` 的 SHA-256 为 `3d17d0827955e90bedf69931ddf777f10bcad85524528b52ab46796dec1ee4d8`，归档为 `d597ca186ecd4251a95620ad4b005ffe06190237d1ff02dae2927b24d1b5c3d8`。

**已实现但实机未验证。** 原应答中断矩阵在 Linux 执行，Windows 的同类故障窗口待独立核验。已记录进程退出，对象释放、ALC 回收、隔离负载内存和生产内存收益分别待测。

**未实现或仍受门禁保护。** 安装详情、恢复状态及 GEO 资源的只读 HTTP 入口仍解析变更服务。整模块安全交接继续开放，Proxy 保持 resident。
