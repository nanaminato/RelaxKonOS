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
