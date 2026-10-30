# Tailscale 与组件中心测试记录（2026-10-30）

## 后续真实安装与修复结果（用户授权后）

以下是后续变更型验收，下方早期只读阶段的失败和边界仍作为历史证据保留。

| 对象 | 已完成的真实验证 | 最终状态 |
| --- | --- | --- |
| Nginx | 冻结计划、提权、生产安装台账、原成功回执、提交 ID、`nginx -t`、本机 HTTP 200 | 受管 `1.28.3-2ubuntu1.12`，服务 Active |
| FRP | 消除重复目录、合法计划、官方文件上传、固定 SHA-256、生产安装台账、原回执和同键重放 | frpc/frps 受管 v0.71.0，无待恢复；未配置业务隧道 |
| Tailscale | 官方签名仓库安装、用户浏览器授权、原登录恢复关账、DNS 设置变更与恢复、断开/重连、原回执和执行重放、进程重启后只读复核、设备间直连 | 外部安装 1.104.4，Running/Online，地址 `100.113.194.85`，接受 DNS、不接收路由 |

### 安装与操作身份

- Nginx 安装：`21963f3c-bba7-4305-b4fe-6ff12b6d37b8`，Succeeded，组件状态的 committedOperationId 相同。
- FRP 服务器下载：`0a6c9ba5-a916-4727-acb5-778a1032bc0d`，Failed / `runtime.redirect_forbidden`，保持 Absent。
- FRP 文件安装：`cb12a859-3171-4970-8529-87bde489958f`，Succeeded；同键重放仍为该操作。
- 首次登录：`06b92e2f-c942-4a9f-b67e-f30e3d5cde2f`，Issued；浏览器挑战链接最初被传输重放门禁拦截，使用同一次原生 AuthURL 由用户完成授权。修复后经 API 恢复预览及确认关账，保留 Issued 原回执，没有伪造 Completed。
- DNS 关闭：`4a6c2049-552a-44a9-a5d6-aa73e9ce3c20`；断开：`723f6c5c-9d7a-400c-b363-5b4327785e99`；重连：`9754b486-d521-409b-8327-1cb03de682b0`；恢复 DNS：`30cd1dd9-c443-4dfa-8061-d3e7f2b44bb6`。四项均 Applied，原回执与重放结果一致。

### 修复、部署与回归

1. APT 默认 MD5 导致无法固定安装目标：Helper 解算命令显式添加 `-o Acquire::ForceHash=SHA256`。实机初次安装用临时同值 APT 配置验证；新 Helper 部署后已移除 `/etc/apt/apt.conf.d/99relaxkonos-test-sha256`。
2. FRP 配置绑定将默认数组与显式数组追加：`FrpRuntimeOptions.BindConfiguration` 在绑定显式 Releases 前清空默认集合，新增回归验证配置目录只保留所配目标。实机备份 appsettings 后移除与默认相同的四条显式配置，保留原内置和 Helper 批准的同一 pin；修复后的 Server 尚未发布到实机。
3. Tailscale 原 operationId 的后续请求被 Linux socket 的十分钟重放缓存拒绝：新增 `PrivilegedOperationReplayPolicy`，仅对已有持久、身份绑定台账的 Tailscale 闭集动词跳过通用缓存，执行与恢复仍走领域台账。Windows pipe 同步采用该策略，保留普通操作重放拒绝。真实 socket 回归覆盖多次领域执行、挑战、恢复与回执，并继续验证普通操作重放拒绝。

Linux Helper 使用当前源码 `dotnet publish -c Release -r linux-x64 --self-contained true` 构建，备份原目录后部署到 `/usr/local/lib/relaxkonos/privileged-helper`。部署归档 SHA-256：`8f93d619d5b4644e8d48ccee2b3fdff2f5f35d421cc83057164658ed44db71c7`；实际运行 Helper DLL SHA-256：`86ceb0969dce3a7ffa8a11b0162a4b403855cb3da27c17cd23b15bec7dffacf1`。这是测试宿主热修复，原发布 ZIP 未修改；Server 仍为 0.1.8。

后续回归：组件专项 **156 条 PASS**（新增配置替换检查），socket 专项通过，Tailscale 专项 **649 项通过**。日志为 `.tmp/component-install-fix-tests.log`、`.tmp/helper-replay-socket-tests.log`、`.tmp/tailscale-install-regression-tests.log`；Helper Linux 发布和 Server 测试项目构建成功，保留既有警告。重启 Server/Helper 后四个原操作仍只读返回 Applied，控制无恢复门禁，配置与基线一致，状态 Running/Online；`tailscale ping` 到另一设备直连约 1 ms。

撤下临时全局 APT 设置后，生产 API 的 Samba 安装计划实测 Allowed、无问题码，冻结包带 64 字符 SHA-256；只预览未安装，证据 `apt-fix-plan-evidence.json`。Nginx 从客户端访问 `http://192.168.1.11/` 返回 HTTP 200，RelaxKonOS 最后健康检查为 healthy。

实机证据目录 `/root/relaxkonos-install-test-20261030/`，含 `api-evidence.json`、`frp-file-evidence.json`、`tailscale-login-operation.json`、`tailscale-control-evidence.json`、`tailscale-after-fix.json`、`restart-evidence.json`，均不保存密码或令牌。原 Helper 备份在 `helper-before-hotfix/`，原 Server appsettings 为 `server-appsettings.json`。

### 尚未验收或仍失败

- FRP 服务器下载仍拒绝 GitHub 签名查询串重定向，文件安装成功不等于下载路径已修复。
- Tailscale 在组件中心仍无安装适配器，本次经[官方 Ubuntu 26.04 签名仓库](https://pkgs.tailscale.com/stable/#ubuntu-resolute)作为外部实例安装；不宣称安装生命周期受管。
- 修复后没有再次注销设备来重测全新浏览器挑战交付；已验证原登录恢复关账。
- 未测试组件跨版本更新、卸载、回滚、故障注入，未建立 FRP 穿透业务，也没有完成桌面/Android 实机 UI 验收。

## 测试对象与范围

- 实机：192.168.1.11，Ubuntu 26.04 x64，RelaxKonOS Server 0.1.8。
- 本地源码：`773cea68`，加本次 `ComponentLifecycleChecks.cs` 测试夹具修正。
- 实机执行认证、状态观察、冻结计划预览及提权检查；没有提交第三方安装、更新、卸载、Tailscale 登录或网络变更。
- 本地专项使用隔离文件、替代原生命令与测试传输；不能作为真实安装、跨版本切换、daemon 或组网验收。

## 自动化结果

| 专项 | 结果 | 日志（仓库根目录相对路径） |
| --- | --- | --- |
| Tailscale Server/Helper | 649 项通过 | `.tmp/tailscale-server-test.log` |
| Tailscale 桌面行为、传输与布局 | 202 项通过 | `.tmp/tailscale-client-test.log` |
| 组件生命周期与安装台账 | 155 条 PASS，退出码 0 | `.tmp/component-server-test.log` |
| 组件中心与运行时客户端 | 520 条 PASS，退出码 0 | `.tmp/component-client-test.log` |

对应四个工程均以 `dotnet build --no-restore -m:1` 构建成功，测试使用当前构建的 `--no-build --no-restore`。Server 构建保留既有未读参数、可空参数和 NuGet 漏洞数据不可达警告；客户端构建无警告。首次统一入口构建失败，随后直接构建成功；第一次服务端测试被沙箱临时文件原子发布权限阻断，解除限制后重跑通过。这些失败没有计为通过。

测试夹具修正：路由元数据检查注册 `IRuntimeDownloadProxyProvider`；将旧的“更新、卸载始终不可用”断言对齐现行冻结计划契约，分别验证无更新、所有权、原安装目标和支持的更新/卸载计划；Repair/Rollback 继续拒绝。没有修改生产生命周期实现。

## 实机结果与阻塞

### Tailscale

- 未认证状态请求返回 401。
- 已认证状态返回 200、`not-installed`、`tailscale.not_installed`；宿主也没有 CLI，`tailscaled` 不活动。
- 控制读取返回 503、`tailscale.control_unavailable`；取得 `HostNetworkChange` 提权后仍相同，不是仅缺提权。
- `/api/v1.0/components/tailscale/state` 返回 404、`component.unknown`。当前宿主没有组件中心生命周期适配器，不能完成安装/登录/连接闭环；参见 [Tailscale 实现边界](../applications/RelaxKonOS.Tailscale.Design.md)。

### 组件状态与所有权

nginx、docker、squid、samba、git、frpc、frps、sing-box、mihomo 共九个状态端点返回 200。Docker、Squid、Git 识别为外部安装；Docker/Squid 更新和卸载计划拒绝缺少受管所有权，未执行接管。Git 本轮变更计划返回 403，未验证其执行链。sing-box 观察为 External，未执行覆盖安装。

### APT 候选无法冻结

Nginx、Samba 状态为 Absent，安装计划返回 `component.package_unresolved`，但宿主 `apt-cache policy` 实际存在候选。

只读重现：默认 `apt-get install --print-uris --yes --no-remove --no-install-recommends nginx=1.28.3-2ubuntu1.12` 输出 `MD5Sum`；`AptPackageResolution.TryParseUriPlan` 要求 SHA-256，因此无法建立目标。相同命令显式添加 `-o Acquire::ForceHash=SHA256` 后输出 SHA256，确认摘要选择是阻塞原因之一。没有降低校验要求，也没有安装包。生产修复和部署后复测仍待完成。

### FRP 合法计划返回 500

frpc/frps 的 `pinnedArchives` 各返回两条相同 releaseId、版本和摘要的记录。使用状态返回的 revision、pinRevision、releaseId 和版本提交合法安装计划，两者均返回 HTTP 500。

服务器日志确认 `FrpComponentAdapter.PlanAsync` 的 `SingleOrDefault` 抛出 `Sequence contains more than one matching element`。首次未携带 pinRevision/版本的负向请求正确被拒绝；不把该负向结果当成合法计划测试。生产重复条目处理和部署后复测仍待完成。

## 实机证据与结束状态

完整实机响应保存在服务器 `/root/relaxkonos-component-test-20261030.json`（0600，不保存令牌或密码）。测试结束时 Server、Guardian、Privileged Helper 均 Active，本机 `/healthz` 返回 200、`healthy`。FRP 500 在日志中留下预期复现异常，因此不宣称本次实机全通过。
