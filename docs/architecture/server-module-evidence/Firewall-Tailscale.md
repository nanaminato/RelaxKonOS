# MB-07 Firewall 与 Tailscale 验收

Firewall 独立执行代码与 Tailscale 对象代次、持久责任验证。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [41. Firewall-F1 独立代码与 Tailscale TS-01 对象代次（2026-11-05）](#41-firewall-f1-独立代码与-tailscale-ts-01-对象代次2026-11-05)

## 41. Firewall-F1 独立代码与 Tailscale TS-01 对象代次（2026-11-05）

本批继续更多模块，不重做已交付 ModuleHost/Terminal，也不扩展 SystemPerformance S1。两个阶段分别验证代码所有权和对象/持久责任边界，不能相互替代；MB-02/07 的剩余整项条件见 §8。

### Firewall-F1：独立契约、执行代码与窄 Host 端口

`IHostFirewallService`、共享错误及 `IFirewallHostOperations` 当前 ABI 位于独立 `RelaxKonOS.ServerModule.Firewall.Contracts`。Linux UFW、Windows facade 和不可用后端的唯一源码迁到 `RelaxKonOS.ServerModule.Firewall`；Server 不再编译这些实现，也不静态引用实现程序集。正式构建/发布把共享契约放在根目录，实现仅放 `Modules/Firewall/RelaxKonOS.ServerModule.Firewall.dll`，无旧根 DI 实现或兼容入口。项目与所有仓内直接构造测试调用同批更新。

Core 保留原认证/HostFeature/FirewallChange grant、特权传输和 Helper 业务权威。新端口只能表达 **13 类 Firewall 操作**及 Firewall 专有字段；不能接受任意 PrivilegedOperationRequest、路径、服务/安装请求或 shell。Core 映射前拒绝未知 enum 和属于其他操作的字段。模块日志只传共享 enum，不向 Root ILogger<T> 缓存交付私有 Type/Exception。Helper 的未确认结果仍按原结果返回，不因代码释放推成功或自动重放；其外部责任没有转移到管理代码。

Host 每代创建 collectible ALC。现有唯一协调器/方法租约继续覆盖实际业务 Task，包括不合作的外部等待；取消等待不脱离原任务，也不重复增加后台 owner。实际任务结束才可对象释放/请求 ALC 卸载；Root 边界重新创建已知共享错误、取消或有界未知错误，不保留原私有堆栈/Inner。激活失败也在创建 ALC 时登记弱引用并重建 Root 错误。最多 **4 个**尚未自然回收的上下文，超过时拒绝第五代，不强制生产 GC，也不回退常驻实现。

Windows 与 Ubuntu 26.04/.NET 10.0.14 各 **31 项**通过：每个进程分别运行 Linux/Windows 两套 facade 各 **100 个真实 ALC**循环，调用方保留成功/失败/取消 Task、规则 DTO 和异常后仍观察回收；覆盖真正异步 Helper 失败、构造失败、真实协调器非合作 mutation 取消保护、未知结果不重放、借用 Core transport 不销毁及4代预算。两平台 facade 的外部 Helper 结果为受控端口，未写宿主规则。测试 GC 仅为弱引用 oracle，不属于产品内存控制策略。

独立构建门禁 **18 项**通过：规范项目依赖正例及 Host/其他执行项目、伪同名路径、二进制 Reference、包、额外框架和 COM 注入拒绝。两套 linux-x64 publish 实测实现只在 Modules/Firewall、根目录无实现 DLL、Host/test deps 无静态实现依赖，契约在共享上下文。正式打包脚本原本递归收录 publish，未增加第二套发布路径。旧 Firewall HTTP/释放与读/配对规则/结构化校验回归、Windows 原生只读回归和默认模块全量通过。

### Tailscale TS-01：本地实际工作结束与外部持久责任分开

当前三代理共享同一生产 runtime/coordinator，已具备方法租约及原持久控制/登录流程，无需新设 resident actor。新增验收使用真实注册、runtime、journal、授权作用域及生产 Helper 桥，仅替换最外层 IPC transport，验证唤醒/执行/对象释放的完整服务图。Prepared/Unknown/Issued/Closure 留在原加密账本；Applying 后等待原实际任务，HTTP 取消不能提前归还租约。浏览器后续、daemon 和 Helper 原始 intent/receipt 仍由原权威负责，管理对象休眠不清账、不执行退出登录、不以后来 Running 状态推导成功。

修复两 journal 的实际资源缺口：在 `SqliteConnection.Open` 或初始化 PRAGMA 失败时，尚未发布给调用方的连接现在立即 Dispose。正常路径仍 Pooling=false、按调用作用域关闭；损坏文件报错且保留原字节，不自动重建或重放操作。

Windows/Linux 各 **27 项**通过：16轮真实 runtime/协调器/journal/Helper 桥对象图回收，保留已完成调用 Task/挑战也不根住旧图；非合作 IPC 与已受理修改取消期间拒绝释放；控制和首次登录的 Unknown 跨代只凭原回执恢复，原 session 单次挑战隔离、关闭意图跨代不重提交、授权拒绝不达 Helper。两 journal 各16次损坏数据库 Open 后，未强制 GC 即核验连接/原生文件句柄释放并保留原损坏页；Linux 另检查 `/proc/self/fd`，不以 .NET FileShare 锁替代原生 SQLite 证据。

Tailscale 仍在 Server 程序集中，本阶段只证明对象边界和持久责任连续性，不声称其 ALC 回收或真实 daemon/Helper 崩溃验收。原 Tailscale 控制、登录、回执恢复、权限及 HTTP 全专项在 Windows 通过；未运行安装或改变测试机 tailnet/防火墙配置。

### 交付证据与仍开放条件

证据统一为本地 Git 忽略目录 `.artifacts/firewall-tailscale-20261105/`：`domain-windows.log` / `domain-linux.log`、两平台 `modules-*.log`、`tailscale-windows.log`、`firewall-build-guards.log`、`publish-layout.json`、构建/发布日志与 `remote-provenance.json`。首次编译中测试 nullable operationId 的10个错误已修正并保留原日志，不作为通过证据。SSH 固定主机密钥与归档 SHA256 核验，普通 UID 1000 直接复用 `/home/nanami/relaxkonos-tests/firewall-tailscale-20261105`；未替换运行中的 Server/Helper、未修改系统网络或账号。

Firewall 真实 UFW/Windows 写入中断、并发外部规则漂移/Helper 恢复矩阵，以及当前正式官方包整体 ABI/多平台业务负载下内存与冷启动收益仍开放。Tailscale 独立代码是否值得拆分须由基线决定，本批不为改而改；其真实 daemon/Helper/浏览器断连联合恢复也未验收。默认模块对象/代码回收测试不是生产 RSS 下降证据，MB-01/02/07/08 不因此整体关闭。
