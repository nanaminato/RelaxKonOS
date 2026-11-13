# 云盘授权部署与验证

云盘是 `cloud-storage` 隔离模块的功能。正常用户无需填写应用密钥或刷新令牌。原有“高级手动凭据”连接仍作为明确的高级配置方式存在，已有百度连接可继续使用。

## 百度网盘

管理员一次性配置百度开放平台应用的 Client ID/API Key 与 Client Secret。用户获得短期设备授权码，在自己的浏览器/手机完成官方授权；服务端按官方返回间隔轮询，不要求用户复制 refresh token。`authorization_pending` 等待，`slow_down` 增加间隔，拒绝/过期终止。百度开放平台权限、配额及应用审核仍由应用管理员负责。

## API 与秘密存储

在 `CloudStorageApi.Route` 下：

- `GET /authorization/providers`：配置是否就绪。
- `PUT /authorization/configuration/{protocol}`：仅管理员，写入应用配置。密钥只写不读。
- `POST /authorization/sessions`：名称、provider、是否写入，以及可选的 `connectionId`。传入时只允许更新当前用户同 provider 的既有连接，保存时检查连接版本。
- `GET /authorization/sessions/{id}`：仅会话拥有者，查询/轮询。
- `DELETE /authorization/sessions/{id}`：仅拥有者取消尚未完成会话。

会话默认 10 分钟，设备码可更早到期；每用户最多 4 个进行中会话，全模块最多 256 条保留记录。状态为 `pending/completed/denied/expired/failed/cancelled`。配置更新令进行中会话失效，旧连接继续使用其原凭据。取消已完成会话不会删除刚创建的连接；删除连接与上游撤销授权是不同操作。

应用配置和连接刷新令牌使用模块现有 AES-GCM 密钥加密，存于模块数据目录；必须保护整个数据目录及备份。加密文件和解密密钥在同一主机，不抵御服务器管理员或主机失陷。授权会话仅保存在进程内，模块重启需重新授权；已保存的连接可继续刷新令牌。上游明确拒绝连接凭证时，模块返回 HTTP 422 和问题类型 `https://relaxkonos.app/problems/cloud-storage-credentials-rejected`。错误响应不包含上游 token 响应正文。

### 权限与运维边界

授权 API 沿用模块代理现有权限：查询要求文件读取权限；创建、取消及配置要求文件写入权限，配置额外要求管理员。仅有文件读取权限的账户不能新建连接，即使连接目标是只读；本次不放宽既有策略。授权 API 均要求登录。

授权处理中网络请求最多等待 15 秒；首版会话处理串行化以保证取消、配置变更与保存的原子顺序，尚未针对高并发多租户优化。

测试入口为 `Tests/Server/RelaxKonOS.BaiduAuthorizationSessions.Tests` 及 `Tests/ServerModules` 的 `--cloud-storage-checks`。这些测试使用合成数据，真实授权与文件传输需另行验收。
