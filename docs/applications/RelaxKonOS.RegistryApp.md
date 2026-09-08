# RelaxKonOS Registry

> **状态：第一阶段已实现。**

## 定位与边界

内置应用 `relaxkonos.registry` 浏览服务器 schema 明确允许的配置型期望状态及其同步状态。它不提供宿主 Windows Registry、任意 SQLite 表、机密、会话或高风险命令的入口。

## 流程与信息架构

应用采用左侧键树、右侧值表和底部编辑器。用户可新建、修改和删除自己作用域内的逻辑注册表值；刷新从服务端重新读取。同步、版本和重启状态属于内部实现，不展示给用户。

## 边界、存储与升级

Protocol 定义键、值 DTO 及 REST 路由；Client 按当前打开的键请求其直接子键和直接值，绝不在点击 `HKEY_USERS` 或“当前用户”时拉取全量内容。服务端内存注册表持有键和值，修改会以短暂的 `PendingSync` 状态批量（5 秒）写回 SQLite，并在正常关闭时再刷新一次。SQLite 使用 `registry_keys` 持久化空键，`registry_entries` 以 `(UserId, Scope, ScopeId, Path, Name)` 持久化值；键和值都按相同的所有者边界隔离。Workspace 的终端外观、桌面偏好、浏览器设置和窗口布局均以各自键的 `(Default)` 值直接写入注册表；旧 Workspace JSON 配置不参与读取、迁移或回写。

## 平台、安全与验收

该应用和 API 在 Windows、Ubuntu 共享相同的托管实现，不访问任何 OS 注册表。路径由代码 schema 白名单控制，读取以 JWT 用户 ID 为租户边界；未认证、未知 scope 与其他用户数据不可访问。只有 `Workspace\…` 下的键可以由用户创建或删除；删除键会一并删除其子键和值。验收包括：两位用户的列表互不包含对方数据；新 Workspace 首次访问后有四个默认键；重复访问不改变 revision；应用可在离线或未登录时给出失败提示。

后续阶段可补充编辑历史、乐观并发和跨实例缓存失效。
