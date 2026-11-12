# 云盘授权部署与验证

云盘是 `cloud-storage` 隔离模块的功能。正常用户无需填写应用密钥或刷新令牌。原有“高级手动凭据”连接仍作为明确的高级配置方式存在，已有连接不迁移、不重建。

## Google Drive

管理员在 Google Cloud 配置 **Web application** OAuth 客户端，启用 Drive API，配置同意屏幕及测试用户/应用审核。完整网盘浏览需要 Drive 受限 scope，`drive.file` 不能替代完整网盘浏览权限。只读连接请求 `drive.readonly`，启用写入请求 `drive`。

管理员一次性配置 Client ID、Client Secret 和 HTTPS 回调 URL：

```
https://<可从用户浏览器访问的服务器域名>/api/cloud-storage/oauth/google/callback
```

回调 URL 必须与 Google Cloud 登记值完全一致。此处是服务器 Web OAuth 客户端，不可混用桌面原生客户端密钥。不得将服务端地址误填为手机/桌面自己的 localhost。没有合适 HTTPS 入口的服务器目前不能使用此浏览器回调模式；不会自动打开防火墙、注册公网域名或启用隧道。

桌面由**客户端系统浏览器**打开 Google 同意页。服务器产生绑定用户、配置版本和短期会话的随机 state 及 PKCE S256；回调只完成已存在会话，不接受用户 ID。客户端通过其已认证连接查询结果。授权码、密钥与刷新令牌不返回客户端。

Android 使用 Google 官方 AuthorizationClient 的 `requestOfflineAccess`，使用同一个 Web 客户端 ID 获取一次性 server auth code，再经已认证 API 提交给服务器兑换。Android 应用还需在 Google Cloud 正确登记包名与签名指纹，并具备所需 Google Play 服务。Google 登录身份不等同于 Drive 授权，不能用 ID token 冒充授权码。Android 原生依赖和真机行为须独立验证。

## 百度网盘

管理员一次性配置百度开放平台应用的 Client ID/API Key 与 Client Secret。用户获得短期设备授权码，在自己的浏览器/手机完成官方授权；服务端按官方返回间隔轮询，不要求用户复制 refresh token。`authorization_pending` 等待，`slow_down` 增加间隔，拒绝/过期终止。百度开放平台权限、配额及应用审核仍由应用管理员负责。

## API 与秘密存储

在 `CloudStorageApi.Route` 下：

- `GET /authorization/providers`：配置是否就绪；Google 公开 Client ID 供 Android 官方组件使用。
- `PUT /authorization/configuration/{protocol}`：仅管理员，写入应用配置。密钥只写不读。
- `POST /authorization/sessions`：名称、provider、是否写入、`browser`/`android` flow。
- `GET /authorization/sessions/{id}`：仅会话拥有者，查询/轮询。
- `DELETE /authorization/sessions/{id}`：仅拥有者取消尚未完成会话。
- `POST /authorization/sessions/{id}/code`：仅拥有者提交 Android 一次性代码。

会话默认 10 分钟，设备码可更早到期；每用户最多 4 个进行中会话，全模块最多 256 条保留记录。状态为 `pending/completed/denied/expired/failed/cancelled`。配置更新令进行中会话失效，旧连接继续使用其原凭据。取消已完成会话不会删除刚创建的连接；删除连接与上游撤销授权是不同操作。

应用配置和连接刷新令牌使用模块现有 AES-GCM 密钥加密，存于模块数据目录；必须保护整个数据目录及备份。加密文件和解密密钥在同一主机，不抵御服务器管理员或主机失陷。授权会话仅保存在进程内，模块重启需重新授权；已保存的连接可继续刷新令牌。错误响应不包含上游 token 响应正文。

## 可复现验证

```
dotnet run --project Tests/CloudAuthorization
```

该测试使用自造响应，不访问用户网盘，验证 RFC 7636、scope、官方端点、Android 代码交换表单、响应大小和错误脱敏。另有 Core callback 参数过滤测试及百度设备码协议测试。真实同意页面、Google Cloud 配置、签名指纹、令牌撤销/过期及 Android 真机仍需部署者验收；单元测试通过不代表真实 OAuth 联调完成。

### 权限与运维边界

授权 API 沿用模块代理现有权限：查询要求文件读取权限；创建、取消、提交授权码及配置要求文件写入权限，配置额外要求管理员。仅有文件读取权限的账户不能新建连接，即使连接目标是只读；本次不放宽既有策略。唯一匿名入口是固定 Google 回调，并且不能指定连接拥有者。

反向代理/访问日志应避免记录 OAuth 回调的查询串，避免授权码和 state 落入日志。授权处理中网络请求最多等待 15 秒；首版会话处理串行化以保证取消、配置变更与保存的原子顺序，尚未针对高并发多租户优化。

测试入口：`Tests/CloudAuthorization`、`Tests/Server/RelaxKonOS.CloudOAuthCallback.Tests`、`Tests/Server/RelaxKonOS.CloudAuthorization.Tests`、`Tests/Server/RelaxKonOS.BaiduAuthorizationSessions.Tests`。百度 provider 检查同时纳入既有 ServerModules 测试入口。所有数据均为合成测试数据。
