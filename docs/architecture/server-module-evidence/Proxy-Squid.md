# MB-04 Squid 配置发布与原操作门禁

## Helper 已重载而 Server 未确认

**已实现且已验证。** 完整 Linux Server 使用 PAM/JWT，通过私有 AF_UNIX 和真实 Helper 首次发布配置并启动专属 systemd Squid。原生 PID `263` 从 `127.0.0.1:18883` 向私有 HTTP 源站转发。第二次发布 `f83e15e1-028a-41a3-af74-6918f6e75e19` 将监听移到 `18884`。Server 原队列 ID 与 Helper 调用 ID 一致。真实 Helper 返回成功时，夹具暂扣应答。Server 原工作租约为一，仍保留旧发布修订。新监听已向真实源站转发，旧 TCP 监听关闭。专属原生重载计数为一，候选配置 SHA-256 为 `535874a391bf8c680a9ca18a5efab386723df4f7703f81cc717ba94b0f23dd87`。

Server 被强制终止后，独立 Helper 也换代。原操作仍为 `Interrupted / squid_publication`，完成时间为空。同键相同请求只返回原操作，同键改变内容返回 `proxy.idempotency_conflict`。在途和重启后的新键均被拒绝。状态读取显示真实配置摘要，原发布修订仍待核验。Squid PID、实际转发、配置和两份 Server 台账字节保持。全程只有一次原 Helper 派发和一次真实重载。禁用 Proxy 的启动按原 ID 拒绝。专属 cgroup 已清理，宿主运行时服务、Squid 配置、代理和 resolver 指纹一致。

首轮发布指纹未满足队列要求的 64 个十六进制字符的格式，导致队列读回失败。已改用操作者和结构化请求的 SHA-256。后两轮流量探针被生产目的端口 ACL 返回 HTTP 403。夹具改用私有网络的 80 端口，保留生产 ACL。Windows Squid 路由回归也补齐实际响应租约的测试注册。

Windows Release 的 Squid、Proxy Core 安全及 ManagedRuntimes 回归通过。可信下载探针缺少进程级测试 CA 覆盖。同批 Linux 复验受管 TUN 保留数据移除及禁用启动，原移除为 `e79ddf28-bd2e-407a-a6d5-03fe615111c1`。两组专属清理通过。归档 SHA-256 为 `0afe631f4cd5a266400b987491039527b67eca71736062eb9ebf3182e64319cd`。日志在 `.artifacts/mb04-tun-marker-sync/`，最终实机为 `squid-linux-final.log`，本机为 `--squid-only.log`、`--proxy-core-safety-only.log` 与 `squid-runtimes-windows.log`。

**已实现但实机未验证。** 本轮在配置发布成功应答处中断，完整 Server 为非 root，应答中继在私有环境以 root 连接 Helper。中继未作为实际发布执行者。Helper 在原生文件替换与重载之间中断、已验证回滚、账户和生命周期的原操作证明未纳入本轮。

**当时仍受门禁保护。** 此阶段的 Helper 缺少按原 ID 查询的持久终态，原未确认请求继续拦截写入和停用。后续核对见下节。

## 原成功与回滚回执

**已实现且已验证。** Helper 在原文件锁内为每个发布 ID 写入受保护的防重放记录。成功和已确认回滚写入候选摘要及实际结果摘要。查询要求原 ID、当前配置与结果摘要一致，且原生待清理标记已清除。Server 在原发布投影上保存同一 ID，只由原队列通过只读调用核验。恢复租约覆盖 Helper 应答、配置投影、异步作用域清理和终态落盘，查询响应另持 HTTP 租约。

Linux 私有 systemd 的认证发布 `4cb221b8-d9a5-4b45-9f6c-2653e0913c3f` 将 Squid 监听移到 `18884`，真实上游转发成功，旧 TCP 监听关闭。Helper 已持久化回执时终止 Server，再更换 Helper 进程。夹具暂移原回执，新键和禁用启动按原 ID 拒绝。恢复回执并制造配置漂移后继续拒绝结案。恢复精确字节后，按原 ID 取得成功，原队列和配置投影确认。候选 SHA-256 为 `535874a391bf8c680a9ca18a5efab386723df4f7703f81cc717ba94b0f23dd87`。

原 `c0b9ef89-61bb-4903-a600-4db1902eaf69` 的候选重载由私有夹具拒绝，原 Helper 恢复并重新加载上一份真实配置。再次在原回执已持久而 Server 未收到时终止并更换两进程。原操作恢复为 `Failed / squid.apply.failed_rolled_back`，同键返回同一终态。PID `263` 及 `18884` 的真实转发跨重启保持。两条原操作各一次初始派发。随后直连重启后的 Helper 重提两个原 ID，匹配请求返回原回执，改变候选被拒绝，配置及两份回执字节保持。系统命令共一次成功发布重载、一次夹具拒绝、一次实际补偿重载。恢复归还租约，最终禁用启动通过，Squid 管理路由返回 404。

Windows Release 的 Squid、Proxy Core 安全和 ManagedRuntimes 通过，可信下载探针缺进程级测试 CA 覆盖。同批 Linux 通过 Squid、受管 TUN 保留数据移除及禁用态安全交接，原 TUN 移除为 `7154d8e8-87c5-44f8-8f85-3a6f73c569a8`，原禁用计划为 `163c5afd-42a6-48e2-9652-157ff92c82cc`。三组专属 cgroup 已清理，宿主共享服务及文件指纹一致。最终归档 SHA-256 为 `456e552d9f077dc49e1f7bf7c63a5c6038a92bf3588480afd31f5b46adc97485`。日志在 `.artifacts/mb04-tun-marker-sync/`，最终实机为 `squid-receipt-linux-final.log`，本机为 `squid-receipt-final--squid-only.log`、`squid-receipt-final--proxy-core-safety-only.log` 和 `squid-receipt-final-windows.log`。

**已实现但实机未验证。** 本阶段的回执缺失由隔离夹具暂移原受保护文件模拟。真实 Helper 进程中断见下节。整机掉电、账户与生命周期原操作仍待验收。

**未实现或仍受门禁保护。** Helper 不从文件、进程或监听现状推导缺失终态。原生清理、发布身份或结果无法核验时仍保留原门禁。Proxy 保持 resident。分别验证了 Server 与 Helper 退出换代、Squid 进程连续性和专属进程树清理。对象释放、ALC 回收、隔离内存和生产收益未测量。

## Helper 在原生效果生效后中断

**已实现且已验证。** Linux 私有 systemd 在 Squid 实际收到重载指令后暂扣 `ExecReload` 完成。候选监听已向独立 HTTP 源站真实转发，上一监听关闭。原 Server 工作租约为一。原 Helper 发布记录只包含防重放身份，`Receipt` 为空，配置及原生待核验标记已持久存在。夹具终止 Helper，再让同一个原生 systemd 作业排空。

替换 Helper 后，原 Server 将该操作保留为 `Interrupted / squid_publication`，无完成时间。原工作已排空，持久准入仍保留。Server 再重启后，同键返回原 ID，新键和禁用启动均被拒绝。读取显示候选配置摘要和原生待核验状态，未更新已确认的发布修订。直接访问重启后的 Helper，原 ID 查询、同 ID 派发及新 ID 均无法绕过门禁，换候选被拒绝。未增加回执文件、重载或补偿，原 Helper 台账、待核验标记、配置和 Server 台账字节保持。原 Squid PID 持续通过新端口转发。

最终原操作为 `49dcce10-a43e-40c8-90b8-3929ea066e63`，候选 SHA-256 为 `0caad8443ae1d95af54d8ad96f1062065489beec75b6c8b06c0f6137b2d3c1f2`，Squid PID `265` 在端口 `18886` 持续转发。Helper 退出时确认原生 systemd 作业仍在，释放夹具后确认该作业排空。最终同时复验原成功 `becc3a39-a12b-428a-9bf9-ec2631c79ec1` 及回滚 `7987b450-bdbc-41e3-bfec-a59b7850b0a1`。专属 cgroup 已清理，宿主服务、Squid 配置、代理和账户文件指纹一致。Windows Release ManagedRuntimes 通过，可信下载探针缺进程级测试 CA 覆盖。归档 SHA-256 为 `73ebe06a73a448a9bce16c2b1686f8f3317e8b11694aed9ad79aa875b48dcfc5`。日志在 `.artifacts/mb04-tun-marker-sync/`，实机为 `squid-native-unknown-linux-final.log`，本机为 `squid-native-unknown-windows.log`。

**已实现但实机未验证。** 本场覆盖真实重载生效后至 Helper 写终态之间的进程中断。整机掉电、其他原生阶段中断、账户与生命周期仍待验收。

**未实现或仍受门禁保护。** 缺少原终态时没有可信的人工确证入口。Proxy 保持 resident。记录了两次换代、原生作业排空和专属进程树退出。对象释放、ALC 回收、隔离内存和生产收益未测量。

## 原发布回执查询在途时再次中断

2026-11-09 在既有 Linux 测试机的私有 systemd 环境复验完整 PAM/JWT Server 与生产 Helper。原发布 `792aa920-99b6-44c8-998e-477ff31470fc` 的配置已生效，第一次中断发生在写入原 Helper 终态之后、交付应答之前。分别验证缺失原回执和当前配置漂移时，原操作保持待核验并阻止新键与禁用启动。

恢复精确配置后，使用认证 GET 查询原队列 ID。中继先从原 Helper 收到同一 ID 的只读成功回执，再暂扣应答。此时确认 HTTP 请求未结束、至少两份租约仍在，分别覆盖 HTTP 查询响应和原队列对账。Helper 原回执、实际配置、Server 配置投影和队列台账均按原字节核对。原 PID `263` 继续在 `18884` 向专属 HTTP 源站转发，旧监听关闭，真实重载计数为一。

在该查询仍在途时强制终止 Server 并更换 Helper。下一 Server 按原 ID 只读确认，持久化原终态后归还租约。同键提交返回原 ID，按 ID 再次查询返回相同终态。Helper 原回执和配置摘要 `535874a391bf8c680a9ca18a5efab386723df4f7703f81cc717ba94b0f23dd87` 保持，恢复代次没有配置派发，真实重载计数仍为一。确认后的禁用启动通过。专属 cgroup 清理、共享服务、Squid 配置、代理与解析器指纹核对通过。

- **已实现且已验证**。Windows Release Squid 专项通过。Linux Release 同批复验原回滚 `6ffb7b9b-a800-4607-b3db-82ecd5375926` 及 Helper 终态缺失 `5419eaa9-6e7f-4e91-9b8a-3e73ccf7e652`。已核对 Server 与 Helper 实际退出、Squid 连续转发和专属进程树清理。原始日志 `.artifacts/mb04-squid-recovery-lease/linux.log` 的 SHA-256 为 `995ce856796a795214b3daa5a4d58fd94fa95628c5fad49b24670f9f325e83e4`，测试归档 SHA-256 为 `0d3be44d4cdd80407c1395276a1b8f3d5c901578036f210a484cab860947f984`。
- **已实现但实机未验证**。整机掉电仍需独立环境。本轮分别观察租约和进程退出，对象释放、ALC 回收、隔离内存差值及生产收益未测。
- **未实现或仍受门禁保护**。Squid 账户和生命周期队列目前未向 Helper 传递原队列 ID，缺少原生具名终态查询，尚无法验收中断后的原回执连续性。缺少原配置发布终态的人工确证入口及整模块交接继续开放。Proxy 保持 resident。

## 服务控制原回执与重启恢复

服务控制的原队列 ID 和动作随专属 IPC 传递。Helper 在原 Squid 文件锁内先耐久记录操作和待核验标记，再派发 systemd 命令。成功时先同步原回执，再移除并同步待核验标记。重复的同 ID 和动作只读回执。身份不匹配、无终态或待核验标记未清理时禁止重发。原队列通过只读 IPC 核验同 ID 与动作后才写入终态，工作租约覆盖查询、作用域清理和终态写入。

2026-11-09 Linux 私有 systemd 使用完整 PAM/JWT Server 和独立生产 Helper 验证原 Reload `a878be0d-7e51-4df1-9cba-4c7ceddf298b`。真实 Squid 重载成功且原回执落盘后截留应答。原队列仍 Running 且持有一个工作租约。同键返回原 ID，改动作和新键被拒绝，原 Squid PID `264` 继续向独立源站转发。终止 Server 后更换 Helper，暂移原回执可复现待核验及停用拒绝。恢复原字节后按原 ID 和动作读回成功，原队列结案、租约归零。同键返回原 ID，原回执字节保持，跨 Helper 代次只有一次原操作派发、一次实际 Reload。

- **已实现且已验证**。Windows Release Squid 专项通过，包含错误动作拒绝和正确回执匹配。Linux 已验证 Reload 响应丢失及 Start 正常终态。配置发布成功、回滚、两次中断和 Helper 原生结果未知同批通过。原始日志 `.artifacts/mb04-squid-lifecycle/linux.log` 的 SHA-256 为 `b80528054b4db3dc4447d3704d32b911ec6e6381bad332fe29ef616a4d9304a4`，归档 SHA-256 为 `7aa65c260190dcedcfbfd1b30daa0bc9579f0906e18535156f93c15eca47e83b`。已验证 Server、Helper 退出换代和专属 cgroup 清理，共享服务、配置、代理与解析器指纹一致。
- **已实现但实机未验证**。其他服务控制动作的原生中断、Helper 在服务命令结束前退出、持久化清理中断与整机掉电仍待实测。本轮未测对象释放、ALC 回收、隔离内存差值和生产收益。
- **未实现或仍受门禁保护**。账户及软件包变更需要各自的原生回执和权限证据。缺少服务控制终态时继续保留原准入，Proxy 保持 resident。

### 服务重载在途时 Helper 退出

Linux 私有 systemd 原 Reload `9eb2e972-781b-418b-8bfb-1699092232bf` 在真实 Squid 重载命令已经执行、原 systemd 作业还未结束时，终止专属 Helper。Server 原队列为 Running，工作租约为一。受保护的原生记录绑定原 ID 和 Reload，`Completed=false`，待核验标记包含原 ID。原 PID `262` 继续转发真实上游流量。

释放原 systemd 作业并核对排空，重启 Helper 和 Server。原队列保留 `Interrupted / squid_lifecycle`，完成时间为空。同键返回原 ID，新重载和新配置发布被拒绝，禁用启动按原 ID 拒绝。Helper 只收到原回执查询，原记录和标记仍在，配置及 Server 投影字节不变。真实 Reload 计数为一，原进程和真实转发保持。已验证工作租约在原进程执行结束后归零，持久准入责任跨重启保留。

Linux Release 通过，专属 cgroup 清理与共享服务、代理及解析器指纹核对通过。Windows Release Squid 专项见上节，生产实现未改动。本轮确认实际进程退出，未测对象释放、ALC、隔离内存和生产收益。原始日志 `.artifacts/mb04-squid-lifecycle/linux-native-unknown.log` SHA-256 为 `ffaabbcdf4bf0336387980fc486732cc298f7bd61095ef88372b7aa839213282`，归档 SHA-256 为 `61958728afbaf60fae752b8ee560bb58b612d40cf6297431e0f251dac9b64df7`。缺少原终态的可信确证、其他生命周期动作及整机掉电继续开放，Proxy 保持 resident。

## 账户口令变更的原回执

认证管理队列对操作者和账户请求计算用途隔离的 HMAC。原队列 ID 和请求 HMAC 传递给 Helper，原台账和回执不保存请求口令。Helper 以原文件锁保护待核验记录、账户文件及实际重载，已知拒绝或确认回滚有原失败回执。成功回执在原文件与重载确认后耐久保存。结果未知时保留待核验记录与原准入，原队列重启后只查询同 ID、同请求标识的回执。

Linux x64 私有 systemd 运行完整 PAM/JWT、生产 Helper 和真实 Squid 基本认证。原口令变更 `d749b337-a366-474e-884b-9ecaa433cd35` 完成真实重载，原应答被暂扣。Server 原队列仍运行且工作租约为一，同键返回原 ID，改换口令返回冲突。新口令已通过实际代理认证到达私有上游，PID `260` 保持。原回执和队列台账均确认无请求口令。

终止 Server 并更换 Helper。暂移原回执时，同键仍返回原 ID，新键被拒绝。恢复原回执后，原队列按原 ID 和标识完成，租约归零。原生回执字节保持，重新认证和真实转发继续，跨代次只出现一次原账户派发，重载计数保持。专属 cgroup 已清理，宿主服务及代理、解析器和账号文件指纹一致。

- **已实现且已验证**。Windows Release Squid 专项通过，包含同键改换口令拒绝和账户响应脱敏。Linux Release 验证创建测试账户、实际口令变更、原回执和工作租约跨重启，以及实际进程退出。原始日志 `.artifacts/mb04-squid-account/linux.log` SHA-256 为 `ae00cd7ad04c48d055f760c01efd55de60554a0417186727c3f9965c7950f0a6`，归档 SHA-256 为 `2fbaed4204ab980e76f0009d882968023f6006efc4e5ce8cdb06ddc95f88ca60`。
- **已实现但实机未验证**。Helper 在账户重载完成前退出见下节。原失败回执中断、启用和删除账户见后续矩阵。整机掉电仍待扩展。本轮未测对象释放、ALC 回收、隔离内存和生产收益。
- **未实现或仍受门禁保护**。原终态缺失时保留待核验，软件包变更及整模块安全交接仍开放。Proxy 保持 resident。

### 账户重载在途时 Helper 退出

原口令变更 `cc328d17-9415-42ab-8bfb-50e7be229475` 的新口令已通过实际认证向专属上游转发，原 systemd 重载作业仍在途。此时原工作租约为一，原账户外层与账户文件变更内层的待核验标记均存在，原回执为空。终止专属 Helper，再释放原作业并确认排空。

替换 Helper 和 Server 后，原队列仍为 `Interrupted / squid_account`，完成时间为空。同键返回原 ID，异键和禁用启动被拒绝。原两份标记及回执记录字节保持，后继 Helper 只按原 ID 读回执。原 PID `264` 仍以新口令提供认证转发，重载只增加一次。另验证关闭最后一个启用账户返回原失败，未触发重载。

同批复验原口令变更成功恢复。Linux 专属 cgroup 清理和共享服务、代理、解析器及账号文件指纹核对通过。Windows Release 专项见上节，生产实现未改动。原始日志 `.artifacts/mb04-squid-account/linux-native-unknown.log` SHA-256 为 `d9ac265683b0a3ec06b16dd6e62d8edb2e77d591b09ab7bf00341978dd25d123`，归档 SHA-256 为 `3a5dbffdf31255c11d649b6b76e36b8f216808ef99c6a642e57cc47a7984cdf9`。记录了原进程退出及工作租约释放，未测对象释放、ALC、隔离内存或生产收益。Proxy 继续 resident。

### 服务动作终态跨重启矩阵

Linux 专属 systemd 分别验证五类真实服务动作。每次在 Helper 终态落盘、原响应交付前截留，核对原队列 Running 与一个工作租约，再强制终止 Server 并重启 Helper。按原 ID 查询恢复原终态。同键返回原 ID，重读终态相等，租约归零，原生回执字节不变，每个原 ID 只有一次派发。

| 动作 | 原 ID | 实际效果 |
| --- | --- | --- |
| Enable | `b20ccb97-43d7-4a36-b132-64bb58a3747a` | 专属 unit 已启用，PID 261 保持 |
| Disable | `4b3e042c-fec8-405c-ab28-ab0f6e8492f2` | 专属 unit 已禁用，PID 261 保持 |
| Restart | `11541b02-3b7e-40da-9c68-af5866bec4f3` | PID 261 变为 771，新进程真实转发 |
| Stop | `819514cc-4379-4ded-90e7-de00ca7905fa` | PID 771 退出，原监听关闭 |
| Start | `30b31974-051a-44bc-8675-e0248ce96797` | 从停止态启动 PID 1161，真实转发恢复 |

同批复验 Reload 原 `6cb7dd55-1d6a-4bad-a12c-f6bf63d6d924` 的终态缺失门禁，原作业仅执行一次。专属 cgroup 清理与共享服务、代理和解析器指纹核对通过。Windows Release Squid 生产回归见上文，本轮仅增加实机夹具。原始日志 `.artifacts/mb04-squid-account/linux-lifecycle-matrix.log` SHA-256 为 `db26ddf807185d7e2030b51aab02390313cfbbc3b7056cc8bb6e9a8f047ce321`，归档 SHA-256 为 `af64ee5ece738ad872defdcaf5bf65062b32586f4f03682fd5e370c7e0a2b43b`。以上验证了真实进程退出，未测对象释放、ALC、隔离内存和生产收益。未产生原回执的窗口仅以 Reload 代表验证，整机掉电和软件包生命周期仍开放。

### 账户动作与已确认回滚的终态矩阵

在 Linux 专属 Squid 上逐项确认账户实际认证，再分别截留原 Helper 终态应答、终止 Server 并更换 Helper。原操作在应答被截留时持有一个工作租约。每次恢复按原 ID 和请求标识核对原回执，重读原终态相等，租约归零。认证主体仍是同一原 Squid 进程，每项只发生一次原账户派发。

| 动作 | 原 ID | 实际结果 |
| --- | --- | --- |
| 创建 | `ba9827a8-0b83-461c-8c28-b6af28776629` | 次要账号通过真实认证 |
| 禁用 | `5e0b29cb-4be1-421a-a44e-1935af389429` | 次要账号认证被拒绝 |
| 重新启用 | `c6e5ab9e-7602-491c-a6da-bdb1135531d8` | 次要账号恢复真实认证 |
| 删除 | `71efe696-22c5-4b9c-9804-2de5e48373d6` | 元数据不再包含次要账号，认证被拒绝 |
| 口令变更回滚 | `047f1235-0d79-4870-9467-53dd38d88df1` | 候选重载被专属夹具拒绝，实际回滚重载一次，原口令继续转发，重启读回 `squid.accounts.failed_rolled_back` |

同批复验原口令成功 `765688c3-62e2-4d34-a8b9-aee9501beacc` 与原终态缺失 `c1976dc7-d903-4858-8a95-0f895eb67b65`。原 cgroup 清理与共享服务、代理、解析器及宿主账号文件指纹核对通过。原始日志 `.artifacts/mb04-squid-account/linux-account-matrix.log` SHA-256 为 `f247e5c9f6a8ccbf88766d6a12cd9eb7ddc3a9500c7a1a474878a6dc374ff0e6`，归档 SHA-256 为 `c616e5a7569d8a9f274969bd97d4e85fa8833c3376466222a90dbfe34ca6d6c5`。Windows Release 生产回归见上文，本轮只扩展实机夹具。进程退出已单独核验，未测对象释放、ALC、隔离内存差值或生产收益。

### 软件包实机准入

现有 Squid 夹具隔离 systemd、网络、`/var/lib`、`/run` 和配置目录，`/usr` 的系统包目标仍共享。真实 APT 生命周期需要专属可写根文件系统、独立 APT 与 dpkg 台账和固定可验证制品。在同一原队列 ID 与可信原生终态绑定完成后，先在该环境验证派发、应答中断和原回执。软件包变更、未知原终态的人工确证、整机掉电及整模块安全交接继续开放，Proxy 保持 resident。

### 固定 Squid 包升级的原回执

2026-11-09 在既有 Ubuntu 26.04 amd64 测试机验证专属 overlay 根。宿主根以只读 bind 作为下层，`/usr` 的探针写入只落在上层。APT、dpkg、缓存、日志、设备、服务单元和运行目录使用专属状态，进程、网络及挂载命名空间独立。预检模拟只升级 `squid`，`squid-common` 已满足固定依赖。用 Ubuntu 归档密钥验证 `resolute-updates` 的 InRelease，从签名 SHA-256 核对解压的 Packages 索引，再核对离线制品 `squid_7.7-0ubuntu0.26.04.2_amd64.deb`，SHA-256 为 `1f962274ff78f23bef1c908319c157c02fb7426a11dac2c4f91e847237e15bcb`。预检日志 `.artifacts/mb04-squid-package/isolation.log` 的 SHA-256 为 `ea22fa65368e3b9e9cce0107f038c7309e0651d332de250b02e4fd9275385a67`。

原升级 `d29717eb-5eff-485d-af1e-b3e71994556a` 由完整 PAM/JWT Server 的组件计划与安装 HTTP 受理，初始为 `7.2-2ubuntu2.2`，目标为固定 `7.7-0ubuntu0.26.04.2`。Helper 在同一原安装 ID 下完成真实 APT/dpkg 升级、校验包归属和程序摘要，持久化原终态。AF_UNIX 中继截留原应答时，按 ID 查询和同键仍返回原 Running。终止 Server 和 Helper 后，重启按原 ID 只读核验 Helper 原终态，HTTP 返回 `succeeded` 和完成时间，同键响应与原终态相等。跨 Helper 代次只出现一次 `ComponentPackageChange`，dpkg 只记录一次 Squid 升级，APT 日志、dpkg 日志和 Helper 回执在恢复前后逐字节一致。

**已实现且已验证。** 系统包变更只有组件中心安装台账执行入口。Squid 服务和账户待核验时，原包执行入口保留门禁。包未结案时，Helper 同一文件锁阻止配置、账户和服务的竞争派发。Windows Release Squid 和组件回归通过。Linux 专属根、进程和 cgroup 清理完成，宿主 Squid 程序、dpkg 台账、配置、账号文件和共享服务指纹一致。原始实机日志 `.artifacts/mb04-squid-package/linux-live-final.log` 的 SHA-256 为 `a3b190c4d0e9724e1e20ede9e2dffec61ff85fa7a6f0291f85c83da517154d58`，归档 SHA-256 为 `0a7e64bce4c027457123220d69449a1d1126b0d9553ba837725316e4dc608a4b`。

首轮试跑修正专属根内的 Helper .NET 启动路径和既有管理员的授权断言。其后实机发现 Helper 将 systemd 的正常停止退出码归并，导致包准入误拒。改为保留原生退出码及状态文本后通过真实升级；未知服务状态继续拒绝。此前试跑和最终复验的宿主指纹均一致。

**已实现但实机未验证。** 本轮覆盖 Linux x64 的真实升级与终态已保存后的应答丢失。首次安装、真实保留数据卸载、整机掉电仍待独立验收。APT 在途及终态未持久化时的进程中断见下一节。已核实进程退出。本轮未测对象释放、ALC、隔离内存差值及生产内存收益。

**未实现或仍受门禁保护。** 缺少可信原终态时须保留原操作的待核验与准入。整模块安全交接仍待验收，Proxy 保持 resident。

### APT 在途且原终态缺失时的包门禁

**已实现且已验证。** 2026-11-09 复用上述 Ubuntu 26.04 amd64 专属根和固定签名包。管理员通过完整 Server 的组件安装 HTTP 受理原操作 `e095e28a-7a71-4711-bb3c-042a7511b1af`。专属 APT 钩子在 dpkg 已真实升级到 `7.7-0ubuntu0.26.04.2` 后等待，原 APT PID `350` 仍活跃。Helper 原记录保留该 ID，`Committed=false` 且无结果身份，同键仍返回原 Running。终止原 Server 和 Helper，确认原 APT 仍在运行，再放行并等其退出。

重启 Helper 和 Server 后，原安装 HTTP 查询返回原 ID、`interrupted` 及终止时间，同键返回相同响应。组件安装台账已结束活动任务，Squid 状态仍显示 `recoveryRequired`，新卸载计划返回 `component.recovery_required`。新 Helper 只核对原包记录。原记录、APT 和 dpkg 日志在恢复前后逐字节一致，整个过程仅一次 `ComponentPackageChange` 派发和一次真实 dpkg 升级。宿主程序、包台账、配置和共享服务核对一致，专属单元与根已清理。

Windows Release Squid 和组件回归通过。原始日志 `.artifacts/mb04-squid-package/linux-native-unknown.log` 的 SHA-256 为 `f4082927c25b0d23c317978e45399b17e0872f1930a28b78306b62d5516d2ebb`，归档 SHA-256 为 `f93f251a64613579e01976cd57bb39fcbc41a3c0d12b86ad85072ebedfbfca45`。原 APT 及夹具进程退出已核对。对象释放、ALC 回收、隔离内存差值及生产内存收益均未测量。

**已实现但实机未验证。** 首次安装需独立验收。保留数据卸载见下一节。整机掉电需可独立断电的环境。

**未实现或仍受门禁保护。** 缺失可信原回执时，包变更继续受准入门禁保护。整模块交接开放，Proxy 保持 resident。

### 保留数据卸载的原回执

**已实现且已验证。** 2026-11-09 在同一 Ubuntu 26.04 amd64 专属 overlay 根，用既有管理员经 PAM/JWT 提交冻结的 Squid 保留数据卸载计划。原操作 `db53d34b-0f69-4e2e-a1dd-caea9c26a93b` 绑定已验证的 `7.2-2ubuntu2.2` 包身份。Helper 真正执行 dpkg 移除，包进入 `config-files` 状态，Squid 可执行文件消失，配置和专属数据标记逐字节保留。dpkg 记录一次 remove，未记录 purge。AF_UNIX 中继截留已持久化的原应答，查询和同键仍返回原 Running。

终止原 Server 和 Helper 后，重启从 Helper 只读查询原 ID，安装 HTTP 返回 `succeeded` 及终止时间，同键返回相同终态。两代 Helper 合计只派发一次包变更，原包回执、APT 和 dpkg 台账在恢复前后逐字节一致，配置和数据标记仍保留。宿主 Squid 程序、包台账、配置、账户文件及共享服务指纹一致，专属进程、cgroup 和根已清理。

Windows Release Squid 与组件回归通过，测试临时目录放在工作区内。原始实机日志 `.artifacts/mb04-squid-package/linux-uninstall.log` 的 SHA-256 为 `a3777ba04737c52fc8c1f10d886cd3afea70493a52650291c215394189738256`，归档 SHA-256 为 `ac82f7117e911348af5448e137888187b573c8450e5286788dbe5b9a9bee67bd`。本轮验证了进程退出，对象释放、ALC 回收、隔离内存差值和生产内存收益未测量。

**已实现但实机未验证。** 首次安装见下一节。整机掉电仍待验收。

**未实现或仍受门禁保护。** 未知终态的人工确证及整模块安全交接仍开放。Proxy 保持 resident。

### 首次安装的原回执

**已实现且已验证。** 2026-11-09 在专属 Ubuntu 26.04 amd64 overlay 根准备 Squid 包缺席、无既有安装回执的初态，保留基础系统及已安装的依赖。私有回环镜像只提供 Ubuntu 原签名 InRelease、签名清单校验过的原 Packages 及固定 `7.7-0ubuntu0.26.04.2` 制品。APT 用 Ubuntu 归档密钥验证索引，固定包摘要同上。移除专属缓存中的包副本，实测镜像恰好收到一次制品下载。试跑调整了 dpkg 未安装状态识别，并关闭私有镜像不提供的辅助命令索引，两次都在 Server 受理前退出，宿主指纹一致。

管理员通过完整 PAM/JWT Server 提交冻结计划，原安装 ID 为 `7bab91db-9182-41b3-98d5-d16902b56972`。Helper 完成真实 dpkg 安装、校验可执行文件摘要，启动 Squid 并持久化原回执。私有 `squid.service` 为 active，回环 3128 端口可连接。中继截留原应答期间，按 ID 与同键仍返回原 Running。重启 Helper 和 Server 后，通过原 ID 只读恢复为 `succeeded`，同键返回相同终态，组件状态为 managed 且保留原提交 ID。回执、APT 和 dpkg 台账在恢复前后逐字节一致，两代 Helper 合计一次安装派发，dpkg 一次安装，服务继续监听。

Windows Release Squid 和组件回归通过。Linux Release 夹具和专属资源正常退出，宿主共享服务及程序、包台账、配置和账户指纹一致。原始日志 `.artifacts/mb04-squid-package/linux-install.log` SHA-256 为 `bd7cd10f9ed51637d52c9adfa712fa629197772b3ee2cc2150bee760d9c06b89`，归档 SHA-256 为 `fc1e049351796e894673d6d3accd023db8df3c70cc994ae97e400130646f0cab`。本轮独立核对进程退出，对象释放、ALC 回收、隔离内存差值和生产内存收益未测量。

**已实现但实机未验证。** 整机掉电和其他平台尚无独立证据。

**未实现或仍受门禁保护。** 未知终态的人工确证和整模块安全交接仍开放，Proxy 保持 resident。

### 软件包原回执查询在途时再次中断

**已实现且已验证。** 在既有 Ubuntu 26.04 amd64 测试机重新核对了专属可写 overlay、独立 APT/dpkg 台账及 Ubuntu 签名的固定制品。以启用 Proxy 的完整 Server 经管理员 PAM/JWT 受理 Squid 升级，原安装 ID 为 `30b7ea72-2fb1-4f68-b11e-d237573a4985`。独立 Helper 将包从 `7.2-2ubuntu2.2` 升到 `7.7-0ubuntu0.26.04.2`，核对程序摘要并保存终态。中继截留首次应答时，按 ID 和同键仍返回原 Running。

终止原 Server 和 Helper。第二代 Helper 已按原 ID 返回已提交回执，安装包身份逐字段等于原回执，查询应答在交给 Server 前被截留。此时 HTTP 尚未监听，Core 安装台账保持原 Running。再次终止 Server 和 Helper，核对安装台账逐字节一致。第三代启动时继续由同一 Core 安装台账负责，通过原 ID 只读核验，保存一条 `recovered` 终态事件。HTTP 按原 ID 返回 `succeeded` 及完成时间，同键返回相同终态。跨三代 Helper 仅有一次 `ComponentPackageChange` 和两次原 ID 的 `ComponentPackageQuery`。APT/dpkg 台账和 Helper 回执在两次恢复前后逐字节一致，dpkg 只记录一次升级。

Windows Release Squid 与组件回归、Linux Release 构建及专属 systemd 验收通过。夹具进程退出，专属 cgroup 和根已清理。宿主共享服务、Squid 程序、dpkg 台账、配置和账户指纹一致。原实机日志 `.artifacts/mb04-squid-package/linux-query-cut.log` 的 SHA-256 为 `e88f8f347384ec5e28f0bee3f7dab017d6811f44af34d15ce4fecf6756004926`，运行归档 SHA-256 为 `ce95f18e35d48a52bd9040d92bfc463d2453bfc26e73bfa9ec547539b3070d7f`。原 Helper 回执、第二次中断时的 Core 安装台账及 dpkg 日志的 SHA-256 分别为 `70ab932f525592b7c50cb00c338ec0f465c0a2156da54e9c055d348399979177`、`016c8ff4f42a6038fccb12f0a90254ef36dd0e274b5f6f7e4409cb4c335d126b` 和 `8fe5b98c318732c77b7000231faa1803f3950bb2ae609b070d0d3574b9bc9047`。

**已实现但实机未验证。** 其余平台和整机掉电需独立环境。该执行图验证 Core 安装台账的所有权及启动准入，未计量 Proxy 对象释放、ALC 回收、隔离负载内存差值或生产收益。

**未实现或仍受门禁保护。** 原生终态缺失时，沿用原包待核验和准入门禁。人工确证需要可信原生终态与原 ID 绑定。整模块交接仍待验收，Proxy 保持 resident。

### Server 存活时丢失软件包终帧

2026-11-09 在既有 Ubuntu 26.04 amd64 测试机复核专属可写 overlay、独立 APT/dpkg 台账和 Ubuntu 签名索引中的固定 `7.7-0ubuntu0.26.04.2` 制品。APT 预检只变更 Squid。复核日志 `.artifacts/mb04-squid-package/isolation-recheck.log` 的 SHA-256 为 `b0b15b17be4adcc2031a829259b2c4a5bc18489630c2f792be2caca0aa02d498`。

原版对照以完整 PAM/JWT Server 和专属 systemd 实际升级 Squid。原安装 `aaa2ea32-dec9-4f0d-9db7-8c40bdbed72d` 的原生终态已保存，程序摘要与包回执匹配。中继丢弃原 IPC 终帧后，同一 Server 把操作保存为 `failed / component.install_failed`。共享服务、程序、台账及配置指纹一致，专属资源已清理。对照日志 `.artifacts/mb04-squid-package/linux-reply-drop-baseline.log` 的 SHA-256 为 `833b8afe387e8af7170e29b4a2483a62aa2b1917632f60d32c60e28624601a54`。

**已实现且已验证。** 修正版原安装 `1126f0af-0ba4-4464-a923-539f286fcc7d` 从 `7.2-2ubuntu2.2` 升到固定目标。终帧暂扣时，按原 ID 和同键都返回 Running。中继丢弃终帧后，Server 原安装执行器按原 ID 只读核对 Helper 原提交与包身份，保存一条完成事件。正常终止并重启 Server 和 Helper，按原 ID 和同键返回相同 `succeeded` 终态，Core 台账字节保持。两代 Helper 合计一次原 ID 查询、一次包变更派发，dpkg 仅一次升级。APT、dpkg 和 Helper 原回执的字节一致，Proxy 管理入口在两代均可读取。

实机日志 `.artifacts/mb04-squid-package/linux-reply-drop.log` 的 SHA-256 为 `37a98927e6ab114f3da495043709adcd316a30713d7bb2457646bb2d93d2b09d`，归档为 `646e592a5cd73bb4272bcbd61d451a43ad848298b1f053c0c9ffcc5c6145deb1`。原 Helper 回执与 dpkg 日志的 SHA-256 分别为 `847347828586759d9b619d170f4b0d06703749c9a70377310d5d7a334dbcb828` 和 `31cd71122a1faba3bf2298b794429864aaf8d1d300a2c53b451141ae2a9491ca`。

反向实测的原安装为 `d3892a1c-dd9c-403c-9c4e-19f6c792a289`。dpkg 完成升级后，APT 停在专属 Post-Invoke 钩子，Helper 检查点仍为 `Committed=false`。终止 Helper，存活的 Server 尝试按原 ID 读取，连接失败并记录 `interrupted / component.recovery_required`。放行并确认原 APT 退出后，重启两进程。原 HTTP 查询和同键均返回相同回执，原 Core 台账与 Helper 检查点字节保持，Squid 仍待核验，新卸载计划受阻。Server 记录一次派发和一次原 ID 查询，dpkg 记录一次升级。首次试跑误要求被杀死的 Helper 写出完成日志。复验改为联合核对 Server 派发记录、原 Helper 检查点和 dpkg 台账。

反向实测日志 `.artifacts/mb04-squid-package/linux-reply-drop-unknown.log` 的 SHA-256 为 `7159e9bbd98ad414e646c9b9e9e02c25dc1ab1a00ebae4fd9fdffb8cc96a9ce8`，归档为 `2a5c88fa45af0985f361bbc42da5fcc20c6035b3f861e463e8c9f60cc29fba43`。待核验检查点与 dpkg 日志的 SHA-256 分别为 `d01163d1f789d2e42b952d8dbf51c1d8dbd6caa2dd503b950a9ade7f84f7067a` 和 `b2b60e193120091ddcf38d527fdacc7645b5966f44067c261089f22909ff2dce`。Linux Release 构建及 Windows Release 的 Squid、组件、模块边界回归通过。本轮确认 Server、Helper、APT 与专属夹具退出，专属 cgroup 和根已清理，宿主共享服务、程序、包台账、配置和账户指纹一致。

**已实现但实机未验证。** 本节覆盖固定 Squid 升级。首次安装和保留数据卸载见下一节。其他软件包与整机掉电仍需独立证据。对象释放、ALC 回收、隔离负载内存差值与生产内存收益均未测量。

**未实现或仍受门禁保护。** 缺失可信原终态的人工确证及整模块安全交接继续开放。Proxy 保持 resident。

### 首次安装与保留数据卸载的终帧丢失矩阵

**已实现且已验证。** 首次安装 `367b9d55-58e3-480a-8edc-1c0e885da1d7` 在 Squid 缺席的专属根使用原签名索引的固定包。专属镜像收到一次制品下载，Helper 派发一次，dpkg 安装一次，私有 Squid 启动并监听。丢弃 Helper 终帧后，存活 Server 对原 ID 只读查询一次并持久化原终态。重启后原 ID 和同键均返回相同 `succeeded`。日志 `.artifacts/mb04-squid-package/linux-reply-drop-install.log` 的 SHA-256 为 `322a1569212d0839fc6329b46732ffc903bdf9f0861693bc8c13736ae27ab502`，归档为 `cce122c328cc236bcea814ad21ac4ba77e0ae21dc72959937c50df2d14b5200f`。Helper 原回执及 dpkg 台账的 SHA-256 分别为 `f806ee168068fe76728602e4555e05187b6b0609d4e1b4af3a420ceff53a8aad` 和 `8d9b80bc2ababd84078b8fb4acfd29b6c7dd1e1a79b9d4757a999a47e72c32b4`。

保留数据卸载 `4e66e6f8-3c2a-4934-b3a8-fe71302b9f7e` 绑定初始 `7.2-2ubuntu2.2` 包身份。专属 dpkg 进入 `config-files`，二进制消失，配置和数据探针字节保持。dpkg 只记录一次 remove。丢弃 Helper 终帧后，存活 Server 按原 ID 只读查询一次并持久化原终态。重启后按 ID 和同键返回相同 `succeeded`。日志 `.artifacts/mb04-squid-package/linux-reply-drop-uninstall.log` 的 SHA-256 为 `0689a1aae471d62e47416421f8a6807c7669662e20b188aa0e8d3c165d138dd5`，归档为 `91b99fdcae0020f452ed6544eaae50cc1d9b7ac73bac7a091b619b744ed4c422`。Helper 原回执及 dpkg 台账的 SHA-256 分别为 `d35204aff1821ef2266bed705be2714cc0c018e140edb7cf78c167513355e24d` 和 `994943bbd4c170d566abcf08c4486f49e3194148178b612599e347ee6db87292`。

两轮均通过完整 PAM/JWT、Linux Release 专属 systemd 和 Windows Release Squid、组件及模块边界回归。每个原操作只有一条完成事件，Core 安装台账、Helper 回执、APT/dpkg 记录均通过跨重启字节核对。Proxy 入口在两代均可读取。进程退出、专属 cgroup 与包根清理通过，宿主共享服务及程序、包台账、配置和账号指纹一致。

**已实现但实机未验证。** 整机掉电和其他平台需独立环境。本次核对进程退出，对象释放、ALC 回收、隔离负载内存和生产收益分别留待验证。

**未实现或仍受门禁保护。** 缺失可信原终态的人工确证和整模块动态驻留仍待验收。Proxy 保持 resident。
