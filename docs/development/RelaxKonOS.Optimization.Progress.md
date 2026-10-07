# RelaxKonOS 优化检查与进度台账

更新日期：2026-10-07。范围：服务端、桌面客户端及开发文档的首轮静态检查与服务端测试项目构建。此轮不是全仓安全审计或性能基准测试；Android 专项检查尚未开展，其详细记录应放在 [Android 自有文档](../../Client/RelaxKonOS.Client.Android/docs/README.md)。

## 1. 状态与维护规则

- 状态：待处理 → 处理中 → 待验收 → 已完成；无法继续时记录阻塞原因。
- 优先级：P1 优先处理数据一致性、敏感信息和取消语义；P2 改善资源消耗、并发及维护成本。
- 代码依据成立不代表线上问题已复现；性能收益必须通过前后对照测量确认。
- 每次实施保留条目 ID，填写变更、验证命令与结果、剩余限制。只有验收条件满足后才标记已完成。
- 内置接口升级直接更新协议、实现、调用方、测试和文档，不引入兼容别名或双格式解析。
- 已有专项文档继续作为专项实现的真源，本台账仅提供入口和检查发现。

## 2. 总览

当前共 7 项：待处理 7、处理中 0、待验收 0、已完成 0。此次完成的是检查及追踪文档建设，未修改运行时代码。

| ID | 优先级 | 检查发现 | 证据性质 | 状态 |
|---|---|---|---|---|
| OPT-001 | P1 | 内存浏览器仓储在归属校验前删除 ID 索引 | 静态确认 | 待处理 |
| OPT-002 | P1 | 摘要净化未覆盖 JSON 引号键和含空格的敏感值 | 静态确认，待回归复现 | 待处理 |
| OPT-003 | P1 | SSH 同步操作启动后缺少取消接入 | 静态确认，待隔离环境验证 | 待处理 |
| OPT-004 | P2 | 浏览器列表存在无界返回路径 | 静态确认，收益待测量 | 待处理 |
| OPT-005 | P2 | 事件告警读取与写入共用串行门控 | 静态确认，收益待测量 | 待处理 |
| OPT-006 | P2 | 专项文档的兼容期与测试缺陷描述过时 | 文档与当前代码对照 | 待处理 |
| OPT-007 | P2 | 服务端构建存在 3 个平台分析警告 | 本机构建确认 | 待处理 |

## 3. 条目与验收条件

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

## 4. 检查与验证记录

### 2026-10-07：首轮检查完成

- 初始 `git status --short` 为空。
- 已检查浏览器端点及两类仓储、SSH transport、事件告警存储、摘要净化器及相关测试、专项设计和文档索引。
- SDK：`dotnet --version` 为 `10.0.300`。
- 初次常规构建未成功；普通日志显示编译服务器命名管道 `UnauthorizedAccessException`。这属于本地执行限制，不能据此判断源码无法编译。
- 关闭共享编译并串行构建后成功：`dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -v minimal`，0 错误、3 个 CA1416 警告。
- 尚未运行测试可执行程序、完整 solution 构建、Android 验证、性能基准或真实 SSH 验收。上述未执行项不计为通过。

推荐实施顺序：OPT-001 → OPT-002 → OPT-003；随后处理 OPT-006/007，并为 OPT-004/005 建立测量基线。

后续记录格式：日期、条目 ID、状态变化、变更文件或提交、验证命令与结果、剩余限制。每次更新同时维护 §2 状态计数。
