# RelaxKonOS 优化检查与进度台账

更新日期：2026-10-07。范围：首轮 7 项优化的实现、回归与容量测量，以及 Chrome 风格桌面浏览器 UI（OPT-008）和地址解析崩溃修复（OPT-009）。10 项代码/文档改动均已落实；真实 SSH 与原生 WebView 平台验收仍有明确未完成项。Android 详细记录继续由 [Android 自有文档](../../Client/RelaxKonOS.Client.Android/docs/README.md) 管理，本次没有修改 Android 专项实现。

## 1. 状态与维护规则

- 状态：待处理 → 处理中 → 待验收 → 已完成；无法继续时记录阻塞原因。
- 优先级：P1 优先处理数据一致性、敏感信息和取消语义；P2 改善资源消耗、并发及维护成本。
- 代码依据成立不代表线上问题已复现；性能收益必须通过前后对照测量确认。
- 每次实施保留条目 ID，填写变更、验证命令与结果、剩余限制。只有验收条件满足后才标记已完成。
- 内置接口升级直接更新协议、实现、调用方、测试和文档，不引入兼容别名或双格式解析。
- 已有专项文档继续作为专项实现的真源，本台账仅提供入口和检查发现。

## 2. 总览

当前共 10 项：待处理 0、处理中 0、待验收 2、已完成 8。待验收项均已完成实现和可在当前环境执行的检查，未把缺少的真实宿主验证记为通过。

| ID | 优先级 | 最终改动 | 当前证据 | 状态 |
|---|---|---|---|---|
| OPT-001 | P1 | 原子归属校验、双索引同步、返回快照 | 仓储并发与 HTTP 跨用户回归通过 | 已完成 |
| OPT-002 | P1 | JSON/引号/空格值净化、截断前净化 | 摘要与异常消息注入回归通过 | 已完成 |
| OPT-003 | P1 | SSH 与 SFTP 原生异步取消、剩余字节进度 | 编译与启动前取消通过；真实传输待验收 | 待验收 |
| OPT-004 | P2 | 有界分页、精确 URL 查询、侧栏按需加载 | 1 千/1 万条容量、HTTP 契约及 UI 回归通过 | 已完成 |
| OPT-005 | P2 | WAL 读写分离、详情快照、原子保留、游标修复 | 混合负载、分页与存储回归通过 | 已完成 |
| OPT-006 | P2 | 清除兼容期要求，更正当前测试状态 | 文档与当前代码核对完成 | 已完成 |
| OPT-007 | P2 | 身份提供方与 Windows SID 的平台边界 | 服务端测试项目 0 警告、0 错误 | 已完成 |
| OPT-008 | P2 | Chrome 风格工具栏与真实标签状态 | 18 组布局及截图通过；原生适配器待验收 | 待验收 |
| OPT-009 | P1 | 地址解析抛出 UriFormatException | 19 类输入与浏览器布局回归通过 | 已完成 |
| OPT-010 | P2 | 图标居中与 Chrome 风格收藏/历史右侧栏 | 实际居中、菜单动作、日期边界和多主题布局回归通过 | 已完成 |

## 3. 首轮发现与验收条件

本节保留修复前的检查依据和最初验收范围；当前实现、结果及未完成验证以 §2、§5–7 为准。

### OPT-001：浏览器内存仓储归属与索引一致性

依据：[BrowserRepository.cs](../../RelaxKonOS.Server/Storage/BrowserRepository.cs) 的 `DeleteBookmark`、`DeleteHistory` 使用 `TryRemove` 后再比较 `UserId`。另一用户传入现有 ID 时返回 false，但 ID 索引已经改变，URL 索引仍保留条目，导致所有者后续按 ID 删除失败。SQLite 实现先按 ID 和用户共同查询，不存在这个具体路径。

建议：让归属检查和删除保持原子性，并维护两个索引的一致性；同时检查新增、清空、删除并发交错，避免仅添加一次预检查而留下竞态。

验收：非所有者删除书签、历史均不改变状态；随后所有者仍可删除；重复删除结果稳定；并发操作后两个索引一致。

### OPT-002：日志摘要净化边界

依据：[ObservabilitySanitizer.cs](../../RelaxKonOS.Server/Observability/ObservabilitySanitizer.cs) 的 `SensitiveAssignment` 要求键名后直接出现空白和 `:`/`=`；`{"password":"example-secret"}` 中键名后的引号不满足规则，普通赋值值匹配也止于空白。现有 [ObservabilityChecks.cs](../../RelaxKonOS.Server.Tests/ObservabilityChecks.cs) 覆盖普通赋值、Bearer 和 URL 凭证，尚未覆盖上述输入。

建议：补充可复现用例后修复净化边界，明确 JSON、带引号值、空格、转义和大小写语义；核对所有调用该摘要净化器的日志与审计出口。

验收：注入测试 secret，摘要与异常摘要均不保留其任何完整敏感值；原有 Bearer/URL 用例通过；覆盖截断前净化、长输入和正常文本。此处不宣称已证明所有 sink 存在泄露。

专项入口：[后端可观测性设计](../architecture/RelaxKonOS.BackendObservability.Goal.md)。

### OPT-003：SSH 执行和文件传输取消

依据：[ServerCenterSshTransport.cs](../../Client/RelaxKonOS.Client/Services/ServerCenter/ServerCenterSshTransport.cs) 的 `RunAsync`、`UploadAsync`、`DownloadAsync` 将同步 SSH.NET 调用包在 `Task.Run(..., cancellationToken)` 中，没有在已开始的同步操作内部接入取消。传给 Task.Run 的 token 可以阻止尚未开始的任务，但不能中断正在运行的同步调用。`RunWithInputAsync` 已使用 `ExecuteAsync(cancellationToken)`，可作为检查方向。

建议：检查当前依赖支持的异步/取消接口；明确取消、超时、断连、资源释放和部分上传文件处理。取消命令等待不等于远端安装已停止，需要核对任务状态。

验收：隔离 SSH 环境中对长命令及大文件上传/下载，在启动前与进行中分别取消；记录取消延迟，确认 UI 退出等待、资源释放和远端状态可解释；密码输入流程无回归。

### OPT-004：浏览器列表容量边界

依据：[BrowserEndpoints.cs](../../RelaxKonOS.Server/Endpoints/BrowserEndpoints.cs) 全量返回书签；历史 `limit=0` 原样传入仓储。[SqliteBrowserRepository.cs](../../RelaxKonOS.Server/Storage/Sqlite/SqliteBrowserRepository.cs) 与内存实现均把 `limit<=0` 解释为无界返回。正常正数历史查询已有 SQL LIMIT，历史也已有 `(UserId, LastVisitedAt)` 索引，不应重复建议增加相同索引。

建议：统一历史 limit 合法范围，评估书签分页与 UI 加载策略。接口改变时同步共享协议和全部客户端，直接采用新契约。

验收：负数、零、默认、上限及超上限行为一致；跨用户隔离保持；以 1 千/1 万条数据记录响应体、分配量、延迟和 UI 加载时间，再决定分页收益。

### OPT-005：事件告警读写并发

依据：[EventAlertStore.cs](../../RelaxKonOS.Server/EventAlerts/EventAlertStore.cs) 的列表读取与追加等操作共用 `_gate`。当前列表已经采用 SQL LIMIT 和游标分页；需要评估的是并发门控，而非把分页改为内存实现。

建议：先测量混合读写负载和门控等待时间；只有确有瓶颈再评估 WAL、读写分离或更细门控。保留 schema 初始化、事务投影、去重及游标一致性约束。

验收：同一数据规模下比较 P50/P95/P99 延迟与写入吞吐；重复事件去重、状态转换、取消、保留清理和多页查询测试通过。未测量前不指定收益目标，也不直接移除锁。

### OPT-006：文档状态与仓库策略对齐

依据：[任务管理器重写](../applications/RelaxKonOS.TaskManager.Rewrite.md) Goal 1、Goal 8 仍要求记录兼容期限和兼容期后删除旧路径，与根 `AGENTS.md` 的发布前直接升级策略冲突。[后端可观测性设计](../architecture/RelaxKonOS.BackendObservability.Goal.md) 2026-09-25 记录称 `TestAssert.Equal/True` 不存在；当前 [TestAssert.cs](../../RelaxKonOS.Server.Tests/TestAssert.cs) 已实现两者，且本次测试项目已构建成功。历史记录应保留日期，并追加当前状态，不把历史失败当作当前失败。

建议：清理当前实施要求中的兼容期，追加带日期的状态更正，并链接仍未覆盖的净化问题 OPT-002。

验收：当前目标不再要求兼容 shim；原历史事实与本次构建事实区分清楚；文档不在未运行测试时宣称回归全绿。

### OPT-007：平台支持分析警告

依据：本次构建出现 CA1416：`ServerProcessIdentity.cs:63` 的 `WindowsSid`，`Program.cs:515` 的 `WindowsLogonProvider`，`Program.cs:517` 的 `LinuxPamProvider`。

建议：逐项核对运行时平台分支和支持属性，让分析器能够验证真实平台边界，避免全局屏蔽警告。

验收：相同构建命令无上述警告；Windows/Linux 身份提供方选择和错误平台拒绝行为通过对应检查。

## 4. 首轮检查记录（实施前）

### 2026-10-07：首轮检查完成

- 初始 `git status --short` 为空。
- 已检查浏览器端点及两类仓储、SSH transport、事件告警存储、摘要净化器及相关测试、专项设计和文档索引。
- SDK：`dotnet --version` 为 `10.0.300`。
- 初次常规构建未成功；普通日志显示编译服务器命名管道 `UnauthorizedAccessException`。这属于本地执行限制，不能据此判断源码无法编译。
- 关闭共享编译并串行构建后成功：`dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v minimal`，0 错误、3 个 CA1416 警告。
- 尚未运行测试可执行程序、完整 solution 构建、Android 验证、性能基准或真实 SSH 验收。上述未执行项不计为通过。

首轮建议顺序为 OPT-001 → OPT-002 → OPT-003，随后文档/平台边界与容量测量；现均已进入实施与验收记录。

后续记录格式：日期、条目 ID、状态变化、变更文件或提交、验证命令与结果、剩余限制。每次更新同时维护 §2 状态计数。


## 5. 2026-10-07 实施记录

- **OPT-001**：内存实现用同一锁维护 URL/ID 两个字典，归属不匹配时不修改状态；新增、删除、清空、访问计数均在锁内执行。返回对象为快照，避免调用方或后续写入改变已返回结果。SQLite 与内存仓储均验证拒绝跨用户删除后所有者仍可删除，并覆盖混合并发写入。
- **OPT-002**：匹配带引号敏感键、单/双引号值、转义、未闭合引号和含空格的普通赋值。普通赋值保守删除到逗号/分号/换行；负摘要长度拒绝，长输入先净化后截断。原有 Bearer/URL 用例与新增摘要/异常用例均通过。未将专项测试扩大解释为所有日志 sink 已审计。
- **OPT-003**：`RunAsync` 改为 `SshCommand.ExecuteAsync(token)`；上传、下载直接使用 SSH.NET 2026.0.0 的 `UploadFileAsync`/`DownloadFileAsync` 传递 token。可定位流的进度分母使用 `Length - Position`，无可靠分母时继续不伪造百分比。启动前取消不访问流；真实传输验收见 §7。
- **OPT-004**：新增共享 `BrowserQueryLimits`，默认 100、上限 500、非正值归一为 1。书签/历史都在查询内分页并用 ID 稳定打破排序相同项；书签支持精确 URL 筛选。服务端、接口、typed client、VM 和协议文档同步升级，不保留旧无限返回语义。VM 提供加载更多、本地写入后重载首屏与过期响应丢弃；分页未加载的书签仍可正确显示星标和删除。
- **OPT-005**：保留串行写锁，增加独立 schema 初始化锁；数据库使用 WAL，列表/汇总读独立连接，详情用 deferred 事务保证多次读取在同一快照，保留作业整体提交。基准发现 GUID 游标字符串与 SQLite Guid 参数表示不同导致重复翻页，现将游标 ID 重新作为 Guid 参数绑定；事件与告警全页遍历通过。
- **OPT-006**：任务管理器重写目标直接替换旧契约，不再要求兼容期；可观测性文档追加当前测试状态与净化证据。验证中发现原有证书布局测试仍使用过时构造与 Expander 假设，已改为当前三回调构造和独立历史入口验收，原布局套件通过。
- **OPT-007**：`MatchesWindowsSid` 声明 Windows 支持属性；身份提供方注册工厂内部也检查平台，分析器不再报告原来的 3 个 CA1416。未增加全局警告屏蔽。
- **OPT-008**：浏览器改为顶部标签栏、单行导航栏、圆角地址框、星标与三点菜单；支持创建/切换/关闭标签、快捷键和新标签入口。标签保存独立地址、加载、前进/后退状态；原生 surface 按需创建，切换不重新导航。侧栏默认关闭并限制最大占比；菜单/设置显示时隐藏 native surface。保留系统主题及中英日资源，移除固定 700px 地址框和对所有 URL 展示固定锁标记的做法。见 [浏览器文档](../applications/RelaxKonOS.Browser.md)。

## 6. 当前验证与测量

构建统一采用 `--no-restore -m:1 -p:UseSharedCompilation=false`；当前 SDK 为 10.0.300。

| 验证 | 命令/入口 | 结果 |
|---|---|---|
| 服务端测试项目编译 | `dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v quiet` | 0 警告、0 错误 |
| 优化与 HTTP 专项 | `dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --optimization-only` | 通过；包含 JWT/分页/跨用户删除、净化、并发索引、WAL 快照、首次初始化、取消、保留与游标遍历 |
| 布局测试项目编译 | `dotnet build Tests/Client/RelaxKonOS.ApplicationLayout.Tests/RelaxKonOS.ApplicationLayout.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v quiet` | 成功；客户端仍有 5 个其他模块警告（CS8604、MVVMTK0034、3 个 AVLN3001） |
| 完整布局套件 | `dotnet Tests/Client/RelaxKonOS.ApplicationLayout.Tests/bin/Debug/net10.0/RelaxKonOS.ApplicationLayout.Tests.dll` | 105 个原页面布局检查通过，额外 18 个浏览器组合通过 |
| 浏览器专项入口 | 同一布局程序添加 `--browser-only` | 标签/分页/布局、菜单命令绑定与地址栏焦点通过，生成截图 |
| SSH 启动前取消 | `dotnet Tests/Client/RelaxKonOS.ServerCenter.Tests/bin/Debug/net10.0/RelaxKonOS.ServerCenter.Tests.dll --ssh-cancellation-only` | 4 类操作取消通过，流未被读取或写入 |

HTTP 专项在临时目录、随机 JWT 和本机回环地址运行；沙箱阻止套接字连接时，本次通过批准的沙箱外测试完成验证。测试临时数据库在退出时清理。未运行完整服务器冒烟程序、完整 solution 构建或 Android 测试。

### 本机容量数据

SQLite 书签测试分别创建 1 千和 1 万条数据。旧行为用相同 EF 全量读取查询重现，对照当前 100 条首屏查询；查询均预热。分配量为当前线程查询分配，响应体为对应 DTO JSON 的 UTF-8 字节数，不含 HTTP 压缩和网络时间。

| 总条数 | 全量查询耗时 | 全量分配 | 全量响应体 | 100 条查询耗时 | 分页分配 | 分页响应体 |
|---|---:|---:|---:|---:|---:|---:|
| 1,000 | 1.65 ms | 799,664 B | 193,790 B | 0.53 ms | 102,152 B | 19,281 B |
| 10,000 | 44.89 ms | 7,963,456 B | 1,947,749 B | 2.84 ms | 102,152 B | 19,279 B |

实际 Avalonia 首屏构建/布局（内存 transport fixture，不含网络）分别为 39.98ms 和 18.02ms，两种规模都只装载 100 条书签及 100 条历史；后一次受到预热影响，不能解释为数据越多越快。单独启动浏览器专项时冷启动样本为 371.85ms / 44.42ms，进一步说明初始化成本与预热会主导这项数值，不能作为远程加载耗时保证。

事件混合负载：1 千种子事件，8 个 worker，每个执行 100 次操作，其中 1 个写入、7 个读取，每次读取 100 条。优化前写门控与默认 journal 模式对照优化后 WAL/独立读取，均在同一机器与 SDK 执行。

| 阶段 | 800 次操作耗时 | P50 | P95 | P99 | 混合窗口写入吞吐 |
|---|---:|---:|---:|---:|---:|
| 优化前 | 3,159.7 ms | 5.40 ms | 275.25 ms | 583.77 ms | 31.6 次/s |
| 优化后最终回归 | 163.0 ms | 1.37 ms | 2.36 ms | 7.55 ms | 613.6 次/s |

优化前负载测量完成后，旧游标分页断言失败，暴露已修复的 GUID 比较问题，因此旧版本未通过整套专项检查。以上是单机诊断样本，不是生产性能保证。

### 布局证据

实际 Avalonia headless 渲染，已人工检查亮色 1100px 与暗色 640px 截图；不包含真实网页或原生适配器。

- [亮色浏览器](./optimization-evidence/Browser-Light-1100.png)
- [暗色窄窗口](./optimization-evidence/Browser-Dark-640.png)

## 7. 未完成的宿主验收

- **OPT-003**：需要隔离真实 SSH 主机，验证长命令、大文件上传/下载在进行中取消的延迟、资源释放、部分上传文件状态、密码输入与远端安装任务状态。取消等待不等于确认远端任务已停止。
- **OPT-008**：需要 Windows/macOS 原生适配器检查网页加载、同 URL 多标签、切换保留后退/前进、关闭资源释放、菜单/设置覆盖与窗口拖动；Linux 确认系统浏览器委托模式下切换标签不重复启动浏览器。

这两项在上述证据补齐后才更新为已完成；不需要再做未完成的实现计划。Android 仍由其自有状态文档记录专项验证。


## 8. 2026-10-07：Ubuntu 测试服务器部署

用户确认 DHCP 变更后的实际地址为 `192.168.1.9`，SSH 指纹经服务器终端核对一致；以固定主机指纹连接。原服务实际运行于 `/opt/relaxkonos/settings-test-20261006/`，安装状态仍记为 `0.2.1`，此次升级使用仓库 System Mode 引擎，并以既有 installationId 校验目标。

- 来源提交：`d4e6bba71d27357641c7abdb2f9a7b372f0a880f`，包含本台账优化与浏览器改动。
- 发布版本：`0.3.0-opt-20261007-d4e6bba7`，Linux x64，自包含 Server/Guardian/Helper，816 个清单文件；本地 ReleaseVerifier 与远端逐文件校验通过。
- ZIP SHA-256：`a40445b63a1a4b3f7ceffdaba4b0ea9e5c5a506fa453d5254d66ca086ffaf3f6`。
- 当前目录：`/opt/relaxkonos/versions/0.3.0-opt-20261007-d4e6bba7/`；`current` 指向此版本，Server 实际进程路径已核对。
- 数据、环境及服务配置已备份至远端 root 私有目录 `/var/backups/relaxkonos-upgrade-20261007-d4e6bba7/`；备份前数据规模 50,832,763 B，原测试构建保留。
- JWT、审计 HMAC/实例标识、数据库路径和原 TLS 证书的保留核对通过，凭据与密钥值未保存到仓库或输出报告。
- Server 与 Guardian 均为 `active/running`，ExecMainStatus 为 0；三组件 DLL 的摘要与本地发布文件完全一致。
- 宿主回环 `/healthz` 返回 `healthy`；局域网 `https://192.168.1.9:5000/api/v1.0/server/host-operating-system` 返回 Ubuntu，未登录的书签 API 返回 401。回环健康路径在 LAN 返回 404 属于既有访问边界。

服务端部署完成不代表 §7 的客户端原生 WebView 或进行中取消验收已经完成。浏览器 UI 随桌面客户端发布，需要运行新版客户端才能显示。


## 9. 2026-10-07：浏览器地址栏崩溃修复（OPT-009）

用户报告地址栏按回车时，`NormalizeAddress` 在 `new Uri("https://" + input)` 抛出 `UriFormatException`，异常经导航命令和 KeyDown 传播到 UI 线程。所有输入生成的 URI 现改用 `Uri.TryCreate`；非法显式 URL 不再重复添加 scheme，而是返回无效地址状态，保留当前标签地址与导航源。

同时修正 `localhost:port` 和 `domain:port` 被识别为不透明 URI scheme 的歧义，处理 IPv6 loopback，并将包含制表符或换行的普通文字按搜索查询编码。既有绝对地址与域名 HTTPS、loopback HTTP 和搜索行为保留。

验证：`RelaxKonOS.ApplicationLayout.Tests --browser-only` 通过，覆盖 5 类正常地址、4 类搜索文字与 10 类非法/空输入；包含不完整 IPv6、非法端口和非法显式 scheme。额外 18 个浏览器语言/主题/宽度组合继续通过。客户端构建仍有原先其他模块的 5 个警告，无新增编译错误。此修复位于客户端，不涉及已部署服务端。


## 10. 2026-10-07：收藏/历史面板与图标居中（OPT-010）

- 浏览器图标按钮水平/垂直居中，更多及收藏/历史入口改用矢量图形；实测按钮与图形中心一致。
- 收藏/历史位于右侧，标题下拉切换分类，支持关闭、条目菜单、清空和继续加载；书签展示标题/域名，历史按日期分组并展示时间。
- 行菜单的新标签页打开、复制完整 URL、删除分别对书签和历史回归；删除或替换访问记录后，日期标题边界仍正确。
- 3 种语言、2 种主题、1100/640/480px 布局通过；完整布局套件 105 个原页面检查通过，之前地址解析崩溃检查继续通过。未更改服务端接口或重新部署服务端。
- 本次行为与截图见 [浏览器文档](../applications/RelaxKonOS.Browser.md)。原生平台验收继续按 OPT-008 的未完成清单执行。
