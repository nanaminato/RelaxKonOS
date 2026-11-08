# MB-05 证书、ACME 与 Kestrel 验收

真实隔离 CA、签发与续期、撤销、ARI、Kestrel 链交付与原订单观察。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [52. MB-05 真实 ACME CA 签发与取消](#52-mb-05-真实-acme-ca-签发与取消)
- [MB-05 真实 CA 挑战中的操作进程崩溃](#mb-05-真实-ca-挑战中的操作进程崩溃)
- [CA 已签发后的响应丢失](#ca-已签发后的响应丢失)
- [MB-05 证书自动续期跨日接续](#mb-05-证书自动续期跨日接续)
- [MB-05 证书 CA 传输中断与原回执](#mb-05-证书-ca-传输中断与原回执)
- [MB-05 原 CA 订单的只读对账](#mb-05-原-ca-订单的只读对账)
- [MB-05 完整认证 Server 的 CA 断流与重启](#mb-05-完整认证-server-的-ca-断流与重启)
- [MB-05 证书续期持久扫描与执行作用域](#mb-05-证书续期持久扫描与执行作用域)
- [MB-05 ACME 原订单观察的 HTTP 工作责任](#mb-05-acme-原订单观察的-http-工作责任)
- [MB-05 ACME SDK 独立执行程序集](#mb-05-acme-sdk-独立执行程序集)
- [MB-05 ACME 撤销确认与本地恢复](#mb-05-acme-撤销确认与本地恢复)
- [MB-05 ARI 断流与原扫描恢复](#mb-05-ari-断流与原扫描恢复)
- [MB-05 Kestrel TLS 轮换与撤销连续性](#mb-05-kestrel-tls-轮换与撤销连续性)
- [MB-05 Kestrel 动态证书链](#mb-05-kestrel-动态证书链)
- [MB-05 切换 CA 时保留原订单身份](#mb-05-切换-ca-时保留原订单身份)

## 52. MB-05 真实 ACME CA 签发与取消

既有 Linux 测试机在无外网接口的私有网络命名空间运行 [Pebble 2.10.1 与挑战 DNS 官方制品](https://github.com/letsencrypt/pebble/releases/tag/v2.10.1)。两份下载归档通过官方发布 SHA-256 校验。临时 HTTPS 证书仅由本次进程信任，签发根仅用于链校验。保留真实 HTTP-01 验证，未开启跳过挑战验证。生产 Anvil 适配器、HTTP-01 listener、证书存储和操作台账参与执行。

签发操作 `2871c43c-2d29-4543-bc3e-bada02becc84` 保存活动证书，部署链通过专属 CA 根与主机名校验。第二个订单的真实 HTTP-01 GET 到达后，夹具暂停响应。取消时工作租约和 Running 回执保留，直到实际响应任务结束才记录 Cancelled。复开台账后，同键返回原操作 `95543702-fb76-4d31-a964-cdd867c1ed6e` 的回执，未签发取消订单。Pebble 确认总计两个订单。共享服务和宿主信任库持续原状。

Linux x64 Release 日志见 `.artifacts/mb05-acme-verified.log`，归档 SHA-256 为 `0a07730143f476e9e5de19cdec8d02abf046ebb166b602184996aa93026e3aab`。首轮两份二进制缺执行位，后续仅在专属解包目录修正后通过。整进程崩溃后的 CA 未知结果、实际 Nginx 绑定及网站发布联合矩阵仍开放。

### MB-05 真实 CA 挑战中的操作进程崩溃

同一私有网络中的 Pebble 发起第三个订单的真实 HTTP-01 请求。专属操作进程在等待响应时保留 Running 回执。父进程读取原持久台账确认该操作是唯一活动项，再强制终止工作进程并确认异常退出。HTTP-01 端口随后能重新绑定。新工作 owner 读取原台账，将原 operationId 持久化为 Failed / interrupted。相同幂等键返回同一回执，CA 日志要求恰有三个订单，恢复不提交新订单。

本项故障发生在真实 HTTP-01 验证期间，仍需验证 CA 已签发但客户端丢失最终响应，以及生产 Nginx 绑定和网站发布联合恢复。

最终 Linux x64 Release 回归通过。崩溃组原操作为 `65cf6873-5791-4866-bf8c-acfb1deaea3b`，原证书目录只有正常签发组证书。Pebble 最终记录该崩溃订单验证失败，未收到第四个订单。日志见 `.artifacts/mb05-acme-crash-final.log`，制品归档 SHA-256 为 `4994febeae9afca1a58c114466289a67a7a55a3ea13e80439c96459da6a9af3e`。

### CA 已签发后的响应丢失

在同一隔离 Linux 环境中，第四个订单通过真实 HTTP-01。Pebble 签发证书后，夹具在真实证书下载响应交给 Anvil 前暂停，验证返回的 PEM 与测试主机名匹配。工作进程仍持有 Running 回执。终止该进程后，挑战端口可重新绑定。新进程将原操作 `034517e3-15aa-48df-bad2-381590d26bdb` 持久化为 Failed / interrupted，同键查询返回原回执。证书存储只包含正常签发组的活动证书，Pebble 日志恰有四个订单。

Linux x64 Release 实机日志为 `.artifacts/mb05-acme-response-live.log`，归档 SHA-256 为 `c887deec74b97eebdb3ca21f3d13ca051e9d3046295dc5b4d481fbefd8e02db4`。外部 CA 已签发，本地未激活证书。该路径验证中断恢复与防重放，未验证从原 CA 订单补取并部署证书。

## MB-05 切换 CA 时保留原订单身份

**已实现且已验证。** Linux 完整认证 Server 在私有网络中使用两个独立 Pebble 进程。分别探测两个 TLS 目录和管理接口，并核对两个 CA 的签发根不同。第一个 CA 已实际签发续期，证书响应被截断后，原操作 `ca957fa1-a2f3-46b0-8e62-6b6bdf9e1035` 保留中断回执和原订单身份。

将 Server 配置指向第二个 CA 并重启，按同一 ID 观察返回 `identity-unavailable`，同键续期返回原回执。原活动叶证书指纹和操作台账字节一致。原 TLS 中继、原 CA 和第二个 CA 的请求日志均无增量。第二个 CA 的目录探针在基线中可见。恢复原目录并重启后，同一原订单返回 `valid`。原 CA 观测在客户端断开和 SIGTERM 后仍按原责任排空。同批复验缺原订单身份的 `5f7693c3-7786-4314-ad70-3165169f9849`，仍返回 `identity-unavailable`。原 CA 全程三张订单和两次签发提交，切换和查询期间没有新增。

Windows Release 证书绑定、创建回执、ACME 执行程序集及模块边界回归通过。Linux 私有环境清理后，宿主服务、账户和环境文件指纹一致。归档 SHA-256 为 `aa2484082049144c84761062da075a0c95c53d7ac8e7f7607f3514d386a1c600`，实机与本机日志在 `.artifacts/mb05-acme-cross-ca/`。

**已实现但实机未验证。** 此轮验证配置切换和恢复。原订单观察仍要求原目录与原账户密钥。其他 CA 产品及整机掉电未纳入此轮。

**未实现或仍受门禁保护。** 没有可信原订单标识时继续保护原操作。自动补取与部署仍需原证书密钥、原订单终态和本地部署授权。记录了 Server 退出、隔离服务清理和原证书保持。未测量本场景的对象释放、ALC 回收、隔离负载内存或生产收益。

## MB-05 证书自动续期跨日接续

原证书操作台账在准入锁内为自动续期查找同一证书的在途操作和未被后续成功续期替代的中断回执。原键与新的自动日期键返回原 operationId，不写新任务或发新 ACME 订单。显式授权的人工续期仍可独立提交，后续成功才解除自动续期阻止。ACME 服务同时核对授权状态，已有效的授权无需再提交挑战。

Linux x64 Release 在既有测试机的独立 Pebble CA 验证真实自动续期。首次实测在已有效授权的重复挑战处未到达签发屏障，修正客户端授权检查后，以确定性的有效授权复用复测。CA 已签发而客户端尚未接收证书时强制终止续期进程。重开原台账后，经生产 CertificateManager 提交原键和下一日键，均返回同一 Interrupted 回执 `de7400be-25b9-4cc9-8e39-0f695ed05730`，保留原活动证书指纹。整组签发、取消、挑战中断、签发响应丢失和续期共五张订单，恢复没有新增订单。日志为 .artifacts/mb05-renewal-daily-live.log，归档 SHA-256 为 3fd290056f8d217279c4c7809beab1da3e5a82ec3cbfd5f9d5d73cb41afc89ff。Windows Release 模块边界回归通过，覆盖跨日原回执、人工成功后自动续期恢复及 Docker 跨进程原回执、单次副作用。测试通过更换调度键模拟日期跨越，未改机器时钟。远端未知订单的主动对账、独立执行图及生产内存测量仍开放。

## MB-05 证书 CA 传输中断与原回执

发送订单请求后，ACME 调用遇到无法确认远端结果的传输失败时，原 Task 仍等待 SDK 返回并排空挑战清理，再以原 operationId 持久化 `certificate.operation_interrupted`。API 保留既有 Failed 状态和 interrupted 阶段。续期中断沿用原台账的跨日门禁，当前进程和重开台账均返回原回执，不进入普通失败退避或创建新订单。明确的业务失败仍走原失败路径。CA 账户准备阶段和订单之外的其他操作未纳入本项。

Linux x64 Release 在独立 Pebble 与 DNS 命名空间中通过。TLS 中继收到真实 CA 的订单创建 201 和已签发证书 200 后分别截断响应体并关闭实际连接。证书执行器保持运行，原订单操作 `8597f483-1b6a-437b-acde-7b92669a77c1` 和原证书下载操作 `a840b9a1-b4ae-43c6-9f23-4a47d9c7e163` 均以同一 ID 保留中断回执。原键、下一日键及重开台账后的新日期键均命中原回执。原证书指纹保持，续期普通失败计数为零，确认本地清理后工作租约归零。整组共七张预期 CA 订单，恢复零新增。日志为 .artifacts/mb05-acme-transport-live.log，归档 SHA-256 为 2f5b8c070d2489b6a35b860834265216f5f2ca4cad43b33069a8015e5472f9dc。Windows Release 模块边界回归通过，Docker 跨进程仍为原 Interrupted 回执和一次副作用。测试使用生产证书管理器、Anvil 和台账；完整 Server 认证入口及未知远端订单的主动查询仍开放。

## MB-05 原 CA 订单的只读对账

生产签发和续期在取得 CA 订单响应后，于提交挑战和请求签发前把原目录、账户、账户公钥指纹与订单 URI 持久化到同一 operationId 的台账。检查点失败按本地持久化故障保留工作责任。已知中断可通过认证的 `GET /api/v1.0/certificates/operations/{operationId}/acme-order` 查询，响应为原 operation 与 remoteStatus。查询核对当前 CA 目录、原账户密钥及同源 HTTPS 身份，只允许目录读取、nonce 请求与原订单的 POST-as-GET，不跟随重定向，不返回内部 URI，不修改回执、台账或当前证书。无可信检查点返回 identity-unavailable。远端 valid 表示 CA 订单状态，本地依然按原中断回执和证书版本处理。

Linux x64 Release 使用隔离 Pebble、真实 TLS 断流与生产证书管理器验证。创建响应丢失的原操作 `6d002ed8-eb47-4c10-a0b8-64c23bb19b6c` 返回 identity-unavailable。已签发响应丢失的 `6467531e-9b4d-460a-b974-3303381d5f5f` 与实际终止续期进程后的 `f65187c4-2aea-4f6f-830a-e91ff7a5afe6` 均在重开台账后观察到原 CA 订单 valid。传输断流后的查询前后台账字节一致，原证书指纹保持，下一日键继续命中原回执。整组共七张预期订单、四次预期签发提交，对账没有新增。日志为 .artifacts/mb05-acme-observe-live.log，归档 SHA-256 为 f379fec759de54010022835163f13c00815e2b0d50168b2812b13b04e162db18。Windows Release 模块边界回归通过，生产 HTTP 路由的隔离 Kestrel 测试核对原回执、禁止缓存和未知操作 404。已知订单与完整认证 Server 的联合实测、缺失订单身份的外部核实及独立执行图继续开放。

## MB-05 完整认证 Server 的 CA 断流与重启

Linux x64 Release 以私有网络、挂载及 PID 命名空间启动完整 System Mode Server。专属 Helper 只开放生产 PAM 验证及 sudoers 资格检查，生产认证、授权及证书 HTTP 路由全部参与。专属 Nginx 用实际 WebRoot 响应 Pebble HTTP-01，原活动证书签发后记录指纹。隔离 TLS 中继分别截断 CA 已签发的 200 和订单创建的 201 响应，原 Server 等待本地挑战清理并保存原中断回执后退出。

完整 Server 重启后，匿名查询返回 401，重新登录通过认证路由查询原续期 `f351872d-c279-41ab-b775-cbe29547de85` 得到 valid，原申请 `82f46e93-7950-4b63-9ee9-4dc3932ab6d7` 得到 identity-unavailable。两条同键提交均返回原回执，台账字节、原活动证书指纹保持。独立 CA 共三张订单和两次签发提交，查询和重试零新增。宿主 Nginx、Docker、Mihomo 状态与 PID，以及 /etc/hosts 和 /etc/environment 指纹前后一致。日志为 .artifacts/mb05-auth-acme-live.log，归档 SHA-256 为 2632a6b64707eccbbc11b3497a0851639cf3ed14e459d08c4c5dad73346258ae。Windows Release 模块边界回归通过，Docker 跨进程仍为原回执与一次副作用。完整认证与原订单恢复路径通过。缺少身份的外部核实、自动补取部署及独立执行图继续开放。

同制品的独立 Pebble 回归继续通过原签发、取消、进程中断、跨日和两种传输断流，七张订单和四次签发提交符合原矩阵。日志为 .artifacts/mb05-auth-acme-regression-live.log，归档 SHA-256 为 36fc3ec833fa37b4e8ccb7cea69904400d72cabe2d88368177373c5c31fd76bc。

### 缺失原订单标识的核验门槛

既有 Pebble 夹具能证明 `newOrder` 得到 201，Server 在截断响应前未取得可持久化的原订单 URI。ACME 账户订单列表可包含多张订单，也可以分页或省略无效订单。可见的同域名订单没有本地 operationId，因此当前只读查询继续返回 `identity-unavailable`。解除门禁需要原账户范围内可归因到原请求的订单标识或等价可信回执，随后由同一个证书 owner 只读核对。该门槛仍待实现，既有 CA 订单、挑战和部署事实继续由原台账保护。 [RFC 8555 账户订单列表](https://www.rfc-editor.org/rfc/rfc8555.html#section-7.1.2.1)

## MB-05 证书续期持久扫描与执行作用域

同一个续期 HostedService 从原扫描台账读取下次到期时间。每轮在进入证书执行前持久化扫描 ID，已完成扫描的下一次计划为完成后一天。重启继续原未完成扫描和固定准入键。扫描从原证书操作台账补齐已经准入但未关联的回执，即使原证书已不在目录中。原台账继续拥有续期操作、退避和未知结果门禁。关联与无操作回执的拒绝按证书去重。旧扫描历史保留，不推导缺失的关联。

等待期间 HostedService 不解析续期扫描执行图。到期先取得原模块工作租约，再创建执行作用域。扫描取消或失败时保留原扫描，作用域清理完成后才持久化完成和下次到期时间。Core TLS 选择器、证书材料与 Nginx WebRoot 职责仍由原服务持有。

Linux x64 Release 私有 Pebble 中，原续期进程在 CA 已签发、原操作已准入但尚未关联扫描时被终止。生产续期执行作用域接续扫描 `4ca2be2e-39ad-4840-8043-4f6e4fc865d3`，从原台账补回 `ef1056c8-1579-495f-b342-0b1a70cbe815` 并持久化下次到期时间。测试作用域在清理时验证扫描仍开放、工作租约仍有效。原 CA 订单只读返回 valid，原本地中断回执保持。整组七张订单、四次签发提交，恢复零新增。日志为 .artifacts/mb05-cert-schedule-live.log，归档 SHA-256 为 370623bf9106342f41dfa7d2d70a2d88ed73c974b424b1bf0f02dd1aab287d0b。Windows Release 模块边界通过，覆盖跨日原扫描、原准入键、重复关联与拒绝去重、重启后的下次到期。完整 Server 自然计时触发和宿主停机联合实测仍开放。证书模块继续 resident，独立执行程序集及生产内存测量仍开放。

## MB-05 ACME 原订单观察的 HTTP 工作责任

认证订单读取从原证书操作 owner 取得一个工作租约，涵盖台账快照、真实 CA Task、HTTP 响应和异步作用域清理。未知操作返回 404，缺少可核实的原订单身份返回 `identity-unavailable`，两条路径均无需解析 ACME 客户端。停机通过同一个 owner 排空，返回 `Cache-Control: no-store`，原回执和证书事实维持原权威。

Windows Release Kestrel 检查通过，覆盖匿名拒绝、客户端断线后的真实 Task 等待、响应及清理期间持有同一租约和台账字节一致。模块边界回归通过，Docker 跨进程原 `1ce0b949-7009-4c97-887c-9b4c44b342ae` 为 Interrupted，受控副作用计数为一。

Linux 完整 PAM/JWT Server 使用私有 Pebble、Nginx 和域名解析。中继取得真实原订单结果并暂扣响应。HTTP 断线后仍有一个租约，放行后归零。再次暂扣时发送 SIGTERM，监听关闭后原进程继续等实际 CA Task，放行后正常退出。重启按原 `cb0406e3-6622-4449-a534-4580d5f05f34` 读到原回执和 CA `valid`，缺失身份的原 `bb03eaff-cc8e-4880-a816-d38bf105c279` 仍为 `identity-unavailable`。原台账字节和活动证书指纹一致，CA 共三张订单、两次签发提交，观察与同键查询没有新增。宿主服务和文件指纹一致。

同制品独立 Pebble 回归通过签发、取消、进程中断、跨日及两种传输断流，七张订单与四次签发提交符合原矩阵。首两轮包装器分别在临时目录路径校验和框架依赖入口处失败，修正后通过，测试目录已清理。日志在 `.artifacts/mb05-acme-observation-drain/`，归档 SHA-256 为 `bb0102b661e02cab73dc82cb31b6f0c286deeee1f5e468376a8bd685c4cdf4d4`，复验启动包装器 SHA-256 为 `38e5704912726a90c5c4e6eb22924f6be4d158d126bd8b25d55b22d1123bef7b`。Certificates 保持 resident，ACME SDK 执行图、ALC 与生产资源验收继续开放。

## MB-05 ACME SDK 独立执行程序集

签发、撤销、原订单读取和 ARI 调用按需加载 bundled collectible 执行器，Anvil、Bouncy Castle 和 SDK 的 JSON 依赖从私有目录解析。Server 保持同一个账户门禁、账户元数据写入、挑战存储、续期 owner、原操作台账及证书部署权威。执行器复用原账户文件和订单身份，跨边界返回共享值或由 Server 重建的异常。持久检查点失败、挑战归属冲突和取消仍沿原责任路径处理。

执行代次保护实际 SDK Task 和挑战清理，最后一个执行调用结束后开始 30 秒闲时计时。HTTP、终态提交和未确认清理继续由原 Core 工作租约负责。账户连接池使用 Host 的框架传输，每次 SDK 调用的客户端在调用结束时处置。Windows Release 模块边界回归通过，五代检查在保留成功和失败 Task 时收集旧代次。MB-05 跨进程原 `2d0b8448-53b3-49dc-bb71-572ac12d8a9b` 为 Interrupted，受控副作用计数为一。

首轮真实 CA 验收已观察到卸载事件，随后 GC 检查发现旧代次仍存活。GC 根定位到默认上下文的类型描述缓存。Json.NET 使用的 TypeConverter 框架程序集改为从当前运行时目录加载到同一代次，缓存随代次释放。归档不携带框架私有副本，生产路径不主动触发 GC。构建守卫反向拒绝未批准引用。

最终 Linux Release 通过五代收集和真实签发 `1e845d1b-cd25-4e4e-91f1-bb744c7607c4` 后的代次收集。保留真实 CA 观察 Task 时，原 `3d6c2fa2-2a44-4936-8f55-73b3eb0c5824` 仍完成代次收集。完整 PAM/JWT Server 观察真实响应在途时保留同一 SDK 代次，断线排空后卸载，新代次按原 `045c90af-8342-4f77-bf45-92ce51101246` 读到同一订单。SIGTERM 排空、重启、同键回执和原活动证书核对通过。认证套件保持三张订单、两次签发提交，独立签发与中断套件保持七张订单、四次签发提交。

自然续期的挑战归属冲突经独立 Nginx 和 Pebble 复验。恢复原扫描 `6074c140-4d42-4b78-a530-2150e7d90888` 和原操作 `e2b8a113-f400-4a42-b52f-67743bdde797`，CA 保持两张订单、一次签发提交。宿主服务和文件指纹一致，证据在 `.artifacts/mb05-acme-execution/`，最终 Linux 归档 SHA-256 为 `44b885410dfad52994f360c70bf5408b89a3f4337cadbe826f18da04a6acb855`。撤销和 ARI 的独立实机故障见后续两节。Certificates 整体休眠及生产资源测量继续开放。

## MB-05 ACME 撤销确认与本地恢复

撤销派发前在原 Certificate 操作台账记录证书版本和叶证书 SHA-256。Anvil 使用已登记的 ACME 账户签名。派发后取消读取原回执，工作租约覆盖实际 CA Task 和响应。收到 CA 成功应答后先持久确认，再将匹配证书退出 Kestrel 选择器、更新撤销状态并清除原部署记录。确认后本地清理受阻时保持原回执和租约，重启只续接可证实的本地清理。待核验和待清理均拦截同一证书的新写入。

如果 CA 结果无法核实，Server 保留原 operationId 和原部署事实。启动恢复重新接管待处理责任，无自动撤销重放。当前 Anvil 适配层也将无法可靠分类的 CA 拒绝留待核验。仍缺少独立的 CA 撤销状态查询与人工确证入口。

Linux 完整 PAM/JWT Server 使用私有 Pebble、私有 DNS、SQLite 和真实 Kestrel 选择器。首轮揭示账户密钥误用于证书密钥签名模式，Pebble 返回 HTTP 403。更正账户签名后，两条真实 CA 路径通过。台账检查点、SQLite 清理失败、进程中断和重启分别断言原回执、部署事实和真实租约。两张私有证书各一次成功撤销提交，重试和重启无新增撤销。宿主共享服务与文件指纹一致。本节初测覆盖选择器状态，实际 HTTPS 握手见后续 Kestrel TLS 章节。外部 Nginx 证书轮换和整机掉电继续开放。Certificates 保持 resident。

确认清理原 `a370def5-174a-47e4-9fc4-dc750a7a4f57` 在 SQLite 阻塞时保持一个工作租约，重启仍保留该租约，移除故障后以原 ID 完成并归还。应答丢失原 `7cdc5ee4-9f42-4652-8daa-493ccefc33fe` 重启后保持同一待核验回执和一个接管租约。另一次认证 CA 回归验证订单观察 `23d34382-f24a-47cb-b3c0-d653ff889b7d` 的实际响应排空、执行代次卸载和 SIGTERM 后同 ID 查询。日志位于 `.artifacts/mb05-revocation/linux-final.log`，归档 SHA-256 为 `42473a819a989cc23e255279d946c6085166e7cc0772f06893946f5d5732ee37`。

Windows Release 证书绑定、请求回执、续期记录和模块边界回归通过。Docker 跨进程夹具以原 `c79b561e-f7c9-4c6d-bd7f-3d292bbda39b` 恢复 Interrupted，受控副作用计数为一。桌面 Release 编译与 Android Debug 单元测试通过。

## MB-05 ARI 断流与原扫描恢复

隔离验收使用 Pebble 2.10.1 的七天期测试证书。真实 CA 建议窗口尚未开始，证书已进入 `NotAfter` 兜底阈值。私有 TLS 中继先扣住真实 ARI 响应，再终止测试 Server。原扫描已持久化、尚未派发续期，工作租约在途，原证书指纹可查询。启动后的同一个扫描读取原证书的 ARI 资源，真实 HTTP 200 响应在中继处被截断，触发到期时间兜底。

替换通知在 Pebble 返回 301，随后 GET 收到 404。验收核对替代证书、原扫描 ID、原续期 operationId、工作租约与挑战清理。下一次启动的自然扫描读取同一完成时间并延后，原回执和证书指纹保持。CA 订单与签发提交计数将初次签发和唯一续期分别计入。

最终原扫描 `ccf15be2-b0ab-483c-8853-a9feff1a46ab` 续接原证书的 ARI 读取，唯一续期 `16c05331-2b8c-4a61-b6f3-ee284c21ae3b` 落盘为 Succeeded。Pebble 共两张订单、两次签发提交和一次替换通知 POST。前几轮分别暴露了夹具漏计 301 后的实际 GET、一天期证书自然窗口已开始，以及所用版本的窗口覆盖接口返回 404。最终使用七天期证书和 CA 自然窗口区分 ARI 与兜底，宿主服务及文件指纹一致。Windows Release 续期记录、ACME 五代回收和模块边界回归通过，Docker 跨进程夹具原 `bfc8a23b-e48a-45d0-8fcf-8cdb5d59219a` 恢复 Interrupted，受控副作用计数为一。

同批独立挑战归属回归恢复原扫描 `7cea11b7-163c-475f-bbea-15a19bb9082c` 和原续期 `2a4d24c1-696a-4fa8-8316-7ef045964c3c`，保留两张订单、一次签发提交。日志保留中继向已终止客户端写入时的 BrokenPipe 证据。两场实测均退出 0。完整日志位于 `.artifacts/mb05-ari-fallback/linux.log`，归档 SHA-256 为 `d01da5885e2f5163148fbd1df8f3ee9d53079ab8f75e7abe41a744f014340d56`。

本轮增加验收夹具。实际 HTTPS 监听的证书切换见下节。其他 CA 的替换通知响应及生产资源测量继续开放。

## MB-05 Kestrel TLS 轮换与撤销连续性

Linux 隔离环境使用实际 HTTPS 监听、PAM/JWT 和正式证书部署 API。两张 Pebble 证书分别绑定不同 SNI，由受信测试签发链验证名称与叶证书 SHA-256。续期原 `b6b1d90a-33f0-4301-9143-e713ffd372f0` 在 CA 已签发且下载应答暂扣时保持工作租约、旧叶证书和另一 SNI。成功后新握手选中替代证书，原 TLS 连接仍可请求健康端点。重启恢复新指纹和原续期回执，重复原键无新订单。

撤销原 `750d3ed6-76ce-44db-af0a-74c6d36a564d` 在 CA 应答在途及用户取消时保留原选择和工作租约。成功应答落盘后，即使 SQLite 部署清理受阻，撤销域名的新握手仍无法通过名称验证，另一 SNI 保持原指纹。重启持续排除撤销叶证书，解除清理阻塞后完成原回执。应答丢失的原 `54c9062a-fa40-4d5b-8254-22ad911ba688` 重启后仍待核验，原 SNI 选择保持。CA 共三张订单、三次签发提交、两次撤销提交。隔离包装器确认宿主共享服务及文件指纹一致。

首轮仅信任 CA 根的客户端在部署后验证失败。后续使用同一签发根并在隔离探针提供中间证书，禁用部分链信任，完成名称验证与连续性矩阵。独立仅根探针复现 OpenSSL 错误 20，修复与仅根复验见下节。

同批 Linux ACME SDK 复验通过五代收集、真实订单响应排空及 SIGTERM 后原 `25c9fc96-e406-401d-98e2-9fd94da029b2` 查询。Windows Release 证书绑定、回执及模块边界回归通过，Docker 跨进程原 `53578e01-838f-446e-aca0-0f76e89fb107` 返回 Interrupted，受控副作用计数为一。证据在 `.artifacts/mb05-kestrel-tls/linux.log` 与首轮 `linux-root-only.log`，归档 SHA-256 为 `539e74546b59a2d38c4bbb6097033e095f5ad00e6b150f8aa9f9c11a9845b231`。

## MB-05 Kestrel 动态证书链

Core 以已保存的叶证书和中间证书构造每个活动版本的 TLS 上下文，握手时通过 Kestrel 的原生 SNI 回调交付。显式 SNI 规则和 TLS 策略仍由 Kestrel 配置负责。无显式 SNI 的 HTTPS 端点使用原生默认 SNI 路径，既有回环 HTTP 控制套接字继续独立。新握手选中当前上下文，已选中的连接持有自己的引用，注册表只保留活动版本。

Linux 最终制品的 URL 监听和命名 HTTPS 端点都用仅信任私有 CA 根的客户端验证。命名端点同时校验 TLS 1.2 限制。两场各三张订单、三次签发提交、两次撤销提交。URL 续期 `56748004-95b2-41eb-a2a7-eb1e8ea76227`、确认撤销 `474ce102-1966-472e-a20a-82a303ae6f0a`、未知撤销 `a7a2ecc1-6279-43ad-955c-a52e60aa7528` 的原回执与部署事实通过。命名端点对应 `b072540c-7aca-4dfc-a03a-b47d16f36cfd`、`c285dc41-32f5-48cf-8edf-dbcf878d3a90`、`3140321d-f10f-45e1-9aed-04d7055fbe95`。两场确认撤销域名的新握手在本地清理受阻时已拒绝，另一域名持续通过，重启和原键查询无额外 CA 写入。

命名端点前两轮暴露了私有权限 broker 继承公开端点配置的问题。broker 改用自己的 Kestrel 配置后，只绑定自身回环 URL。最终隔离包装器确认宿主共享服务及文件指纹一致。Windows 实际托管 TLS、HTTP/3、整机掉电与生产内存测量仍待验收，Linux 测试机缺少 QUIC 运行时。另见 [Nginx 显式轮换](WebServer-TLS.md#mb-05-nginx-显式证书轮换与原重载回执)。

Windows Release 证书绑定、请求回执及模块边界回归通过，配置投影回归验证显式 SNI 规则在重载时不会保留合成的通配规则。Docker 跨进程夹具原 `df16a557-ef96-40b0-a5c9-2dcd8e345b05` 恢复 Interrupted，受控副作用计数为一。Linux ACME SDK 同批验证五代收集、订单响应排空和 SIGTERM 后同 ID 恢复。日志位于 `.artifacts/mb05-kestrel-chain/linux-final.log`，最终归档 SHA-256 为 `bda6a4e0d1d34a64ff45068a129751a019f1a7b96298d56d9c403c2c8fd1cae9`。
