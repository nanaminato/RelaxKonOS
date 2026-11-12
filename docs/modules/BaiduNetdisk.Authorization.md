# 百度网盘设备码授权

百度网盘授权在**用户自己的浏览器**完成。远程 RelaxKonOS 服务端只申请短期设备会话、按提供方要求轮询并保存加密凭据。无需在远程服务器弹出浏览器，也无需用户复制 refresh token、在客户端填写应用 Secret 或配置公网回调地址。

## 管理员配置

1. 在百度网盘开放平台创建并审核自己的应用，取得应用 AppKey（Client ID）及 SecretKey（Client Secret）。所需权限为 `basic,netdisk`。
2. 管理员在 RelaxKonOS 中配置一次 `baidu-netdisk` 应用信息。应用 Secret 只提交给受信任的 RelaxKonOS 服务器，通过 HTTPS 或受信任的加密连接操作，不放进二维码、客户端构建变量或日志。
3. 服务端用现有 CloudStorage 存储密钥 AES-GCM 加密应用配置和连接 refresh token。密钥与数据处于同一服务器，不能防御服务器账户失陷；按服务器凭据保护和备份，不上传到仓库。

## 用户授权与会话

- 创建授权会话，客户端展示百度返回的用户码、官方验证链接及可用的官方二维码链接。用户在本机浏览器登录百度并确认授权，可使用另一个设备扫码。
- 返回客户端的是会话 ID、用户码、验证/二维码地址、间隔、到期时间和状态。设备码、应用 Secret、access token、refresh token 均不作为客户端会话数据返回。
- 服务端只接受 `openapi.baidu.com/device` 路径下的官方验证/二维码链接。历史 HTTP 链接仅对这一受信任的官方来源升级到 HTTPS，拒绝其他域名、用户信息、非标准端口及其他路径。
- 轮询遵守提供方 `interval`；收到 `slow_down` 后增加 5 秒间隔。前端频繁刷新不增加真实上游轮询频率。会话按百度 `expires_in` 与服务器短期会话上限中较早者过期。
- `authorization_pending` 保持等待；拒绝、到期或其他失败转为终态。取消、配置更新、会话过期及成功均清除内存中的设备码和授权链接。服务器重启后需重新发起未完成的授权。
- 每个会话归属已认证用户，不能查询或取消其他用户的会话。服务器限制并发会话总数及单用户待授权会话数。
- 成功后保存新的用户连接。授权会话取消只取消尚未完成的连接流程，不代表撤销百度账号已经授予应用的权限；如需撤销，请在百度账号授权管理中处理。

## 协议与排障

使用百度官方 SDK 对应接口：

- `GET https://openapi.baidu.com/oauth/2.0/device/code`：`response_type=device_code`、`client_id`、`scope=basic,netdisk`。
- `GET https://openapi.baidu.com/oauth/2.0/token`：`grant_type=device_token`、`code`（不是 `device_code`）、`client_id`、`client_secret`。

百度设备码接口不是 Google 的授权码 + PKCE 流程。本实现不假设百度支持 PKCE，也不套用 Google 的回调或参数。

官方 token 接口按 SDK 使用查询参数，包含 Secret。禁止记录完整请求 URL、上游原始响应或异常详情；生产代理/APM 也应过滤 OAuth 请求地址。应用错误仅返回固定失败信息，不包含 Secret 或 token。传输层禁止重定向并校验目标网络地址，OAuth 响应限制 64 KiB。

出现失败时重新发起会话，检查应用是否有网盘权限、服务端网络及应用配置。不要让用户提交账号密码或把 token 粘贴到 issue。该流程不保证所有百度开放平台账户/应用都已开通设备授权权限，需管理员使用自己的已审核应用做一次正式验收。

## 测试边界

`BaiduDeviceAuthorizationChecks` 使用模拟 HTTP 上游验证官方请求参数、编码、错误状态、间隔/有效期数据、官方 URL 校验、响应大小、格式错误、取消和敏感信息脱敏。`Tests/Server/RelaxKonOS.BaiduAuthorizationSessions.Tests` 通过真实模块 HTTP 路由与模拟提供方验证用户隔离、管理员配置、加密存储、间隔与 `slow_down`、取消/保存竞态、请求途中过期、配置轮换和待授权限额。不访问真实百度账号，不使用私有生产数据。不把模拟协议验证等同于真实平台授权验收。

官方接口来源：
- https://github.com/baidu-netdisk/baidu-drive-sdk-go/blob/main/baidudriver/api/auth_device_code.go
- https://github.com/baidu-netdisk/baidu-drive-sdk-go/blob/main/baidudriver/api/auth_device_token.go
