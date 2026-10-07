# Android 网站发布

## 1. 入口与前置

“管理 → 网站”按 `server.web-server` 门控。读取受管实例、配置语法检查、站点和 TLS 关联；证书读取另需 `server.certificates`。无权限或未核实不显示为无站点/无证书。

发布需要运行中的应用、loopback 上游、受管 Web Server、可控域名与当前 `nginxConfigurationWrite` 授权。共享执行行为见 [Web Server 设计](../../../../docs/applications/RelaxKonOS.WebServerManager.Design.md) 和 [证书管理](../../../../docs/applications/RelaxKonOS.CertificateManager.md)。

## 2. 手机流程

选择应用与实例 → 输入域名并按提示配置 A/AAAA → 显式选择有效期内、状态可用且 SAN 匹配的既有证书（缺失/未核实/不覆盖时禁止提交，不自动改为申请），或填写联系人/同意条款/确认 HTTP 80 可达 → 确认目标与授权 → 提交持久发布操作 → 核实结果。

Android 不生成宿主 Nginx 配置，不缓存私钥、挑战凭据或 DNS token。站点关联只读取服务端 `siteId`，不从端口、域名或上游猜测。

## 3. 结果与恢复

服务端检查域名/站点归属和证书覆盖，受控写入经过 `nginx -t`、reload 与失败恢复，再绑定应用。发布账本持久保存幂等键、阶段和检查；每应用只允许一个活动操作，重启标为中断，不重放 ACME 或配置写入。绑定失败只还原本次站点变更，保留旧证书及应用进程。

分别展示上游、DNS、TLS 握手和 HTTP 检查的观察位置、时间与结果。配置完成但访问无法证明时显示未核实，不能把宿主 loopback 可达当公网成功。手机断网后按原操作查询，不自动重新发布。

停止应用、关闭访问、删除站点和吊销证书是独立动作。DNS 服务商自动化及内网发布仍属 [支持范围](../design/SupportScope.md)；不收集未接入服务商的凭据、不自动放开端口。真实 Nginx/ACME/DNS 与设备访问检查集中见 [验证要求](../development/Verification.md)。
