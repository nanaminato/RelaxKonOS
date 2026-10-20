# Mihomo Linux 生产 HTTP 验收

`mihomo_http_acceptance.py` 只通过生产 HTTPS 路由执行变更，覆盖 Server 安装台账、独立 Helper IPC、签名目录下载、原生校验及真实 systemd 服务。`systemctl show` 仅用于观察，服务启停仍通过生产 Proxy 路由。测试要求专用 Linux System 宿主及当前 Mihomo 未安装；它会安装、更新、修复、回退、保留数据卸载/重装，最后删除本次安装的数据。不要对已有业务实例运行。

## 签名来源

在测试宿主之外保管签名私钥。准备机 Python 环境需已有 `cryptography`。生成器只接受既有真实归档测试中核对过 SHA-256 的 `v1.19.10` 和 `v1.19.30`，不信任新上传文件的自报摘要：

```sh
python3 Tests/ManagedRuntimes/prepare_mihomo_http_fixture.py \
  --source mihomo-http-acceptance-unique-id \
  --base-uri https://192.168.1.7:18443/ \
  --older-archive /local/mihomo-linux-amd64-v1.19.10.gz \
  --newer-archive /local/mihomo-linux-amd64-v1.19.30.gz \
  --output /local/new-private-fixture-directory
```

只将 `catalog.json`、两个原始归档与 `policy.json` 交付测试机；`publisher-private.pem` 留在准备机。以证书有效、IP SAN 匹配的 HTTPS 服务提供目录和归档，Helper 必须使用宿主正常 TLS 信任。不要关闭证书验证。生成策略只允许该精确 RFC1918/ULA 地址与端口，无重定向授权，清单有效六小时。

用测试机上已安装的 Helper 本机管理员入口审查并明确确认策略，不能通过 Server HTTP 传入公钥或 URL 建立信任：

```sh
RelaxKonOS.PrivilegedHelper runtime-source inspect /protected/policy.json
RelaxKonOS.PrivilegedHelper runtime-source confirm /protected/policy.json 0 <inspect返回的指纹>
```

使用新的 sourceId，已有来源不得通过删除保护账本重新初始化修订/历史。测试源的发布者身份明确为本地验收，不代表上游官方发布者认证。

## 执行

在指定 Linux 测试机执行。密码放在命名环境变量中；脚本不把密码、token 或配置秘密写入日志、命令行参数或证据 JSON。

```sh
read -rsp 'Host password: ' MR_TEST_PASSWORD; export MR_TEST_PASSWORD; echo
read -rsp 'Administrator password: ' MR_TEST_ADMIN_PASSWORD; export MR_TEST_ADMIN_PASSWORD; echo
python3 -u Tests/ManagedRuntimes/mihomo_http_acceptance.py \
  --server https://192.168.1.7:5000 --confirm-test-host https://192.168.1.7:5000 \
  --ca /protected/server-certificate.pem --username nanami --administrator root \
  --source mihomo-http-acceptance-unique-id \
  --older-release mihomo-http-acceptance-unique-id-v1.19.10 \
  --newer-release mihomo-http-acceptance-unique-id-v1.19.30 \
  --evidence /protected/mihomo-http-evidence.json
unset MR_TEST_PASSWORD MR_TEST_ADMIN_PASSWORD
```

81 项检查包括每个动作的精确计划、冻结键、同键重放、台账终态与实测修订；首次安装不启动/不开机启动；独立安装目录中的五项固定 GEO 资源与重复准备；真实 mixed 监听的 HTTP 转发；跨版本更新恢复运行状态；同版本 Repair；回退保留当前发布配置；保留数据重装维持配置及凭据身份；最终删除数据后不存在待恢复事务。证据仅保存操作 ID、动作及目标 releaseId。

失败时不要盲目重新运行：先查询原操作 ID、安装与恢复接口，解决本次命名事务。不能用手工删除运行时根或保护账本制造“未安装”。验收结束将测试源 `enabled` 改为 `false`，通过 inspect/confirm 提交当前预期信任修订；保留信任/目录历史，停止本次镜像服务，撤下仅为验收加入的 CA 和 TLS 临时私钥。

该流程只证明声明范围内的 Linux 归档运行时和 DIRECT 基线，不证明 provider/凭据资源分发、TUN 网络事务、Android 设备交互或 Windows 端到端。
