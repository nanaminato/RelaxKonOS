# MB-04 Squid 配置发布与原操作门禁

## Helper 已重载而 Server 未确认

**已实现且已验证。** 完整 Linux Server 使用 PAM/JWT，通过私有 AF_UNIX 和真实 Helper 首次发布配置并启动专属 systemd Squid。原生 PID `263` 从 `127.0.0.1:18883` 向私有 HTTP 源站转发。第二次发布 `f83e15e1-028a-41a3-af74-6918f6e75e19` 将监听移到 `18884`。Server 原队列 ID 与 Helper 调用 ID 一致。真实 Helper 返回成功时，夹具暂扣应答。Server 原工作租约为一，仍保留旧发布修订。新监听已向真实源站转发，旧 TCP 监听关闭。专属原生重载计数为一，候选配置 SHA-256 为 `535874a391bf8c680a9ca18a5efab386723df4f7703f81cc717ba94b0f23dd87`。

Server 被强制终止后，独立 Helper 也换代。原操作仍为 `Interrupted / squid_publication`，完成时间为空。同键相同请求只返回原操作，同键改变内容返回 `proxy.idempotency_conflict`。在途和重启后的新键均被拒绝。状态读取显示真实配置摘要，原发布修订仍待核验。Squid PID、实际转发、配置和两份 Server 台账字节保持。全程只有一次原 Helper 派发和一次真实重载。禁用 Proxy 的启动按原 ID 拒绝。专属 cgroup 已清理，宿主运行时服务、Squid 配置、代理和 resolver 指纹一致。

首轮发布指纹未满足队列要求的 64 个十六进制字符的格式，导致队列读回失败。已改用操作者和结构化请求的 SHA-256。后两轮流量探针被生产目的端口 ACL 返回 HTTP 403。夹具改用私有网络的 80 端口，保留生产 ACL。Windows Squid 路由回归也补齐实际响应租约的测试注册。

Windows Release 的 Squid、Proxy Core 安全及 ManagedRuntimes 回归通过。可信下载探针缺少进程级测试 CA 覆盖。同批 Linux 复验受管 TUN 保留数据移除及禁用启动，原移除为 `e79ddf28-bd2e-407a-a6d5-03fe615111c1`。两组专属清理通过。归档 SHA-256 为 `0afe631f4cd5a266400b987491039527b67eca71736062eb9ebf3182e64319cd`。日志在 `.artifacts/mb04-tun-marker-sync/`，最终实机为 `squid-linux-final.log`，本机为 `--squid-only.log`、`--proxy-core-safety-only.log` 与 `squid-runtimes-windows.log`。

**已实现但实机未验证。** 本轮在配置发布成功应答处中断，完整 Server 为非 root，应答中继在私有环境以 root 连接 Helper。中继未作为实际发布执行者。Helper 在原生文件替换与重载之间中断、已验证回滚、账户和生命周期的原操作证明未纳入本轮。

**未实现或仍受门禁保护。** 原生配置和成功转发只证明当前效果。Helper 缺少按原 ID 查询的持久发布终态，原未确认请求继续拦截写入和停用。下一步在原 Helper 文件锁与原 Server 队列下记录并只读核验该终态。先区分已确认成功、已确认回滚与未知，再验证原操作结案。Proxy 保持 resident。已验证 Server 与 Helper 换代、Squid 进程连续性及专属进程树退出。对象释放、ALC 回收、隔离负载内存和生产内存收益未测量。
