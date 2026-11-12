# Cloud authorization

The Files screen offers **Cloud connections**. It lists server-managed connections and creates a named Baidu Netdisk connection. Selecting a connection opens its cloud directory browser with foreground upload and download; host-file operations retain their existing behavior.

The server administrator must configure the provider application first. Provider application secrets and refresh tokens stay on the server. The Android client does not accept or store these credentials.

Baidu Netdisk uses a server-side device-code session. Android displays the user code and opens the official HTTPS verification page in the device browser. Return to the dialog after approval; status polling completes the connection. The device code and provider secret never reach Android.

Write access is off by default and must be explicitly selected. Pending sessions expose cancellation and expiry, and connection creation is reported only by the server. Leaving the screen, switching server/login identity, or losing the process stops local work; uncompleted sessions expire on the server. Screen disposal does not send a cancellation using a different login. In-flight provider-start responses are discarded after an identity switch. Challenges are memory-only and excluded from their debug representation. Provider responses and exception text are not displayed as diagnostics.

## 多账号文件浏览与传输

文件页的云存储入口列出每个连接的名称、提供商和连接 ID 短标识。同一提供商可重复授权多个账号；连接不会按提供商名称合并。选择连接后可列目录、返回上级、刷新和加载下一页，文件引用和分页游标均保持服务端返回的不透明值。

点击文件使用 Android 文档选择器选择下载目的地；仅在服务器接受请求后打开输出流。具备写入权限的连接显示上传按钮，使用系统文档选择器选择源文件。源文件先暂存到应用缓存（上限 1 GiB），再以服务器规定的块大小上传。临时文件会在结束或取消时删除。服务器接受发布请求后显示“已接收”，不将异步网盘发布误报为已经成功；请稍后刷新目录确认。

每个浏览器实例绑定登录身份与完整连接 ID；文档选择器回调和传输使用打开时的连接和目录，不读取后来选择的账号。关闭浏览器取消其协程；身份变化后忽略旧回调和旧响应。未进入提交阶段的上传尝试清理服务器暂存会话，网络失败时仍由服务端到期回收。已提交的远端发布不能通过关闭窗口撤回。

当前传输属于前台浏览窗口，不支持进程重启续传、后台任务或跨账号复制。下载失败或关闭时，系统文档提供者中可能留下不完整目标文件，应删除后重新下载。发布结果不确定时先刷新核实，避免重复上传。真实百度授权及设备上的文档提供者行为需要真机验收。
