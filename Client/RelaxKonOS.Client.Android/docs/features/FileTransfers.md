# Android 文件上传与下载（后台传输与续传）

> 日期：2026-10-03
> 归属：本文拥有 Android 侧的客户端设计。线协议、服务端行为、桌面客户端与提权模型见仓库级 [`docs/architecture/RelaxKonOS.FileUpload.Design.md`](../../../../docs/architecture/RelaxKonOS.FileUpload.Design.md)；iOS/桌面不得与本文的偏移规则分歧。
> 前置阅读：[`Product.Design.md`](../design/Product.Design.md)（安全边界与文件应用定位）、[`Shell.Design.md`](../design/Shell.Design.md)（能力门控与页面清单）

---

## 1. 范围

普通服务器文件上传使用分块会话、应用级上传协调器和续传日志；下载直接落盘，预览使用受预算约束的缓存。应用部署归档暂存使用独立接口，不能因为普通文件支持续传就宣称部署归档也支持续传。共享偏移、授权与校验语义由 [文件上传协议](../../../../docs/architecture/RelaxKonOS.FileUpload.Design.md) 定义。

下载保持 HTTP 流式落盘，服务器内部以有界分块跨越用户执行或提权边界，不再把整个文件装进单次 Helper 响应。单次内容限制不等于下载文件总大小限制；服务器返回 `413 / content-too-large` 时，客户端使用本地化的大小限制提示。

Server 文件上传与下载使用可收起的页面进度卡，显示文件名、实际传输字节、已知总量和百分比，并提供取消；进度卡不阻挡导航。长度未知时显示已传输字节与不确定进度动画；准备源文件不计入上传百分比。续传上传保留既有确认偏移、在途估计和核对提示，停止后仍提供继续/放弃等操作。

小文件单次上传与所有下载由应用级 `FileTransferCoordinator` 持有，使用独立 `FileTransferForegroundService`（`dataSync`）提供后台进度与取消；大文件与未知长度上传仍交给 `UploadCoordinator/UploadForegroundService`。离开文件页、旋转、重建 Activity 或切到后台后继续当前进程内任务，返回显示同一状态；结果通过应用级提示报告。通知仅展示通用传输文案及百分比，不包含文件路径、宿主或凭据。通知权限拒绝不阻止执行，页面仍提供取消。需管理员授权时等待用户回前台处理。

退出登录或切换会话会取消原单次传输，迟到进度/清理及旧通知取消不会影响新任务。单次上传中断后不自动重发，下载失败或取消清理未提交目标；这两类不提供跨进程续传。服务结束或移除应用任务会停止单次传输。分块上传遇到服务启动失败、系统服务超时或移除应用任务时中断网络，但保留已建立会话的续传日志与缓存；用户明确取消仍放弃会话并配对清理。服务处理 [Android dataSync 超时](https://developer.android.com/develop/background-work/services/fgs/timeout)，不保证系统回收后继续执行。

## 2. 源的可寻址性策略

续传的前提是"能从任意偏移重新读到源数据"。SAF 给的是流，不是文件，因此按源的形态分三种处理：

| 源形态 | 判定 | 处理 |
| --- | --- | --- |
| 可寻址（本地存储上的普通文档） | `openFileDescriptor(uri,"r")` 成功，且 `FileInputStream(fd).channel` 能 `position(offset)` | 直接分块读：每片按 `position(offset)` 定位后读 `chunkSize` 字节。**不落盘**，零额外磁盘占用 |
| 不可寻址（云盘/内容提供者）或长度未知 | 上面的探测抛异常，或 `OpenableColumns.SIZE` 为 null/负数 | **先落应用私有缓存**（`cacheDir/uploads/<sha1(uri+dirty)>`），再从缓存文件分块上传；缓存文件在成功/取消/失败后必须删除 |
| 用户重新选择了同一个 URI 但文件已变 | 缓存的 `length + lastModified` 与本次不一致 | 丢弃缓存与续传条目，重新开始（拼半个旧文件是比失败更坏的结果） |

判定实现放在纯 Kotlin 的一层（`data/UploadSource.kt`），不依赖 Android framework 的静态调用，便于 JVM 测试：

```kotlin
/** 一次上传的源：能按偏移重读，或者明确表示做不到（此时必须先落盘）。 */
sealed interface UploadSource {
    val length: Long            // 声明的总长度；无法确定时返回 null
    val displayName: String
    /** 打开 [offset, offset+max) 的读句柄。不可寻址的源抛 SeekUnsupported。 */
    fun openAt(offset: Long): InputStream
}

class SeekUnsupported : Exception("source is not seekable")
```

落盘阶段在转移卡片上必须显示**独立的阶段**（"准备中"），不能计入上传百分比——否则用户会以为在传，其实在读云盘。落盘前检查 `cacheDir` 可用空间 ≥ 长度 × 1.1，不满足时给出明确错误（`files_upload_cache_full`），不静默降级为不可续传的一次性上传。

缓存键包含 `uri` 与 `lastModified`/`size` 的摘要：云盘文件在本地改了内容就必须换键。
恢复时先校验同一源签名的缓存副本及其长度；完整副本可直接用于续传，即使原 URI 的临时读取授权已失效。

---

## 3. 执行与前台通知

分片循环与状态由 `AppContainer.uploads` 的应用级 `UploadCoordinator` 持有，不依赖 Files 页面或 Service 生命周期。`UploadForegroundService` 以 `dataSync` 类型镜像状态、显示可取消通知，并把取消交给协调器；服务自身的 scope 只观察状态，不执行传输循环。

只有活动上传时存在服务与通知，结束立即停止；服务使用 `START_NOT_STICKY`，不自动后台重启，也不申请 `WAKE_LOCK`。前台运行不保证厂商省电策略不会中断网络，失败按重试与权威偏移核实处理。当前每次只执行一个上传，不承诺多文件并发或无人值守自动恢复。

应用被回收后，用户重新进入从续传日志读取可继续任务，并显式选择继续/放弃。设备系统机制与大文件检查集中在 [验证要求](../development/Verification.md)。

---

## 4. 分片循环

一次上传 = 一个循环，每个迭代一个 HTTP 请求：

```text
ensureSession()
  ├─ 续传日志命中（serverKey + uri + length + lastModified 全部一致）→ GET /files/uploads/{id}
  │     ├─ 200 → offset = 权威偏移（可能小于本地记忆：信任服务端）
  │     └─ 404/410 → 丢掉条目，新建会话
  └─ POST /files/uploads（Idempotency-Key 由服务器键、目标目录和源签名稳定生成；创建响应丢失后重试仍用同一个键）
        └─ 403 elevation-required → container.elevationAnswers 弹窗 → 重试创建（只重试创建）

loop:
  if offset == length → commit
  openAt(offset) → 读 chunk = min(chunkSize, length - offset) → PATCH（Upload-Offset: offset）
    ├─ 204 → offset = 响应 Upload-Offset；失败计数归零；chunkSize 向服务端下发的上限升回
    ├─ 会话丢失（not-found / expired）→ 丢弃条目，新建会话（只重建一次）
    ├─ 401 → 刷新 token（session.authenticated 的既有机制），GET 权威偏移，继续
    ├─ offset-mismatch / concurrent-chunk / chunk-too-large / length-required
    │     → offset = 响应携带的权威偏移，继续（chunk-too-large 额外把 chunkSize 减半，下限被上限夹住）
    │     → **偏移没有前进就计入同一份预算**：这些答复都是"不是失败"，不计入就会永久空转
    ├─ 5xx / IOException（含网络切换）/ 读超时 → 退避（1s→2s→…→30s，带抖动）
    │      → GET 权威偏移；若连续失败 ≥ 10 次 → 终态失败，保留会话与日志
    └─ 507/429/413/文件名 4xx → 终态失败，保留会话，界面给出可操作原因

commit:
  POST /files/uploads/{id}/commit（本地先算 sha256，可选）
    ├─ 409 upload-incomplete → 回到 loop（响应已带权威偏移）
    ├─ 401 → 刷新后重试 commit（不重传数据）
    └─ 201 → 记入 RecentOperationJournal、刷新目录、删除续传条目与缓存文件
```

关键约束（与协议文档同源，这里只写 Android 的实现口径）：

- **每片一个新连接**。不复用长连接、不依赖连接存活：`HttpURLConnection` 的连接池在切换网络后本就不该被信任。
- **分片请求使用独立的超时**，不沿用普通 API 的 20 秒读超时：连接 15 秒不变；读超时按 `chunkSize / 最慢预期带宽` 取值（默认 8 MiB 分片、5 分钟读超时）。连续两次读超时 → 分片自适应降级（8 → 4 → 2 → 1 MiB），成功后不急于升回（避免抖动）。
- **分片永不超过服务端下发的 `chunkSize`**（服务端就是按它设置该路由的请求体上限），自适应缩小的**下限取 `min(1 MiB 偏好, 该上限)`**：把下限钉死在 1 MiB，会让"服务端只允许 32 KiB"的会话陷入"每次都被拒、每次都不缩小"，直到预算耗尽。上限只有服务端一个权威来源。
- **重试预算是共享的**（`MAXIMUM_CHUNK_FAILURES = 10`）：传输失败与"服务端反复给出不前进的答复"用同一份额度。耗尽后是**保留会话与日志条目的失败**——用户点"继续"就是从权威偏移接着传，而不是从头开始。
- **`setFixedLengthStreamingMode(chunkLength)`**（`long` 重载，API 19+）：分片远小于 2 GB，不会踩到 `int` 重载的限制。
- **偏移算术只有一个入口**：`RelaxKonGateway`/`RelaxKonApi` 之外不得再出现 `Upload-Offset` 的拼装或计算。改网关接口必须同步 `app/src/test/.../FakeGateway.kt`（否则 JVM 测试直接编译不过，这是仓库既有约定）。
- **不假装成功**：只有收到 204 才推进偏移；只有收到 201 才认为文件到了远端。

---

## 5. 续传日志

`data/UploadResumeJournal.kt`，存应用私有目录（`noBackupFilesDir` 与凭据分离，**不加密**：它只有路径与偏移，没有任何凭据；这一点要在注释里写明，避免后来者误以为需要凭据保护）。

一个条目：`serverKey`（稳定 `serviceId + 用户 id + 工作区 id + 设备 id`；隧道换端口不改变恢复归属）、`uploadId`、目标目录、文件名、源 uri、源 length/lastModified、已确认偏移、记录时间。桌面端接入动态隧道前也须把 `ExplorerOperationCenter.SessionKey` 调整到相同语义。

规则：

- 只在会话边界写：创建成功后、每片确认后（限频：最多每 2 秒一次）、提交失败后、终态失败时。
- 读取时校验 `serverKey` 与源签名，不一致即丢弃；超过 7 天丢弃（与服务端绝对 TTL 对齐）。
- 最多 16 条（移动端场景少于桌面端），按最旧淘汰。
- 文件损坏 → 视为空。

---

## 6. 状态归属与界面

- 状态由 `container.uploads` 应用级协调器持有，`MobileNavHost.FilesDestination` 的上传卡片订阅它；离开 Files 页面不取消上传。
- 转移卡片包含两种状态：**准备中**（源落盘，独立阶段）与 **可继续**（有续传条目、未在传输、带"继续/放弃"）。百分比与字节数只由"已提交偏移 + 在途字节"合成，不允许回退显示。
- **核对态必须是独立字段**，不是"字节数为 0 的报告"：`UploadState.resynchronising`（桌面端对应 `LargeFileUploadProgress.Reconciling`）。核对期间界面与前台通知同口径地显示文案（`files_upload_reconciling` / `files_upload_progress`），不显示任何数字——一个 `0` 会被监视进度的代码读成"从头开始"。
- 失败文案必须可操作：区分"网络中断（已保留进度，可继续）"、"存储空间不足（服务器）"、"本地缓存空间不足"、"源文件已变化（需要重新开始）"、"源文件不可读（需重新选择）"、"目标目录需要管理员授权"、"文件名服务器不接受（需要重命名）"。最后一个不是可有可无的分类：手机上合法而宿主上非法的名字（冒号、结尾点、保留设备名）只能靠改名解决，落进通用"服务器拒绝"就等于没有解释。
- 三条 `strings.xml`（`values` / `values-zh` / `values-ja`）键集必须一致；新增键按仓库既有校验方式核对。
- 视觉只用既有 tone（`StatusTone.Success` / `Danger` / `Warning`），不写十六进制颜色；图标从桌面镜像集里取（上传/取消已有映射），不自绘。

---

## 7. 取消、失败与清理

| 动作 | 行为 |
| --- | --- |
| 用户取消 | 停止循环 → `DELETE /files/uploads/{id}` → 删除缓存文件 → 删除续传条目 → 通知消失 → 目标目录无残留（目标文件只由提交产生） |
| 取消时 `DELETE` 失败 | 不阻塞用户；条目保留在服务端并在 TTL 后被清理；本地条目标记为已放弃，不再自动续传 |
| 可继续失败（网络、服务端/本地空间、暂时拒绝或授权未取得） | 已有会话及续传条目保留，用户处理后继续；以 `UploadFailure.keepsResumeEntry` 判定，不仅按 HTTP 码判断 |
| 文件名被拒绝（`400 invalid-file-name`） | 不保留条目、无会话可删（服务端在建会话前就拒绝了）；提示重命名后重试 |
| 无法继续的失败（源变化 / 源不可读 / 会话丢失 / 文件名被拒） | 条目与缓存副本**成对删除**，文案说明要重新开始而不是"继续" |
| 应用被系统回收 | 下次进入显示"可继续"，按 §5 恢复 |
| 传输期间用户重新选择同一文件 | 视为新任务；旧条目由 TTL 自然淘汰，不并发两个传输 |
| 源在传输中被移除（云盘取消下载/SD 卡拔出） | 分片读失败 → 视为可重试；连续失败进入终态并提示"源文件不可读" |

缓存文件的删除要与续传条目同生命周期：**条目删除即缓存删除**，反之亦然（避免 20 GB 缓存无人认领）。这是一个必须在代码里成对出现的不变量，测试要覆盖。

---

## 8. 资源与配额

- 上传分片内存：80 KiB 缓冲，与文件大小无关。
- 缓存落盘：仅不可寻址源；单个文件上限沿用服务端单文件上限，且必须通过可用空间检查。
- 蜂窝网络：不自动在移动数据上传大文件（用户显式发起后即可用；是否增加"仅 Wi-Fi"偏好由产品决定，不影响协议）。
- 通知与前台服务：只在有活动传输时存在；传输结束后立即停止服务，不留常驻通知。

---

## 9. 实现职责

| 文件 / 目录 | 职责 |
| --- | --- |
| `core/net/RelaxKonApi.kt`、`RelaxKonGateway.kt`、`Wire.kt` | 当前上传路由、流式请求、偏移和问题码；单发与分块共用当前契约 |
| `data/UploadSource.kt`、`AndroidUploadDocument.kt` | SAF 元信息、源可寻址判定、缓存暂存/清理 |
| `data/UploadResumeJournal.kt` | 有界制表符转义日志、源绑定、过期与配对移除 |
| `data/UploadCoordinator.kt` | 分片循环、重试预算、权威偏移核实、提交/取消及配对清理 |
| `data/FileTransferCoordinator.kt`、`service/FileTransferForegroundService.kt` | 应用级单次上传/下载、会话与任务代次隔离、独立后台通知与取消 |
| `service/UploadForegroundService.kt` | 状态镜像、前台进度通知与取消入口 |
| `ui/files/FilesScreen.kt`、`ui/nav/MobileNavHost.kt` | 单发/分块分派、上传卡片与跨页面回执 |
| `AppContainer.kt`、Manifest、三语资源 | 应用级依赖、dataSync 服务/权限、统一文案 |

---

## 10. 验证归属

JVM 检查覆盖续传日志、源暂存、权威偏移、重试预算、取消及缓存清理。执行证据见 [功能目录](../README.md)；大文件、SAF、进程回收、弱网、通知和桌面一致性检查统一见 [验证要求](../development/Verification.md)。
