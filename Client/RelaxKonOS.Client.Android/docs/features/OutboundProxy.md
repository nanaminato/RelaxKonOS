# 宿主出站代理

BP02-M1/M2 接入宿主自定义及受管 Mihomo 出站代理。手机入口为“更多 → 网络 / 出站代理”，Docker 页面也可打开同一页面；入口和请求按 `server.docker` 门控，与当前服务端端点的宿主功能一致。平板从“更多”使用设置详情栏，从 Docker 打开的页面保留返回入口。

## 数据与编辑

`OutboundProxy.kt` 投影当前共享 [DockerProxyContracts](../../../../Shared/RelaxKonOS.Protocol/Docker/DockerProxyContracts.cs)，`RelaxKonGateway/RelaxKonApi` 调用 `GET/PUT/DELETE /api/v1.0/docker/proxy`。`DockerRepository` 复用 AuthSession 的认证刷新和精确会话检查；页面没有另一份本地宿主偏好或替代代理配置。该设置作用于当前服务器及其全部用户，不改变 Android 网络。

表单可启用/停用代理，编辑 HTTP_PROXY、HTTPS_PROXY 和 NO_PROXY，并分别选择引擎拉取、Server 发起的 Docker 构建、镜像标签查询和运行时下载。HTTPS 留空按服务端规则复用 HTTP；NO_PROXY 支持由服务端校验的逗号分隔绕过列表。保存后使用返回的设置重新填充表单，清除使用当前 DELETE 契约，不构造本地“已清除”结果。

`server.proxy` 存在时可选择受管 Mihomo，并打开同一 [Mihomo 管理器](Proxy.md) 完成安装、启动或修复。选择来源清空自定义 URL/凭据，保留四类消费范围和 NO_PROXY；选择仍需显式保存。既有受管设置显示实际解析的当前监听地址及可用状态，不在手机另存运行时地址。无管理权限的账号只能查阅，不能修改表单或提交。

## 确认与结果核实

保存和清除均先显示宿主影响确认。安装、替换或退役已安装的引擎代理可能重启 Docker 并中断容器；即便上次状态失败或已停用，API 也没有暴露内部 `EngineApplied` 标记，因此所有写入均确认，清除不依据层状态省略确认。确认发送此前捕获的表单，取消不提交，保存请求只在确认后携带 `confirmed=true`。

页面分别显示引擎/构建的 Disabled、Applied、RestartRequired、Unsupported、Failed 与已映射的问题/说明。构建使用 loopback 的不可达警告不会被“已应用”掩盖。Docker daemon 的实际 HTTP/HTTPS/NO_PROXY 与 Docker Desktop 上游及模式分别展示。

镜像查询与运行时下载显示已保存的消费范围设置：当前状态 DTO 没有这两类请求的实际网络观测字段，页面明确提示没有连通性或成功证据。引擎配置失败不推断所有消费者失败；实际代理解析及下载行为仍由 Server 管理。显式刷新状态立即重新解析受管监听地址；所选下载范围的来源不可用时拒绝创建 HTTP 客户端，保留稳定问题码。未选择的范围使用直连，NO_PROXY 绕过仍按宿主设置执行。本地 HTTP 消费验证见 Verification，真实宿主验收独立记录。

写入的传输错误、响应损坏或异常显示“结果尚未确定”，清空可提交的状态快照并禁用继续写入，先显式刷新核实。明确拒绝也要求刷新后再提交。失败不自动重放写入；401 只沿用 AuthSession 的一次刷新重试。代理设置 API 是同步写入，未创建安装/领域任务，因此不向 OperationIndex 写入虚假的任务 ID。

## 表单与秘密生命周期

URL 可含凭据，表单始终保存真实值供再次提交，默认遮挡 URL，可显式显示；daemon 与 Desktop 上游遵循相同显示选项。错误只显示本地化问题码映射，不输出原始服务端详情或异常文本。新增全部文案同步中/英/日。

编辑状态只驻留页面内存，不写 SavedState、偏好文件、日志、诊断或任务索引；离页取消页面观察与协程，旋转/重新进入重新读取宿主。精确身份变化取消 Repository 调用，旧结果不覆盖新会话。刷新、页面返回和跳转 Mihomo 均有未保存编辑确认，确认后到达原先请求的目标；清除确认明确说明将放弃编辑。页面可滚动并处理 IME，按钮纵向排列，适用于设置详情栏和大字体。

当前测试结果、被测提交与剩余验收见 [Verification](../status/Verification.md#12-bp-测试进度)。手机/平板交互及真实 Ubuntu/Windows 代理行为尚未验收。
