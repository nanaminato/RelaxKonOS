# MB-05 WebServer 的 WebRoot、TLS 与续期部署

隔离 WebRoot 签发、Nginx TLS 轮换、证书续期自动部署与原重载回执。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [53. MB-05 Nginx 重载中断后的 TLS 连续性](#53-mb-05-nginx-重载中断后的-tls-连续性)
- [MB-05 网站发布自动 ACME 的原 Direct HTTP-01 阻塞](#mb-05-网站发布自动-acme-的原-direct-http-01-阻塞)
- [MB-05 网站发布 WebRoot 签发与中断恢复](#mb-05-网站发布-webroot-签发与中断恢复)
- [MB-05 首站 HTTP-01 显式启用与重载中断](#mb-05-首站-http-01-显式启用与重载中断)
- [MB-05 首站 WebRoot 备份核验与授权 TLS 绑定](#mb-05-首站-webroot-备份核验与授权-tls-绑定)
- [MB-05 网站发布自动建立首站 HTTP-01 路由](#mb-05-网站发布自动建立首站-http-01-路由)
- [MB-05 首站临时路由的授权清理](#mb-05-首站临时路由的授权清理)
- [MB-05 首站授权续接原证书](#mb-05-首站授权续接原证书)
- [MB-05 首站续接绑定的原生回执对账](#mb-05-首站续接绑定的原生回执对账)
- [MB-05 首站首次 TLS 绑定的原生回执对账](#mb-05-首站首次-tls-绑定的原生回执对账)
- [MB-05 网站 WebRoot 签发的取消与停机排空](#mb-05-网站-webroot-签发的取消与停机排空)
- [MB-05 WebRoot 挑战内容变化时保留原清理责任](#mb-05-webroot-挑战内容变化时保留原清理责任)
- [MB-05 续期扫描跨挑战归属阻塞恢复](#mb-05-续期扫描跨挑战归属阻塞恢复)
- [MB-05 Nginx 显式证书轮换与原重载回执](#mb-05-nginx-显式证书轮换与原重载回执)
- [MB-05 Nginx 续期自动部署](#mb-05-nginx-续期自动部署)
- [MB-05 Nginx 自动部署准入前的进程中断](#mb-05-nginx-自动部署准入前的进程中断)
- [MB-05 Nginx 自动部署的目标检查点恢复](#mb-05-nginx-自动部署的目标检查点恢复)
- [MB-05 Nginx 自动重载在派发前的原回执恢复](#mb-05-nginx-自动重载在派发前的原回执恢复)
- [MB-05 直接 WebRoot 启用执行器](#mb-05-直接-webroot-启用执行器)
- [MB-05 WebRoot 逐站点回执与清理恢复](#mb-05-webroot-逐站点回执与清理恢复)
- [MB-05 WebRoot 已知拒绝的具名补偿](#mb-05-webroot-已知拒绝的具名补偿)

## 53. MB-05 Nginx 重载中断后的 TLS 连续性

既有 Linux 测试机运行独立的 Nginx master、回环端口、配置和临时证书。客户端校验对端证书指纹和回环地址。生产 WebServer 重载入口提交操作后，隔离 transport 对专属 master 执行真实 reload，等 HTTPS 返回新内容且原回执仍为 Running 再终止管理工作进程。原 master 继续提供 HTTPS，新 owner 持久化原操作 `dbe8c470-e6c2-4050-84de-4ccf2490603c` 为 Failed / interrupted。两条重载入口使用相同幂等键均返回原回执，master 日志仅有一次重载。仅清理专属 master 和临时目录。

Windows Release 构建和 Linux x64 Release 实机验收通过。实机日志为 `.artifacts/mb05-nginx-tls-live.log`，制品归档 SHA-256 为 `d6530f4e0fdaca000a963ad8dfa62acc9259a13b485b13e7706e061e6437f01b`。本轮的 TLS 配置由夹具提供，生产站点绑定和网站发布协调器的实机恢复仍待验收。

### 生产站点绑定

既有 Linux 测试机在私有网络和挂载命名空间运行独立 Nginx，使用私有 `/etc/nginx` 和 443 端口。生产 WebServerManager 与 Nginx provider 通过站点保存入口验证本地证书文件、渲染配置并持久化站点元数据。测试 transport 限定在私有配置树，执行真实配置检查和重载。HTTPS 客户端校验证书指纹并读取生产站点配置指定的静态内容，原 master 日志确认一次 reload。

Windows Release 构建和 Linux x64 Release 实机验收通过。日志为 `.artifacts/mb05-nginx-site-live.log`，归档 SHA-256 为 `f914d7871113188fbe605eed44ca1c77fe503425a19152f989d1a4a0f42a4f78`。Nginx 报告现有 listen http2 写法弃用警告，配置检查通过。本轮覆盖本地证书文件绑定，证书仓库 ID 绑定及网站发布协调器崩溃恢复仍开放。

### 证书仓库 ID 绑定

Linux x64 Release 实机使用生产 FileCertificateStore 保存第二张自签证书。站点更新按原更新时间提交，持久元数据记录证书 ID，生成配置引用证书仓库的稳定 PEM 路径。客户端以第二张证书的指纹完成新 TLS 连接并读回站点内容。站点更新增加一次重载。

PASS NGINX LIVE SITE TLS: local and managed certificate bindings applied, certificate=00ffc68b-f14d-4959-bbad-6ce61d6d8dd3; reloads=2

日志为 .artifacts/mb05-nginx-managed-live.log，归档 SHA-256 为 c38990ce7d3e5f6f0be35307e66d3b230ccd599c3353499b81d64befafcf9b3d。暂存配置检查期间出现重复 server_name 警告，最终配置检查和实际 TLS 均通过。网站发布协调器跨进程恢复仍待验收。

### 网站发布协调器进程崩溃

Linux x64 Release 在私有网络及挂载命名空间运行生产发布协调器、证书仓库、站点 provider 和独立 Nginx。应用运行状态由 Docker 观察夹具提供，上游为真实回环 HTTP 服务。发布进入 Verifying 后，工作租约仍在，原台账和应用关联已落盘。父进程使用 SNI 及指定证书指纹验证 Nginx 已代理实际流量，再强制终止发布工作进程。

启动恢复将原操作 61f0e452-39ff-4e1c-b144-bfcc5a1a62b6 持久化为 Interrupted。同键返回原操作和问题码。原 master 在恢复后继续通过 HTTPS 代理同一上游，应用关联保留，发布只触发一次重载。日志为 .artifacts/mb05-nginx-publication-live-san.log，制品 SHA-256 为 f9c77392701e801a355058bf3e8a4c9937cc22d9e6b22f7e7b4a1823b5cf7c78。初次测试的 PEM 读取及 TLS 探测失败在夹具中修正，最终实机通过。发布协调器的证书查询 DTO 和验证阻断使用夹具，独立 CA 签发见 §52。

### Nginx 已重载、发布元数据未提交

在同一隔离 Linux 环境，真实 Nginx reload 成功后暂停 transport 响应。实际 HTTPS 已代理回环上游，站点元数据中尚无新站点，应用关联尚未写入，原发布回执仍为 Configuring 且工作租约在位。终止发布进程后，启动恢复按原 operationId 11415427-0caf-4545-9018-2a201877ab83 持久化 Interrupted 和 website.site_application_unverified。同键重试取回相同回执。站点元数据内容和应用关联保持中断时的状态，原 master 继续代理 HTTPS，发布只触发一次重载。

Linux x64 Release 实机日志为 .artifacts/mb05-nginx-configuring-live.log，制品 SHA-256 为 2d1f70095edd08bf32ac0db5cbd042d7358da80a2920e00997a9577b53463503。该路径保留未知结果供复核。

## MB-05 网站发布自动 ACME 的原 Direct HTTP-01 阻塞

旧网站自动签发路径选择 Direct HTTP-01。生产 `HostPrivilegeService` 拒绝把 Server 的进程身份当作权限，现有生产 Helper 无此监听能力。实机在私有系统启动独立 Pebble 和 DNS，已探通 TLS 目录，旧发布在签发前返回 `certificate.port80_elevation_required`。当前网站自动签发路径见下节。

此路径的跨进程回归核对已持久化的原失败回执、同键和应用历史，检查证书操作 ID 缺失、CA 无新订单、没有打开 80 端口或重载 Nginx。原 HTTPS、原 Docker 上游和共享服务继续运行。

Linux 原发布 `929bf5d6-7e68-4df1-baca-8f503bb88af0` 跨重启返回原失败。首轮等待签发响应失败，第二轮通过生产回执确认拒绝原因，最终轮按拒绝路径验收。日志为 `.artifacts/mb05-acme-authorization-final-website-acme.log`，现有证书路径的 Verifying 回归见 `.artifacts/mb05-acme-authorization-final-website-verifying.log`。Release 归档与上一阶段相同。

## MB-05 网站发布 WebRoot 签发与中断恢复

自动签发要求所选实例已有 ACME WebRoot 集成，并检查实例配置权限与证书签发权限。新域名须预先从 CA 的网络通过 TCP 80 到达该 WebRoot。证书操作用原网站 operationId 持久化关联与独立的请求键范围，网站重启恢复可补齐尚未写入的原证书回执。WebRoot 签发和续期在写入前记录挑战文件名与内容摘要，原证书任务完成挑战清理后才提交终态。重启清理核对原路径与摘要，文件无法确认时保留并隔离证书工作准入。

Linux x64 Release 使用完整 PAM/JWT Server、生产 Helper、私有 systemd、真实 Nginx 和 Docker Engine，以及专属 Pebble CA 和测试域名。夹具预置专属 Nginx HTTP-01 路由，CA 实际通过 Nginx 取挑战。在成功路径，正常签发并部署到真实 Docker 上游，TLS 客户端信任专属 CA 并核对实际响应，Nginx reload 一次，原挑战已清理。在中断路径，测试中继暂扣 CA 已签发的实际证书响应，此时终止 Server。重启后按原网站 ID、应用历史和同键返回同一中断回执，原证书操作 ID 可只读查询 CA `valid`。CA 只创建并 finalize 一次，网站未部署，原 TLS、Nginx master 和 Docker 上游继续运行。原挑战路径返回 404，无关测试文件保留，Nginx 无重载。

最终中断发布 `a18aad80-5b6a-48a3-a131-25961501e8d6` 关联证书操作 `a1694919-20d1-4657-84ee-a2a0820a7546`。成功发布为 `7b65af66-eb68-4ac1-8103-491250e4f420`，原 Verifying 回归为 `f9cc0811-4dbd-4816-9bde-53d06c5a18b6`。三个 Linux 运行均清理专属 cgroup，宿主 Nginx/Docker PID 与 Nginx 配置指纹一致。日志为 `.artifacts/mb05-webroot-final--systemd-website-acme.log`、`.artifacts/mb05-webroot-final--systemd-website-acme-success.log` 与 `.artifacts/mb05-webroot-final--systemd-website-verifying.log`，Release 归档 SHA-256 为 `9cb6545d4a170606d6a6f854cb015315263a3dadc964ce4eaed7a61717094f9d`。首轮夹具误读 `remoteStatus`，修正后通过。Windows Release 模块边界回归（含跨进程 Docker 原回执与 WebRoot 台账恢复）及 Android 网站发布请求单测通过。Windows Direct HTTP 实监听在允许本地 URL 注册的运行中通过 16 项。

待验收范围包括公共 CA 与公共 DNS、完整非管理员提升交互及整机掉电。首个独立站点的显式启用及网站发布自动建立路由见后续小节。独立执行程序集与动态休眠仍开放，Certificates 和 WebServer 保持 resident。

## MB-05 首站 HTTP-01 显式启用与重载中断

Linux 系统包 Helper 允许已有暂存文件原子移动为集成目录中的固定 `acme-http01.enabled`。其余 `.enabled` 文件及子目录中的同名文件仍拒绝，原路径和链接检查继续生效。旧 Helper 实测在该移动处返回 `webserver.config_elevation_required`。原有 WebRoot 的网站发布对照继续通过。

既有 Linux 测试机的私有 systemd、PID、网络和挂载命名空间运行完整认证 Server、生产 Helper、真实 Nginx、独立 Pebble 与专属 DNS。夹具从空的 RelaxKonOS 站点目录开始，空实例启用返回 `webserver.acme_no_managed_sites`，没有重载。通过认证 API 保存首个 HTTP 站点并显式启用 WebRoot。正常路径启用 `55e19870-e050-4f17-8c70-7bd28f705e7b`，新证书操作 `b60a2556-83e5-45a7-bda1-f438356e3391`。Pebble 实际通过 Nginx 取挑战并签发一次，挑战清理后绑定到原站点，原 Nginx master 用受信任的 TLS 提供站点正文，旧 HTTPS 站点持续服务。

中断路径在 Nginx 已服务挑战路由、真实 systemd 重载未返回时终止 Server。重启后按原 ID `5350cd4b-bb11-40ac-bc5e-36530f4264b4` 和同键返回相同 `Failed / webserver.operation_interrupted`，没有重放重载。独立检查路由后明确提交新证书操作 `7e7e9144-3b9c-45dd-a7e8-2b7f3745daf0`，一次 CA 签发完成且原挑战清理。当时站点 `.rollback` 阻止随后的 TLS 绑定，返回 `webserver.site_save_failed`，备份字节、原站点元数据和旧 HTTPS 保持，未发生第三次重载。后续恢复见下节。

两轮的专属 cgroup 已清理，宿主 Nginx、Docker PID 和 Nginx 配置指纹一致。日志为 `.artifacts/mb05-first-site-final--systemd-first-site-acme.log` 与 `.artifacts/mb05-first-site-final--systemd-first-site-acme-success.log`，Release 归档 SHA-256 为 `494dec81bf1d019fb0acd57d2bf330a3e7954a2869419883a2301512b3f6bf45`。初轮夹具遗漏向命名空间传入模式参数，修正后复现旧 Helper 拒绝。Windows Release 模块里程碑回归通过，Docker 跨进程恢复 `e3ed673d-e29f-48e5-8429-776513717dc6` 为原 Interrupted，副作用计数为一。

## MB-05 首站 WebRoot 备份核验与授权 TLS 绑定

Linux 系统包 Nginx 的站点更新在持有原配置门禁时，从持久站点定义重建有 WebRoot 和无 WebRoot 两份配置。当前文件和原 `.rollback` 分别逐字节匹配，且 Helper 确认 Nginx 服务作业已排空，新的授权更新才可用当前配置原子替换可重建的旧备份。Helper 的系统服务操作共享执行门禁，空闲检查还查询实际 systemd 作业，以识别跨 Helper 进程的在途作业。文件不匹配、缺少当前文件、链接或作业状态不明时保留原备份。原启用回执仍按原 ID 返回中断，文件证据只用于后续更新准入。其余站点配置中断形态继续保留供核验。

Linux Release 在私有 systemd 上复验。原启用 `d100fc82-3425-404b-993d-ea20587de1d8` 的重载仍在途时终止 Server。重启后由 Pebble 真实验证并签发，证书操作为 `7fbaf778-4c93-4c58-bbd8-9a9dbb34715c`，全程一次订单和一次签发提交。原 Helper 存活、Helper 崩溃并重启但原 systemd 作业仍执行，以及故意修改备份三个条件下，绑定均拒绝并保留当时备份内容及原站点元数据，未新增重载。恢复准确的备份且作业排空后，明确提交的新绑定只重载一次，清理备份，原 Nginx master 用新证书提供站点正文并保持旧 HTTPS。绑定完成后同键仍返回原中断回执。

同制品复验直接重载原回执 `b7054c8b-5d2b-4e88-9795-8f211f4fd158`，Helper 重启后旧 ID 仍拒绝重载。两个专属 cgroup 已清理，宿主 Nginx、Docker 及配置指纹一致。日志为 `.artifacts/mb05-first-site-recovery-final--systemd-first-site-acme.log` 与 `.artifacts/mb05-first-site-recovery-final--systemd-receipt.log`，归档 SHA-256 为 `033dd6f176406d9ee99dd7788509c53f27eb66269a186685c17e25f45c6be121`。Windows Release 模块里程碑回归通过，Docker 跨进程原 `d2699ea5-6353-46df-8efe-0ea63e018a2a` 返回 Interrupted，副作用计数为一。网站发布自动建立首站 HTTP-01 路由见下节，整机掉电仍开放。

## MB-05 网站发布自动建立首站 HTTP-01 路由

Linux 系统包 Nginx 在已集成、运行中且受管站点为空时支持首站自动 HTTP-01。Helper 只读核验现有 TCP 监听和固定 `nginx -T` 的配置摘要条件。80 或 443 被使用，配置包含对应显式监听、隐式监听或无法确定的语法和截断输出时，拒绝在该实例建立路由。完整配置留在 Helper 内。自动路径目前建立 IPv4 路由。

原发布在任何配置写入前持久化站点 ID 和未核验责任。临时配置包含同一发布 ID，仅服务该域名的 HTTP-01，其他 Host 和请求路径返回 404。只有原 ID 的原生重载回执确认成功才允许提交 ACME。最终站点写入前逐字节核验临时配置和原 ID，成功后用正式 TLS 站点替换临时配置、保留该站点的挑战路由，再完成原应用关联。单站点标记独立于实例级 WebRoot 集成。

重启原发布仅恢复原回执与未核验责任。该阶段中断时保留原文件及 Helper 证据，拒绝同目标新发布及直接覆盖。后续授权清理见下节。网站发布和证书仍由原常驻 owner 管理。

Linux Release 在私有 systemd、Docker、Nginx、Pebble 与 DNS 通过三轮。原发布 `612a9f7c-9b01-4cd4-80cc-6a70f63f2bf4` 由原生 Nginx 提供实际挑战，证书操作 `a458416c-b552-4fb2-86ef-c47eee91fe92` 只创建及签发一次，真实 HTTPS 返回容器正文。共两次重载，临时配置和备份均清理，原 Nginx master 的旧 TLS 持续。独立活动监听及未加载的 Nginx 端口配置均在写入和下单前被拒绝。

中断发布 `2701ed88-08aa-43cb-9443-2c77bb5c3819` 在原生重载生效、返回前终止 Server。错误 Host 和非挑战路径均返回 404。重启后同键返回原 Interrupted，原 Helper 回执及同 ID 请求都读到原成功证据。直接覆盖和新键发布被拒绝，原配置字节保持，只有一次重载，CA 零订单，应用未关联。已有 WebRoot 的发布对照 `a65aa654-df57-4a2e-a4dc-02f1c986e5d4` 仍以一次重载完成 HTTPS。三轮专属 cgroup 清理，宿主 Nginx、Docker 和配置指纹一致。

最终日志为 `.artifacts/mb05-website-bootstrap-final--systemd-website-acme-bootstrap-success.log`、`.artifacts/mb05-website-bootstrap-final--systemd-website-acme-bootstrap.log` 和 `.artifacts/mb05-website-bootstrap-final--systemd-website-acme-success.log`，归档 SHA-256 依次为 `d5417d2af3f11a2345250f2f9f91e820d1afe60c39946aa842bd1ae0322dd984`、`f892f7cda6d915e4631b90acf65a3479db16a8a0e24a563c2312cf0faac5c6fe` 和 `74c890658f2077b511f281946627d84ef9ba050c2589520af66fea93e664be18`。

Windows Release 模块边界回归通过，Docker 跨进程原 `dba7519d-8ecb-4b6d-8aa6-a220662a1821` 返回 Interrupted，副作用计数为一。日志为 `.artifacts/mb05-website-bootstrap-win-final.log`。

## MB-05 首站临时路由的授权清理

授权清理按原发布 ID 提交并在原发布协调器的工作租约中排空。进入前核对原 Helper 重载回执、已结束的本地证书责任、站点未关联、已集成锚点和原临时配置字节。实际移动前在原发布台账持久化独立的清理重载 ID。临时配置移动到专属备份，原清理 ID 确认原生重载完成后才移除备份、提交清理时间并解除站点占用。原发布的状态、错误和关联证书保持。

重新提交清理使用同一 Helper ID。原清理已确认时仅继续删除匹配备份或提交清理终态。原生作业仍在途、Helper 缺原回执、配置改动或证书责任未结束时保留原证据。确认的 CA 验证拒绝在挑战清理后由原发布自动执行同样流程。签发结果未知时仍保留原回执，启动不自动提交 CA 或重载。

Linux Release 使用私有 systemd、Docker、Nginx、Pebble 和 DNS。专属 Pebble 验证端口不可达，实际创建一个订单并返回验证失败，未提交签发。发布 `0a923e1a-027b-4aa3-a483-af157d1c5aa2` 以清理 ID `1072ea25-10e7-43ef-9798-b9d7ee1a725e` 自动完成一次清理重载，原挑战和临时配置移除，原回执保持 `certificate.validation_failed`。正常发布对照 `8fe89344-f862-4a62-9595-5ad004c34479` 完成一次签发及两次预期重载。日志为 `.artifacts/mb05-bootstrap-cleanup-final--systemd-website-acme-bootstrap-failure.log` 与 `.artifacts/mb05-bootstrap-cleanup-final--systemd-website-acme-bootstrap-success.log`，归档 SHA-256 分别为 `4d00c410b9a43ce9e31cbb3d7cf9961119c3588ef848d6a1f508474a1e194e5c` 和 `fe681e2ef6cc09f2c71bb0719317ee7d2c3ed3e7ea8e5231854edd75e92ab2c8`。

Windows Release 模块里程碑回归通过，Docker 跨进程原 `8ce9da8e-6b46-4eba-bec7-e73965d70454` 返回 Interrupted，副作用计数为一。日志为 `.artifacts/mb05-bootstrap-cleanup-win-final.log`。Linux 首轮夹具的 Pebble 端口变量在外层展开失败，修正后通过。

授权清理最终复验中，发布 `78cf5d3d-bc48-4321-8c51-cc99e9bfd2ef` 的 Server 在原建路由重载期间被终止。重启后匿名清理、在途重载清理和被改动配置清理均被拒绝。原证据恢复后提交授权清理 `83cdfa90-c83a-4865-97f0-1ca16fba95e9`，实际清理重载开始后再次终止 Server。再次重启用原 ID 接续，移除匹配备份并持久化清理终态。同键仍返回原 Interrupted。总计一次建路由重载和一次清理重载，CA 零订单，原 HTTPS 保持。新键请求可进入证书校验并因测试证书不存在而结束，未新增重载或订单。各轮专属 cgroup 已清理，宿主 Nginx、Docker 和配置指纹一致。日志为 `.artifacts/mb05-bootstrap-cleanup-final-admission.log`，归档 SHA-256 为 `395b04e10dd7c5dd68e7a99cefe3fe09858903148d39bf4faa8c04e276a0e421`。

## MB-05 首站授权续接原证书

启动恢复保存原中断阶段。证书阶段中断、原证书操作已成功落盘、原应用仍为未关联的本地 IPv4 上游时，授权续接可以复用该证书。配置门禁内再次核验原路由字节、原锚点、原 Helper 重载成功及当前无原生作业，随后在原发布回执写入续接开始，再执行一次正式 TLS 绑定。原证书与发布 ID 保持，应用关联后持久化续接完成和验证检查。重复请求返回原回执。

若续接启动后再次中断，原回执保留起始时间及未核验责任。后续有原生成功证据的绑定对账见下节。现有自动签发仍由原证书操作负责。缺少已持久化的原中断阶段、原证书成功证据或原配置时保留核验门禁。

Linux Release 使用私有 systemd、Docker、Nginx、Pebble 和 DNS。测试中继在本地证书操作已经终态成功时暂缓交付实际 Helper 只读回执，此时终止 Server。原发布 `5dea1429-9724-4b2b-9bbb-31a330a2e6ad` 重启后仍返回 Interrupted。匿名及配置字节被改动的续接均被拒绝，且未持久化续接开始。恢复原配置后，授权续接使用原证书操作 `74bde98a-8f57-4ce5-8de3-258b14558cc8` 完成绑定，TLS 客户端收到实际 Docker 上游正文，原 Nginx master 继续服务旧 HTTPS。重复续接与原发布同键均返回同一回执，全程一次 CA 订单、一次签发和两次预期重载。

同制品授权清理对照原发布 `43b10f0f-d394-4947-996f-1257f93c9840` 与清理 ID `fad5db52-5da3-4026-9cb8-71e9098ae32f` 通过。对应日志为 `.artifacts/mb05-bootstrap-resume--systemd-website-acme-bootstrap-resume.log` 与 `.artifacts/mb05-bootstrap-resume--systemd-website-acme-bootstrap.log`，归档 SHA-256 分别为 `8accb0476fa556326965474ea2efd9a82897766c7dc6b81a4f7f1beb47419fba` 和 `68c574ff87b456402ccf795b32835bf769a63e476c7a45d981fc81cdf747d63b`。Windows Release 模块里程碑回归通过，Docker 跨进程原 `74250f92-5ba4-4d5e-b5f0-539553f16539` 返回 Interrupted，副作用计数为一。

二次中断最终复验原发布 `4021a173-ec05-40ed-b372-5e9076092baa`，原证书操作 `0ffcaa2d-266b-4410-999d-dfcd6734a43e`。续接已使真实 TLS 可达，原生重载仍在返回屏障内时再次终止 Server。重启后保持原回执、原证书和未核验责任，应用未关联。续接与清理均返回 409，原旧 TLS 和新 TLS 继续，仍为一次订单、一次签发、两次重载。初轮断连在夹具中打印预期的断管异常，收敛日志后复验通过。最终日志 `.artifacts/mb05-bootstrap-resume-final-interrupt.log`，归档 SHA-256 为 `2dc4e30151bb1a2654fa0d5258910aa615ef3b55ada6d3449b3a0b0389e09d84`。三轮专属 cgroup 清理，宿主共享服务和配置指纹一致。后续新增可核验的绑定证据见下节。

## MB-05 首站续接绑定的原生回执对账

授权续接在任何正式配置写入前，原子持久化独立 Helper 重载 ID、站点定义、正式配置 SHA-256 与证书指纹。续接使用该 ID 执行原生重载。失去终态时保留正式配置、原临时配置备份和原回执。再次授权续接只读取原生回执，核对站点定义、正式配置、证书指纹、原临时备份和当前上游。确认成功才补齐站点元数据及应用关联，并在二者持久化后移除匹配备份。备份已移除时必须已有匹配站点元数据和关联。原发布状态和原证书回执保持。

Helper 无成功证据、现有配置不匹配或原证书及上游已变更时，保留待核验责任。授权续接的正式 TLS 绑定先采用该流程，初次发布接入见下节。

Linux Release 在既有隔离环境用真实 Pebble、Nginx 和 Docker 复验。原发布 `549a59f9-fd3a-4814-973f-b124aaa37005` 在续接绑定 `81b6c554-6b35-435b-8d1e-ac09c41ad24b` 已服务 TLS、Helper 尚未完成原生重载时二次终止 Server。重启后仍拒绝正在运行的原作业，且拒绝正式配置或原备份被改动的路径。原文件恢复一致后，按原绑定回执补写站点和应用关联并清理备份。重复续接和发布原键返回同一原回执，原证书操作 `305c1a55-5b24-4672-9028-9952c9d9a9c5` 保持。旧 TLS 与新 TLS 都从原 Nginx master 返回真实正文，全程一次 CA 订单、一次签发和两次预期重载。

正常续接 `d5b1b454-8708-4a14-a135-c6b9575d8f50` 与清理对照 `9b51f6c7-d3b9-4554-b477-fe67a1a7d24a` 通过。日志为 `.artifacts/mb05-bootstrap-bind-reconcile--systemd-website-acme-bootstrap-resume-interrupt.log`、`.artifacts/mb05-bootstrap-bind-reconcile--systemd-website-acme-bootstrap-resume.log` 与 `.artifacts/mb05-bootstrap-bind-reconcile--systemd-website-acme-bootstrap.log`，归档 SHA-256 依次为 `b752f417b07621a16fdb91d459a6af556001cfd4302cde4d58a5ee379526d94c`、`d00afbe2adc4ebcc854bb83c115c5de10bdb5576571d24bdcb15b261bf9387dd` 和 `58faa3ce04f32455b1fb77feb6802c0ecb5b2fce47b165ef2c1459a860ac10eb`。三轮清理专属 cgroup，宿主 Nginx、Docker 和配置指纹一致。Windows Release 回归通过，Docker 跨进程原 `6a2602e1-01a7-40fd-be61-44d125f1ed31` 返回 Interrupted，副作用计数为一。首次构建因证书摘要类型误用失败，修正后通过。

## MB-05 首站首次 TLS 绑定的原生回执对账

自动建立 WebRoot 的首站首次发布与授权续接共用正式绑定记录。首次路径在写入正式配置前持久化独立 Helper 重载 ID、证书指纹、站点定义和正式配置摘要。正常发布在原生重载成功后核验这些证据，随后补写站点和应用关联、移除原临时配置备份。原回执按正常流程完成，首次成功路径没有续接时间。

首次正式绑定中断后仍按原发布 ID 授权续接。已记录绑定时，续接只核验原 Helper 成功回执、当前证书及上游、正式配置和备份，完成本地关联并保留原中断回执。原生结果未确认时保留证据并拒绝清理或继续写入。既有 WebRoot 和手动指定证书的发布保持原流程。

Linux Release 使用既有私有 systemd、Docker、Nginx、Pebble 和 DNS。首次发布 `04524788-ec36-4957-bc74-d2efd67a15fb` 在原 TLS 绑定 `1b64ec26-9b19-41f2-b0b2-fbd0c6b0dacd` 已提供真实 HTTPS、原生结果仍在途时终止 Server。重启读回原 Configuring 中断，续接和清理在 Helper 无终态时拒绝。原 Helper 成功后，续接保留原证书操作 `3b786c6e-cf4d-4716-b359-9539f88d00be` 和正式配置字节，补写站点及应用关联并移除备份。同键和续接重试返回同一回执，全程一次订单、一次签发、两次预期重载。原 Nginx master 与旧 HTTPS 持续服务。

正常首次发布 `553aa6d4-7a9e-4d11-8cfc-b9a62ba15165` 和授权续接二次中断 `12448c61-9fec-4155-ada5-d5111b225607` 同批通过。三轮均清理专属 cgroup，宿主 Nginx、Docker 与配置指纹一致。日志为 `.artifacts/mb05-first-bind-reconcile--systemd-website-acme-bootstrap-bind-interrupt.log`、`.artifacts/mb05-first-bind-reconcile--systemd-website-acme-bootstrap-success.log`、`.artifacts/mb05-first-bind-reconcile--systemd-website-acme-bootstrap-resume-interrupt.log`，归档 SHA-256 依次为 `3662d74aa8ecf9517313f2d1ff57f954c141c637d8007eabe4229e6c8460ebff`、`506ae122963dedc178334f597dbc17ee96c0612a7e82f088fa3f4a7bc4b3acb4`、`c74e6c315f2c2066c7a12441ae6fe5e6af994935a8d087387c31841d86b88298`。Windows Release 模块回归通过，Docker 跨进程原 `84c84432-35e1-43d2-b0b9-e670b703b28e` 返回 Interrupted，副作用计数为一。

## MB-05 网站 WebRoot 签发的取消与停机排空

已记录 CA 订单的 Cancelled 回执保留原订单身份。原 ACME 只读对账路由也接受该回执。续期已记录订单时，下一次自动扫描沿用原回执，直到另一次明确授权的续期成功。

同一 Linux 私有 systemd 设施中，Pebble 已实际签发，中继暂扣真实证书响应。认证取消返回原 Running / cancel-requested，挑战文件仍在。释放响应后，证书原回执为 Cancelled，网站发布原回执为 Failed / certificate.operation_cancelled。另一轮向完整 Server 发 SIGTERM，Server 先停止接收 HTTP，继续等待原 CA 响应且保留挑战文件。交付响应后，挑战清理与原证书回执落盘，Server 正常退出。重启后原网站发布标记 Interrupted。两轮均按原证书 operationId 查询 CA valid，原挑战清理，未新增订单、重载 Nginx 或提交新应用关联，旧 TLS 和真实 Docker 上游持续运行。

取消发布为 `aa53d0b9-acc2-42cd-9347-f74b67e7a2e9`，证书操作为 `9222c5ac-cd33-4477-b8a1-129fdef81146`。SIGTERM 发布为 `d9257bbe-d069-4461-ab3e-835a206a586c`，证书操作为 `e446e203-0669-4b79-a358-a2b4c654c792`。日志为 `.artifacts/mb05-acme-drain--systemd-website-acme-cancel.log` 和 `.artifacts/mb05-acme-drain--systemd-website-acme-drain.log`，归档 SHA-256 为 `00bd22b753e8caa35ff9bfe68de0572bc7c305223c96a5ee9833edc25d4f20c0`。Windows Release 模块边界回归覆盖重开台账后下一次自动扫描保留原取消回执。自然续期取消的 CA 实测见上文，主机掉电仍开放。

## MB-05 WebRoot 挑战内容变化时保留原清理责任

WebRoot 文件摘要不匹配时，原证书操作持久保留 `Running / challenge-cleanup-required`、原订单身份和原挑战记录。存活进程继续保留故障工作责任。重启时读取同一台账，保留内容不匹配的文件及清理阻塞，同时开放原回执、取消状态与原订单的认证只读查询。同键返回原证书或网站发布回执，新证书操作拒绝准入。文件恢复与原摘要一致后，在下次启动中完成原清理并写入原中断回执。远端订单有效性和本地签发、TLS 部署状态继续分别核实。

Linux Release 在私有 Pebble、DNS、Nginx、Docker 和完整认证 Server 上验证发布 `73e1a7e3-4ba7-41da-befa-db788ed9c022` 与证书操作 `2ae08813-0c24-479d-93f9-7b20007b7e04`。CA 已实际签发，响应暂扣期间换入不同字节。放行后原任务保留待清理状态，强制终止 Server。重启仍可查原回执与原 CA `valid`，授权的新证书操作返回 HTTP 409，替换文件保持且可由原 WebRoot 读回。恢复原字节后再启动，原文件被清理并持久化原证书中断回执，测试哨兵保留。旧 TLS 与 Docker 上游保持，CA 一次创建及签发提交，Nginx 零重载。

正常 SIGTERM 对照发布 `c0240e22-2771-427b-9e47-b7f6a386fec7` 同批通过，原订单可查、挑战移除、无额外重载。两轮专属 cgroup 清理，宿主 Nginx、Docker PID 与配置指纹一致。日志为 `.artifacts/mb05-acme-challenge-final/live.log`，归档 SHA-256 为 `aaa08de7ce0c574ed4ff83bc32bd1b3424f3cfd6e815929f485d02deb3522f62`。Windows Release 验证清理阻塞时自动续期扫描保持待处理，同键回执稳定，MB-05 原 `a06c0e90-266d-4a46-9baa-257df0da32eb` 跨进程回执为 Interrupted，受控副作用计数为一。归属阻塞与实际自然日扫描的联合路径尚未实测，Certificates 与 WebServer 保持 resident，生产内存未测。

## MB-05 续期扫描跨挑战归属阻塞恢复

在私有 Pebble、DNS 和 Nginx 上签发短期证书。真实续期挑战响应在途时换入不同内容并终止 Server。重启后生产续期 worker 按原启动周期创建到期扫描，发现清理阻塞后保留扫描未完成及原证书操作。恢复匹配文件后再启动，由同一 worker 接续原扫描并关联原证书回执。测试使用 Pebble 短期证书和原到期回退规则，隔离网络内的 PAM 夹具只承载认证与管理员检查。

Linux Release 验证原扫描 `a0ae423b-b106-480a-b0fe-cca9fdadcf65` 和原续期 `aba20dc2-767d-488e-900a-16cd868920ff`。第一次实际扫描保留空完成时间及零拒绝，旧文件和活动证书指纹保持。恢复原挑战字节后，下一次自然扫描使用同一 ID 和开始时间，关联原 Interrupted 回执。原键和原订单仍可读取，活动证书指纹一致。Pebble 共两张预期订单、一次签发提交，恢复零新增。强制终止首次 Server 后，测试中继向已关闭 socket 交付原响应得到 BrokenPipe，后续真实扫描和 CA 计数均通过。宿主 Nginx、Docker PID 与环境文件指纹保持。日志为 `.artifacts/mb05-natural-renewal-ownership/live.log`，归档 SHA-256 为 `34652357bb4a0846d608c2d5e82d704ded4877a8c7fe902d2e751a0f24e9304e`。Windows Release MB-05 夹具按原 `a1c1ff2d-364f-4d94-869d-fbedabee9988` 返回 Interrupted，受控副作用计数为一。

## MB-05 Nginx 显式证书轮换与原重载回执

验收使用 Linux 专属 systemd、生产 Helper、Nginx、独立 Docker 上游和 Pebble。认证网站发布绑定证书仓库 ID。原续期成功并落盘后，真实 TLS 新握手仍提供旧证书，直到管理员提交独立的 Nginx 重载。受信客户端核对域名与叶证书 SHA-256。新 worker 装入替代证书，原 Nginx master、网站关联、配置字节和独立 HTTPS 站点保持。

测试在 Nginx 已发送真实 reload 信号后暂扣 systemd 作业完成。有原生回执的路径终止 Server，原 Helper 持续等待。释放作业后，Helper 持久化原结果，Server 重启按原 ID 完成并退休详情。无原生结果的路径同时终止 Server 与 Helper，真实 TLS 可观测到替代证书。恢复责任仍由原 WebServer 操作持有，TLS 指纹不能替代原生回执。

确认场的原续期为 `f624341d-7f74-4c54-b778-358de21dcf85`，重载 `01772633-de75-4b14-947e-b8f8e049da12` 恢复 Succeeded，同键和原 ID 一致，回执退休后 Helper 拒绝旧 ID。未知场的原续期为 `5ae804a6-7f75-4c9e-abd3-70602192bae0`，重载 `4e159d29-9d5d-4385-b1a0-1ca0e3cbbad0` 保持 Running / `webserver.native_recovery_required`，保留工作租约，新键返回 409。两场各两个真实 HTTP-01 token、两张 CA 订单、两次签发提交，只有首次发布及原轮换各一次 reload。共享 Nginx、Docker 和站点文件指纹一致，专属 cgroup 已清理。

首轮已观察到轮换后的真实握手，但发布后虚拟主机继承的全局访问日志关闭，夹具无法核对第二个 HTTP-01 token。补齐专属访问日志后，确认、未知及原网站签发三场均退出 0。Windows Release WebServer、站点并发、证书绑定和模块边界回归通过，Docker 跨进程夹具原 `9bedb315-1ec4-4d9d-8962-6cbb36922fae` 为 Interrupted，受控副作用计数为一。日志位于 `.artifacts/mb05-nginx-rotation/linux-final.log`，归档 SHA-256 为 `5e0010613f4b2097108f992aef4cc79c1eb5e05964f5ec128a4d4bccbe9d82fc`。

本节验收显式重载链路，自动部署见下节。整机掉电与生产内存测量继续开放。

## MB-05 Nginx 续期自动部署

成功续期的证书版本与原操作在同一台账提交，自动部署协调器接管后先持久化受管目标。待处理列表只保存尚未完成投影的实例。同证书同版本同实例使用原 WebServer reload 幂等键，执行与恢复沿用现有 WebServer 操作、模块租约和 Helper 原生回执。无待处理续期时协调器等待成功续期信号。

每个目标在派发前核对当前证书版本、实际配置与受管站点定义、Nginx 运行状态及配置测试。校验期间保留原证书串行门禁与 Nginx 集成门禁。自动工作以 `renewal-worker` 记录，使用现有固定命令，站点配置保持。SQLite 部署记录保存原续期和原重载 ID、目标版本、最近由协调器确认的成功版本及独立结果。证书续期终态与 Nginx 结果各自持久化。

已确认失败保留新 PEM 与原失败回执，管理员可修正原因后显式重载。同一个自动操作不会重派；缺少原生证据时保留原待核验回执和门禁。已确认的 Helper 结果只做原 ID 对账及清理。

最终 Linux 复验使用专属 systemd、真实 Pebble、生产 Helper、Nginx 和独立 Docker 上游。共用证书的两个站点经一次自动重载切换到新指纹，原续期 `425a648a-c0c9-4364-9dbb-288ad66b7e86` 与重载 `5a85848d-c139-4d68-8385-a5cb0249fa68` 重启可查。单站确认场原重载 `dc593e62-abda-4f51-ad06-a5f89ba810cd` 以原 Helper 回执完成。双进程中断场原重载 `319dd454-f29b-4b78-91d0-fa6ed26b4623` 在替代证书已提供服务时保持待核验，拒绝新键，原工作租约继续持有。

真实配置检查拒绝的原重载 `90ad8ccf-5664-4712-a5de-1a0a32e7cbff` 和专属 systemd 原生作业拒绝的 `4f27a1dd-4cb5-497d-a589-076980d042eb` 均记录独立部署失败，原续期保持成功，旧 HTTPS 持续。各续期场均只产生预期的两张订单和两次签发提交，恢复无附加写入。最终以原续期的实际挑战文件核对访问日志，以旧 Nginx worker 退出和新握手核对轮换收敛。此前一次全量日志 token 计数和一次旧 worker 尚未排空时的握手断言失败，均保留在前次日志中。

Linux 六场退出 0，宿主共享服务与文件指纹一致。Windows Release 证书、WebServer 和模块边界回归通过，Docker 跨进程原 `09314bc2-2e7f-410c-98c0-03e4449676f7` 返回 Interrupted，受控副作用计数为一。日志位于 `.artifacts/mb05-nginx-auto-renewal/linux-final.log`，归档 SHA-256 为 `9fe1b07f64763b9a6bbab1a0cd9d127e0db227f10b58ce119e634405bfc1483a`。

## MB-05 Nginx 自动部署准入前的进程中断

先用完整认证 Server 发布站点，再重启独立 Server 并停用 WebServer 管理模块。原 Nginx master、旧叶证书和 Docker 上游继续服务，管理路由返回 404。实际 Pebble 续期 `eb23802c-d9d6-440a-b629-32db659983be` 成功后，证书台账持久化新版本及尚未分派目标的检查点。WebServer 台账字节、部署记录和原生 reload 计数核对均无新增，Nginx 仍提供旧指纹。

强制终止该 Server 后重新启用 WebServer。生产协调器接续原续期，仅准入重载 `9fdf62e3-4f66-416c-a470-6266b5135fbc`。新指纹在实际 HTTPS 握手可见。重载期间再次终止 Server，原 Helper 确认后再启动，原回执恢复 Succeeded，部署记录与证书版本匹配，工作租约归还。首次发布和自动轮换各一次 reload，Pebble 共两张订单和两次签发提交。检查点在部署对账完成后清除。双站分组与未知回执场景同批回归，宿主共享服务和文件指纹一致。

Linux 三场均退出 0。证据在 `.artifacts/mb05-nginx-before-admission/linux.log`，归档 SHA-256 为 `1fe5106390a88c746a55cd5568c47f9524d8bad06813530867d05b58d6dd7c5a`。Windows Release 证书续期、WebServer 和模块边界回归通过，Docker 跨进程原 `10baaeb2-08f2-4b70-8d31-854aa5fbb7df` 恢复 Interrupted，受控副作用计数为一。

本轮采用管理模块停用来建立准入前边界。模块启用时的目标检查点恢复见下节。整机掉电、其他平台的真实 Nginx、独立部署查询界面和生产内存测量仍开放。Certificates 与 WebServer 保持 resident。

## MB-05 Nginx 自动部署的目标检查点恢复

完整认证 Server 保持 WebServer 管理启用。在独立 Linux systemd 环境内，以同名目录阻断 WebServer 台账临时文件的实际打开。Pebble 续期 `63f847e4-dfed-4fe3-b674-20ecec0da0e4` 已成功，证书台账保留新版本和准确的 Nginx 目标。WebServer 原 owner 显示 `module-recovery-blocked` 并持有工作租约。原 WebServer 台账字节、部署记录及原生重载计数均表明新重载尚未持久准入，旧 HTTPS 指纹和 Docker 上游继续服务。

强制终止 Server，确认原检查点保持，再移除独立故障目录并重启。以原续期键和原 operationId 读取同一回执。协调器从原检查点仅持久准入重载 `e10c0f39-8ed5-4fb2-b6c8-25b3a6066f8d`，新指纹由实际 HTTPS 握手确认。重载期间再次终止 Server，原 Helper 完成后，重启以同一重载 ID 得到 Succeeded。原生回执退役及工作租约归还后，证书检查点清除，WebServer 台账只存在一条对应键的成功回执。Pebble 仍只有预期的两张订单和两次签发提交，原生轮换重载恰好一次。

Linux 四场退出 0。管理停用、双站分组和未知原生回执对照均通过，未知回执 `6741d85b-dfae-4566-8463-b09a3d6ba5b5` 继续持有原恢复责任。每场清理专属 cgroup，宿主 Nginx、Docker 及文件指纹一致。日志在 `.artifacts/mb05-nginx-blocked-admission/linux.log`，归档 SHA-256 为 `3dd4963c7999308a5a162c84b0e2d7650b020fd13b0ff4d396c96a1f0af79934`。

Windows Release 证书续期、WebServer 和模块边界回归通过。Docker 跨进程夹具以原 `44464f7d-4b88-4682-8319-0b193b68e91c` 返回 Interrupted，受控副作用计数为一。重载回执已落盘但执行尚未派发的场景见下节。

## MB-05 Nginx 自动重载在派发前的原回执恢复

在专属 Linux systemd 内让 SQLite 触发器拒绝自动重载投影的实际写入。Pebble 原续期 `a7c6be2f-5c91-425d-972e-28148cf8d860` 已成功，证书目标已持久化。WebServer JSON 台账保留原 `47eec065-1c87-4d2f-8651-eb596e7495d2` 的 Queued 回执，SQLite 尚无对应行。原 owner 显示 `module-recovery-blocked` 并持有工作租约。原生尝试计数和旧 HTTPS 指纹保持。

强制终止 Server 后移除独立触发器，重启时原 WebServer owner 以同一重载 ID 写入 JSON 终态和 SQLite 投影。回执为 Failed、阶段 `interrupted`，错误 `webserver.operation_interrupted`，开始时间为空。证书续期回执继续成功，独立部署记录指向原重载并记录失败。对账后工作租约归还，后续再次重启仍返回同一回执。真实 Nginx 与 Docker 上游继续提供旧 HTTPS，自动重载实际尝试为零。Pebble 仍只有预期的两张订单和两次签发提交。

Linux 五场均退出 0。目标检查点、管理停用、双站分组和原生回执未知对照通过，宿主共享 Nginx、Docker 与文件指纹保持。日志在 `.artifacts/mb05-nginx-blocked-projection/linux.log`，归档 SHA-256 为 `de0709d113d8a2e1743e229d6272fb8219fff9d6a3ae0a95e4099e197b7a31ce`。

## MB-05 直接 WebRoot 启用执行器

显式启用 ACME HTTP-01 的标记事务、站点分派和结果处理复用 WebServer 可回收代次。Host 继续持有实例锁、配置和文件归属检查、原站点台账与实际 Nginx 命令。实际 reload、补偿、文件清理及原操作状态落盘后才释放执行租约。清理无法确认时原工作 owner 保留故障责任。

Linux 系统包 Nginx 的 Helper reload 应答丢失时，显式启用记录原 `operationId` 和 `webserver.site_reload_unverified`，保留活动文件与原备份。新启用请求在站点备份检查处停止，已知拒绝才进入补偿。原作业中断时同键继续读取原 `webserver.operation_interrupted` 回执。后续显式站点更新仍须核对原文件、备份、站点定义和原生作业已排空。该阶段的 WebRoot reload 未写入逐站点的 Helper 成功回执。逐站点回执与清理恢复见下文。

Windows Release 的 WebServer 和模块边界回归通过。跨进程夹具按原 `2402a136-0d7e-44ff-820d-36fe8a126c69` 返回 Interrupted，受控副作用计数为一。Android Nginx 管理与回执相关 26 项单元测试通过。构建边界的正向检查与禁止依赖检查通过。

专属 Linux Nginx、生产 Helper 和独立 Pebble 共通过五个场景。成功启用 `7cd75d2d-4e8c-473b-8989-d9a7be99e4dc` 的真实 reload 在途时持有租约，清理和终态后旧代次卸载并被 GC 收集，同键查询无加载或 reload。进程中断 `79d37444-bed4-40ef-972c-258db9753982` 和真实 Helper 成功应答丢失 `39352c8f-41a6-46fb-ba65-4ec8adebf1b6` 均保留原回执与活动配置。应答丢失后，新键被原备份拦住，只有另行授权且经证据核验的 TLS 绑定才重载。三轮首站签发各创建一个 CA 订单并维持原 Nginx master。

已知拒绝 `15e8ccea-16e2-42f1-bbf9-cd8f9996a809` 恢复原配置并确认一次补偿，备份清理后原 TLS 持续服务。直接 reload 的取消与 SIGTERM 排空同制品复验通过。首次夹具因 CRLF 在 Bash 解析处退出，修正为 LF 后通过。专属 cgroup 已清理，宿主服务与配置指纹一致。日志见 `.artifacts/mb05-webroot-execution/linux-final.log`，归档 SHA-256 为 `806764bf5a537d32fa6f86207f7b461fa2f611c2688c61cf800d3aad7994f902`。

WebServer 整体保持 resident。逐站点回执与清理确认的后续验收见下文。非系统包 WebRoot 原生故障矩阵、整机掉电和生产内存继续开放。

## MB-05 WebRoot 逐站点回执与清理恢复

Linux 系统包 Nginx 的显式 WebRoot 启用沿用原工作租约、可回收执行代次与 Host 配置门禁。父操作先记录站点顺序，每个站点在暂存前记录独立 Helper reload ID 和原配置、候选配置摘要。已派发的原生命令、文件清理、终态写入及回执退休都由原 owner 排空。已知拒绝的在途执行另记一次补偿 ID，确认恢复原配置后才释放原备份。

重启与应答丢失时，原台账只观察当前已派发子项。匹配的成功回执和配置摘要可确认原备份清理，备份已删除也可凭同一证据继续。暂存或备份内容漂移时保留文件和门禁。父操作尚有未派发站点时以原 ID 收尾为中断，未派发站点保持原配置。父操作终态保留已确认的子 ID，释放在途计划和配置摘要，再退休 Helper 回执并保留防重放标记。待核验期间拦截同实例新的后台变更和站点写入。

Windows Release WebServer 与模块边界回归通过。跨进程夹具按原 `a0848e86-4182-4852-87d1-3b009a7d35af` 返回 Interrupted，受控副作用计数为一。

隔离 Linux 的正常启用 `a73361c9-32a0-44a2-821d-1d95f1f7185c` 确认终态和子回执清理后代次被 GC 收集。实际 reload 期间的进程中断 `95f168c4-e48a-492b-873e-244ef2a6b88d` 在备份漂移时继续等待，恢复匹配后完成原回执。应答丢失 `2ab5cfb7-fe5a-417e-8f79-fdc5c299a21b` 由原子回执确认，已知拒绝 `afd31296-c8a9-40e4-9366-2a129af10d18` 只进行一次受跟踪补偿。可签发场景各使用原 Nginx master 和一个真实 Pebble CA 订单。

双站点父操作 `c2768147-f1f4-4c64-9276-00bd97859e1e` 的首个子 ID `4f649c02-ab73-4369-9ccd-6298e70201c8` 已真实重载，重启仅核对并清理该子项。第二站点字节一致，原父回执为中断。实际备份删除应答暂扣时终止 Server，父 `9fbe3c27-b6f9-4fd0-8d4b-2353d2ef92f2` 和子 `ec072863-b93d-4887-a197-321473c8178c` 重启后确认同一清理及终态。子 ID 留在原父台账，旧 ID 被 Helper 拒绝再次重载。直接 reload 的取消与 SIGTERM 排空也通过。七个隔离场景的专属 cgroup 已清理，宿主服务与配置指纹一致。日志为 `.artifacts/mb05-webroot-receipts/linux-final.log`，归档 SHA-256 为 `682d32e8f392c16825175df2b0a88dacaf6518bb8c3029d6b61ebd98c3df742c`。

WebServer 保持 resident。已知拒绝与补偿派发之间的恢复见下节。缺失原生终态、整机掉电及非系统包 Nginx 的同等故障矩阵继续开放。生产内存未测量。

## MB-05 WebRoot 已知拒绝的具名补偿

原站点 reload 已由 Helper 持久记录失败，但 Server 在补偿派发前中断时，具备原实例 Nginx 配置写入权限的操作者可确认后对原 WebRoot 操作发起具名补偿。受理要求原生作业空闲，原候选配置和备份摘要匹配，原暂存和补偿暂存均不存在。原台账在修改文件前持久化唯一补偿 ID 与操作者。后台工作租约负责恢复原字节、配置验证、跟踪 reload、清理及终态提交。

重复请求返回同一补偿 ID。重启只核对已有补偿回执与实际配置，待核验期间保留实例写入门禁。匹配的补偿成功回执和配置摘要确认后清理备份，原父回执以 `webserver.site_reload_failed` 收尾并保留 `compensationOperationId`，原 reload 与补偿分别保留防重放标记。原生终态缺失，或补偿已记录但没有可核验的成功证据时，原操作继续待核验。

Windows Release 编译、WebServer 和模块边界回归通过。跨进程夹具按原 `d731757c-16d5-4743-9809-366c8a94bbcf` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 中，先在原生 reload 持久拒绝后、Server 收到应答前终止 Server，再在实际补偿 reload 运行期间终止后继 Server。重启以原父 `1663051f-0dbd-4169-8f34-9547c057b33e`、原站点子项 `1656611a-9c21-40b4-a33f-d1269530f6ec` 和唯一补偿 `2b072aaa-1d4a-43ed-a27f-24aeb4afdc7f` 收尾。重启未派发原 reload，重复请求只读原回执，实际补偿一次。匿名、未确认和备份漂移均未派发补偿。补偿执行期间备份与工作责任保持，终态后两个 Helper 回执均退休，旧 ID 再次执行被拒绝。原 Nginx master 与 HTTPS 持续服务，恢复进程无需加载 WebServer 执行程序集。

同批通过正常在途失败的自动补偿、首站原 CA 订单恢复、双站点中断及集成具名补偿。专属 cgroup 均已清理，宿主服务与配置指纹一致。首轮夹具在主动终止 Server 后记录了预期的断管，修正夹具后五项复验通过。最终日志为 `.artifacts/mb05-webroot-named/linux-final.log`，归档 SHA-256 为 `76a644178e7f92f569dc04198eaed5d11dc6021079e44a12db8e3c765b3f31a9`。

WebServer 整体保持 resident。整机掉电和其他 Nginx 安装形态仍待验收。
