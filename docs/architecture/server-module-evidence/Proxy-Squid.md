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

**已实现但实机未验证。** 本阶段的回执缺失由隔离夹具暂移原受保护文件模拟。实际 Helper 在写候选后、结果落盘前被中断的窗口，以及整机掉电、账户与生命周期原操作仍待验收。

**未实现或仍受门禁保护。** Helper 不从文件、进程或监听现状推导缺失终态。原生清理、发布身份或结果无法核验时仍保留原门禁。Proxy 保持 resident。分别验证了 Server 与 Helper 退出换代、Squid 进程连续性和专属进程树清理。对象释放、ALC 回收、隔离内存和生产收益未测量。
