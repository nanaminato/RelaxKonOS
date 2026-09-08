# RemoteOS 应用私有配置存储

`AppSettings` 是 RemoteOS 为内置应用和 `.roapp` 外置应用提供的服务端配置存储。它保存应用自己的、小型、版本化 JSON 文档；不替代 Workspace 系统偏好、可查询的业务实体或机密存储。

## 1. 何时使用

新应用默认应使用 AppSettings 保存跨登录、跨服务端重启的应用偏好，例如刷新频率、视图模式或编辑器选项。

- 系统桌面偏好（主题、壁纸、默认程序）继续使用 `WorkspacePreferencesDto`；它们影响 Shell，需要强类型校验与即时同步。
- 书签、历史、待办、文档等需要逐项查询、排序、删除的业务数据使用独立领域表/API。
- 密码、访问令牌、私钥不得写入 AppSettings；应使用专门的机密存储。
- 临时状态和未保存文件内容不应持久化。

内置应用可直接注入客户端的 `IAppSettingsClient`；不需要经过 SDK。外置应用只能通过 `IExternalAppContext.SettingsStore` 调用，宿主自动绑定其 manifest `AppId`，不会向包暴露 RemoteOS 登录令牌。

## 2. 隔离与范围

每份文档由 `(UserId, Scope, ScopeId, AppId, Key)` 唯一确定：

| Scope | ScopeId | 适用场景 |
| --- | --- | --- |
| `user` | 当前用户 | 所有 Workspace/设备共享的偏好 |
| `workspace` | 当前 Workspace | 默认范围；同 Workspace 的设备共享 |
| `device` | 当前设备 | 仅此客户端设备的 UI 或硬件偏好 |

服务端从 JWT 中取 UserId、WorkspaceId 与 DeviceId，客户端不能自行指定 ScopeId。AppId 必须匹配 `^[a-z0-9][a-z0-9.-]{2,127}$`；Key 必须匹配 `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`。每个 JSON 文档最大 64 KiB。

## 3. REST 契约

```
GET /api/v1.0/app-settings/{appId}/{scope}/{key}
PUT /api/v1.0/app-settings/{appId}/{scope}/{key}
```

`GET` 未找到返回 `404`，成功返回：

```json
{
  "scope": "workspace",
  "key": "default",
  "value": { "refreshIntervalMilliseconds": 1000 },
  "schemaVersion": 1,
  "revision": 3,
  "updatedAt": "2026-08-10T12:00:00+00:00"
}
```

`PUT` 请求体为 `{ "value": <任意 JSON>, "schemaVersion": 1 }`，并可带 `If-Match: "3"`。服务端使用 revision 乐观并发控制；若值已被另一客户端改动，返回 `409`。传 `If-Match: "0"` 可实现“仅在不存在时创建”；不传该头为无条件覆盖。

## 4. SDK 用法

外置应用从激活上下文取得存储能力：

```csharp
var current = await context.SettingsStore.GetAsync();
using var json = JsonDocument.Parse("{\"refreshIntervalMilliseconds\":2000}");
var saved = await context.SettingsStore.SetAsync(
    json.RootElement.Clone(), schemaVersion: 1,
    expectedRevision: current?.Revision);
```

默认 scope 与 key 分别是 `workspace` 和 `default`。应用升级其 JSON 格式时增加 `schemaVersion`，并自行兼容旧版本；服务端只保存 JSON 与版本，不解释应用字段。

## 5. 服务端持久化

SQLite 的 `app_settings` 表使用上述五个隔离字段作为复合主键，另存 `ValueJson`、`SchemaVersion`、`Revision` 与 `UpdatedAt`。`Revision` 是 EF Core concurrency token。当前项目仍采用 `EnsureCreated`，所以启动逻辑额外执行 `CREATE TABLE IF NOT EXISTS`，保证已有部署会补齐新表。

外置应用的 SDK capability 是宿主 API 的隔离边界。当前 `.roapp` 仍在客户端进程内加载；若要抵抗恶意原生/托管代码，必须另外引入进程隔离，不能把它视为代码沙箱。
