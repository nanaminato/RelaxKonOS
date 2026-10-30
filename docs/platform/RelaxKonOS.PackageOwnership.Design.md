# 系统包授权、外部变更与管理登记

状态：代码与自动化检查已覆盖 PO-01～PO-04，PO-05 的真实 Linux APT 生命周期尚未执行；在 PO-05 验收前不得宣称实机交付。2026-10-30。

## 产品决定

适用于组件中心明确支持的 Linux APT/dpkg 系统包；其他包管理器需独立适配。外部软件包允许显式确认后更新或保留数据卸载，不要求先纳入管理。单独提供“纳入管理”：不重装、不改业务配置、不改变服务启停，登记当前可验证的软件包。更新外部包不会隐式取得长期管理授权。只有明确纳入管理或原有受管身份才保持受管。

## 身份与证明

程序存在、包名相同、版本变化或进程存活均不能单独证明身份。Helper 在固定映射内核对 dpkg 安装状态/版本/架构、程序文件的包归属及 dpkg 文件校验、原生服务归属、无待处理包事务。外部包卸载冻结当前安装身份，不依赖候选仓库或旧下载地址。外部更新冻结候选及依赖，同时冻结当前安装身份并在执行前复查。不得根据客户端路径、摘要或布尔字段授予所有权。

受管登记持久保存首次授权和最近核验身份。外部 apt 更新后保留首次授权，只有同一包名/架构、程序与服务仍属于该包、完整性成立且无待恢复事务，才报告受管并提示外部版本变化。读取状态不得伪造原安装成功回执，也不得把新版本当成原操作提交证明。身份异常进入待核对并阻止变更。

## 台账与并发

登记、更新、卸载均走统一安装台账与产品提权、组件串行锁及 APT/dpkg 资源锁。执行前耐久写入意图；操作键及终态证明绑定原 operationId。中断时只核对原意图，不根据当前健康状态推断原操作成功。默认保留配置/数据，不 purge/autoremove，不接管业务配置。所有系统包状态查询失败均保留未知而非未安装。

## 实施清单

- [x] PO-01：安装身份查询/完整性验证与公共状态契约，覆盖外部包及受管外部版本变化。代码与自动化检查完成，真实宿主未执行。
- [x] PO-02：外部包更新/卸载冻结与 Helper 再核对、持久操作证明。代码与自动化检查完成，真实 APT 变更未执行。
- [x] PO-03：显式纳入管理台账操作、权限、确认及幂等/中断核对。代码与自动化检查完成，真实 Helper 登记未执行。
- [x] PO-04：桌面入口/三语言变化提示；共享协议调用者同步。Android 细节由 Android 文档拥有。代码与客户端/Android 单测完成，未做真实宿主验收。
- [ ] PO-05：自动化及真实 APT 包生命周期验证，记录命令、范围及未验证项。自动化检查通过；真实 APT 生命周期未执行。

## 2026-10-30 实施状态

**身份与状态。** 公共契约新增 `ComponentInstalledPackage`，`ComponentStateView`/`ComponentPackageResolution` 带 `InstalledPackage`、`ExternalPackageChange`、`RecordedPackageVersion`。Helper 的 `VerifyInstalledPackageAsync` 只在程序存在且以下全部成立时报告身份：`dpkg-query` 状态为 `install ok installed`、架构与版本可解析、程序文件唯一属于固定包、`dpkg --verify` 仅有配置文件变化、原生服务归属该包、`dpkg --audit` 为空、程序 SHA-256 可读，并再次核对 dpkg 状态未变化。查询失败返回未知并使变更不可用，不降级为未安装。受管登记与外部身份比较由 `PackageIdentityPolicy.Assess` 决定“受管/外部变化/待核对”，版本变化不单独撤销授权。

**外部更新与卸载。** `ComponentFrozenTarget` 保存当前安装身份。更新计划还冻结候选、依赖、仓库与摘要；卸载计划只冻结身份，`Package` 为空。Helper 在执行前重新测量 `InstalledPackage` 并要求与冻结值完全相等，服务归属、包审计、候选与真实更新关系均再次核对。外部更新成功后写入 `ResultIdentity` 作为本操作的终态证明；它不等于原安装回执。受管包经产品更新才写新的安装回执并推进登记身份。卸载只调用 `dpkg --remove`，保留配置与数据。

**纳入管理与幂等。** 新增 `ComponentPackageRegistration`：`OperationId` 保存首次显式授权且不因外部变化或再次确认而改写，`Identity` 仅保存最近一次授权/确认的核验身份。外部包的首次登记使用当前台账 operationId；受管包已有安装回执时沿用原回执 operationId，确认外部版本变化只推进身份。登记在组件锁内重读安装身份、拒绝待处理包事务与 Tailscale 未决操作，成功后原子写 `registration.json` 和绑定当前 operationId 的恢复证明。重复提交同一 operationId 返回同身份，身份变化则冲突。

**入口与调用者。** 桌面端在外部系统包或受管外部版本变化时显示“纳入管理/确认当前安装”，确认后经 `ComponentRecoveryAction.RegisterPackage` 进入统一安装台账；三语文案分别说明“不重装、不接管配置、不改变服务启停”和“保留原授权、只记录核验身份”。Android 同步解析身份与变化字段，外部系统包在身份可核验时允许更新/卸载，但不提供纳入管理，也不因更新取得管理权。安装路由继续要求 controller、产品提权与 `installation:install`。

**并发。** 登记、更新、卸载均先取组件文件锁再走统一台账；安装台账本身以 `service:<组件>` 与 `linux:apt-dpkg` 资源键串行化。Helper 的 APT 调用另外新增 `/var/lib/relaxkonos-component-packages/apt.lock` 全局独占锁，覆盖绕过台账的直接包操作，并避免不同组件的 apt/dpkg 交错；dpkg 前端锁仍为最后防线。锁文件与目录按 0700/0600 创建。

**自动化证据（本机 Windows，2026-10-30）。**

- `dotnet build RelaxKonOS.sln --no-restore -m:1`：0 错误。
- `dotnet run --project RelaxKonOS.Server.Tests -- --components-only`：253 项通过，覆盖身份策略、受管外部变化、外部更新/卸载冻结、恢复与包身份校验、登记台账校验、Tailscale 包事务阻塞。
- `dotnet run --project Tests/Tailscale/RelaxKonOS.Tailscale.Tests`：全部通过；新增外部卸载 `Target=null`、缺少安装身份和缺少更新候选的 pending 判定。
- `dotnet run --project Tests/Client/RelaxKonOS.Installation.Tests -- --component-refresh-only`：12 项通过；新增外部包登记入口与外部变化提示检查。
- 同一工程 `--component-lifecycle-only`：17 项通过；新增状态身份/外部变化与仅身份卸载冻结的线协议检查。
- `dotnet run --project Tests/Client/RelaxKonOS.WindowPreviews.Tests -- --component-installs-only`：121 项通过。
- Android `:app:testDebugUnitTest --tests "...ComponentWireTest"`：通过；新增外部包身份与变化解析。完整 Android 单测的 12 项失败在干净 `HEAD` 工作树同样复现，均属 `ProxyViewModelTest`/`SingBoxRuntimeViewModelTest`，与本设计无关。

**未验证与限制。**

- 未在真实 Linux 宿主执行外部包登记、外部更新、外部卸载或受管包被外部 apt 更新的完整生命周期。
- 未验证 `dpkg --verify` 在真实软件包配置变化、缺失文件、损坏文件和 usrmerge 服务别名下的判定；仅由纯策略检查覆盖格式。
- 未验证中断恢复（APT/dpkg 中途失败、Helper 重启、登记写盘前后崩溃）与真实跨组件 APT 争用；全局锁只有代码路径。
- 未验证 Tailscale 外部包更新/卸载后的原生控制互锁；未执行真实跨架构替换或包名替代（如 nginx-core）。
- 不把本机 Windows 自动化检查当作 Linux 实机交付证据。

## 接续位置

`RelaxKonOS.PrivilegedHelper/ComponentPackageOperations.cs` 负责查询和安装凭据；`ComponentPackageIdentity.cs` 负责身份核验、登记与登记证明；`ComponentPackageRegistration.cs` 定义首次授权与身份推进；`ComponentPackageChanges.cs` 负责变更台账；`RelaxKonOS.Server/Components/HostPackageComponentAdapter.cs` 与 `NginxPackagePlan.cs` 负责冻结和执行；桌面 `ComponentPackageFreeze.cs` 负责动作和确认。下一步是在真实 Debian/Ubuntu 主机上按 PO-05 执行并记录外部包登记、受管外部更新、外部更新、保留数据卸载、中断恢复与跨组件并发，再把结果写入[组件中心实施进度](RelaxKonOS.ComponentCenter.Progress.md)。
