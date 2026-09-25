# Android 大文件上传（分块与续传）设计

> 状态：已实现（JVM 层 41 项上传相关测试全绿，与全仓 273 项一起通过；§10.2 的真机矩阵待执行）
> 日期：2026-09-24
> 归属：本文拥有 Android 侧的客户端设计。线协议、服务端行为、桌面客户端与提权模型见仓库级 [`docs/architecture/RelaxKonOS.FileUpload.Design.md`](../../../docs/architecture/RelaxKonOS.FileUpload.Design.md)；iOS/桌面不得与本文的偏移规则分歧。
> 前置阅读：[`RelaxKonOS.Mobile.Design.md`](./RelaxKonOS.Mobile.Design.md)（安全边界与文件应用定位）、[`RelaxKonOS.Mobile.V1.Design.md`](./RelaxKonOS.Mobile.V1.Design.md)（能力门控与页面清单）

---

## 1. 范围与改造前的阻塞点

> 本节记录改造前的状态（保留，作为改动理由与回归基线）；改造后的落地情况见 §9 与 §10。

当前 Android 上传是**一次连接传完整个文件**：

- `RelaxKonApi.upload` 手写 multipart，用 `HttpURLConnection` 流式写出（`core/net/RelaxKonApi.kt:210-258`）；本身不整包缓冲，这一点比桌面端好。
- 但连接参数是普通 API 的参数：`connectTimeout = 15s`、`readTimeout = 20s`（同文件 `:545-546`），大文件在移动网络上跨过这个量级。
- 执行者是**页面的** `viewModelScope`（`ui/files/FilesScreen.kt:494-537`）：页面销毁、进程被杀、Doze 之外的后台限制都会终止它，而且没有任何地方记录"传到哪了"。
- 源是 SAF 的一次性 `InputStream`（`FilesScreen.kt:841-863` 的 `openPickedDocument`），不可寻址，所以重试只能从头。
- Manifest 里没有前台服务（`INTERNET` / `USE_BIOMETRIC` / `USE_FINGERPRINT`），没有长时间传输的资格。

结论：Android 侧要改的不是"上限"，而是**执行位置、源的可寻址性、偏移记忆**三件事。协议与服务端不因 Android 而变。

---

## 2. 源的可寻址性策略（最关键的一条）

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

## 3. 传输执行位置：前台服务

长时间传输不能挂在页面上，也不能只挂在 `Application` 作用域（进程被回收后无从恢复）。方案：**前台服务**。

- 新增 `UploadForegroundService`（`dataSync` 类型），声明 `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_DATA_SYNC` 权限与通知渠道。
- 服务持有当前传输的 `CoroutineScope`（`SupervisorJob + Dispatchers.IO`），负责：跑分片循环、更新通知进度、接收"取消"意图。
- 通知：标题 = 文件名，进度 = 已提交字节 / 声明长度（与界面同一口径），动作 = 取消。**Android 14+ 要求在通知里提供用户可见的取消途径**，这也正好是我们本来就需要的语义。
- 界面与服务的通信：服务暴露一个进程内的状态流（`StateFlow<TransferState>`），由应用级容器（`AppContainer`）持有；页面只订阅，不拥有。
- 不申请 `WAKE_LOCK`：前台服务已使应用免于后台执行限制与 APP_STANDBY 冻结，不引入无关权限。厂商激进的省电策略仍可能暂停网络——那时按 §4 的分片重试 + 权威偏移重同步处理，损失上限是一个分片。
- 前台服务只能有一个"当前传输"。选择多个文件时，队列由应用级调度器串行推进（**不做多文件并发上传**：移动网络下并发只会让每个文件都变慢，且更容易被系统或网络切换打断）。

**进程被杀后的恢复**（与"服务被系统回收"区分）：

- P4 的第一版：不做自动后台重启。用户回到应用，转移卡片显示"上次未完成（已完成 62%）"，提供"继续"与"放弃"两个动作，靠 §5 的续传日志恢复。
- P4 的后续（若需要无人值守续传）：`WorkManager`（`setExpedited` 不可用于长时间任务，需用普通 `OneTimeWorkRequest` + 前台服务 run-as-foreground）+ 对 pick 进来的 URI 调 `takePersistableUriPermission`。这是一个独立决定，不阻塞第一版；本文件在实现该版本时必须补写 URI 持久化权限与失败重试的边界。

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

一个条目：`serverKey`（`serverUrl + 用户 id + 工作区 id + 设备 id`，与桌面端 `ExplorerOperationCenter.SessionKey` 同口径）、`uploadId`、目标目录、文件名、源 uri、源 length/lastModified、已确认偏移、记录时间。

规则：

- 只在会话边界写：创建成功后、每片确认后（限频：最多每 2 秒一次）、提交失败后、终态失败时。
- 读取时校验 `serverKey` 与源签名，不一致即丢弃；超过 7 天丢弃（与服务端绝对 TTL 对齐）。
- 最多 16 条（移动端场景少于桌面端），按最旧淘汰。
- 文件损坏 → 视为空。

---

## 6. 状态归属与界面

- **转移状态必须活在应用级作用域**，不能留在 `FilesScreen`：`MobileNavHost.kt:84` 已确立"下载/上传的生命周期长于发起它的页面"这一约定，`FileMessageBanner` / `FileTransferCard` 属于两边都要看得见的东西。改造前 `FilesViewModel.upload()` 把 `transfer`/`transferJob` 放在页面 VM 里，正是被搬走的部分（现已改为 `container.uploads` 应用级坐标器 + `MobileNavHost.FilesDestination` 的回执卡片）。
- 转移卡片新增两种状态：**准备中**（源落盘，独立阶段）与 **可继续**（有续传条目、未在传输、带"继续/放弃"）。百分比与字节数只由"已提交偏移 + 在途字节"合成，不允许回退显示。
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
| 终态失败（507/429/413/源变化） | 保留服务端会话（不删，用户处理后可以继续）+ 保留续传条目 + 删除缓存文件（源变化时） |
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

## 9. 需要改动的文件

| 文件 | 改动 |
| --- | --- |
| `AndroidManifest.xml` | 新增 `FOREGROUND_SERVICE`、`FOREGROUND_SERVICE_DATA_SYNC`、`POST_NOTIFICATIONS`；注册 `UploadForegroundService`（`exported="false"`、`foregroundServiceType="dataSync"`） |
| `core/net/RelaxKonApi.kt` | 新增 `createUpload` / `sendUploadChunk` / `getUpload` / `commitUpload` / `abortUpload`；`upload` 保留为 ≤ 4 MiB 单发快路径；新增 `FileRoutes.UPLOADS*` 常量；分片请求使用独立超时，并复用 `streamInto` |
| `core/net/RelaxKonGateway.kt` + `Models.kt` | 网关接口与 DTO 镜像（`UploadSession` 等）；**同步 `FakeGateway`**，否则 JVM 测试直接编译不过 |
| `core/net/Wire.kt` | 分片响应的 `Upload-Offset` 与问题码解析；可空字段一律经 `optNullable*`（`org.json` 会把 JSON `null` 读成字符串 `"null"`） |
| `data/UploadSource.kt`（新） | `UploadSourceStager`：源形态判定（可寻址 / 不可寻址 / 长度未知）、`openAt(offset)`、缓存落盘与 `discardCache`/`trim`；不可寻址源先落 `cacheDir/uploads/<sha1(sourceKey)>`，属独立"准备中"阶段 |
| `data/UploadResumeJournal.kt`（新） | 续传日志（§5）；制表符转义行格式（**不是 JSON**——JVM 单测跑在被 stub 的 `org.json` 上），写入按"读改写整表"实现，`remove` 返回被删项供交接 |
| `data/UploadCoordinator.kt`（新） | `UploadCoordinator`：分片循环、退避与自适应分片、权威偏移重同步、核对态、重试预算、会话与条目的配对删除；`UploadFailure` 用 `keepsResumeEntry` 区分"可继续"与"必须丢弃" |
| `data/AndroidUploadDocument.kt`（新） | SAF 文档的显示名/大小/修改时间与文档来源（`AndroidUploadDocuments` 提供者），供界面与日志共用 |
| `service/UploadForegroundService.kt`（新） | 前台服务 + 通知 + 通知内取消；**只镜像状态、不持有状态**，转移的生命周期归应用级坐标器 |
| `data/FilesRepository.kt` | **未改动**：阈值分派发生在界面层（`FilesViewModel.upload(uri)`）：长度已知且 ≤ 4 MiB 走 `uploadSingleShot`，**长度未知也走可续传路径**（未知长度必须落盘，见 §2）；单发收到 `TOO_LARGE_FOR_SINGLE_SHOT` 时同样转可续传 |
| `ui/files/FilesScreen.kt` | 转移卡片状态上移到 `MobileNavHost.FilesDestination` 可见的作用域；新增"准备中/可继续"与上传进度卡片；移除页面级传输作业 |
| `ui/nav/MobileNavHost.kt` | 应用级转移状态持有者 + 跨路由的 `FileUploadCard`（与既有跨路由回执同处，见仓库备忘录的"跨路由回执"约定） |
| `ui/common/ProgressSheet.kt` | 支持可选的脚注与操作行，供"准备中/核对中"的说明与取消按钮使用 |
| `AppContainer.kt` | 组装 `UploadCoordinator`（应用级作用域 + 注入的 dispatcher）、`UploadResumeJournal`、`UploadSourceStager`；`uploads` 声明在 `elevationAnswers` 之后 |
| `res/values{,-zh,-ja}/strings.xml` | 新增 `files_upload_needs_folder`、`files_upload_reconciling`、`files_upload_progress` 等（三份键集必须完全一致） |
| `app/src/test/.../FakeGateway.kt` | 补上五个新网关方法与处理器、`abortedUploadIds`/`committedUploadIds`/`uploadSessionReads` 记录，供偏移与配对断言 |

---

## 10. 验收

### 10.1 JVM 单元测试（无 Android framework 依赖）

三类共 **43 项**，与全仓 275 项一起通过（`gradle :app:testDebugUnitTest`）。它们跑在**被 stub 的 `org.json`** 上（每次调用都抛），所以续传日志用制表符转义行格式而不是 JSON——这不是风格选择，是测试可行性。

`data/UploadResumeJournalTest`（13 项）——日志的读写与淘汰：

- 含分隔符的路径与文件名能往返；首次写入即建文件；同一会话重录是**替换**而不是追加；条目按新到旧返回（未完成列表就按这个顺序显示）；只列当前服务器的条目。
- 越龄淘汰（与服务端保留会话的时长对齐）、条数上限时淘汰最旧。
- `remove` 把被删项交回调用方（缓存副本要跟着同一项一起走）；`keepServer` 交接；按文档查找会清掉同一文档的旧会话。
- 非本格式文件视为空；损坏行跳过但其余仍能加载。

`data/UploadSourceStagerTest`（14 项）——源形态与缓存：

- 可寻址源就地使用、**不落盘**、也不上报"准备中"；不可寻址或长度未知的源先落应用私有缓存，且"准备中"是**独立阶段**、绝不作为进度的一部分。
- 缓存键 = 源签名（改动过的文档拿到自己的副本，不会复用旧缓存）；可用空间不足在复制开始前就失败；被拒的复制与中途失败都不留半成品或截断文件。
- `discard` 只删对应副本；`trim` 只留仍被条目拥有的副本并报告删除数；目录尚不存在不算错误。
- 分派阈值低于单发路由自身上限（阈值是调度选择，不是契约）；不可寻址能被提前识别，不需要先读到数据。

`data/UploadCoordinatorTest`（14 项）——偏移、预算与配对：

- 采用**服务端报告的**偏移而不是本地记忆；服务端问不到时不另开第二个会话；服务端已忘记的会话只重建一次。
- 未被确认的分片不推进偏移，且重同步有预算；服务端给出**更低**偏移时进度不回退（界面切到"正在与服务器核对进度"）。
- **不前进的答复不能死循环**（与传输失败共用同一份预算）；分片永远不大于服务端下发的大小。
- 源在传输期间变过就不再提供续传；预算耗尽保留会话、条目与继续入口。
- 服务端拒绝**文件名**（`400 invalid-file-name`）自成一类失败（`UploadFailure.NameUnusable`）并给出"重命名后重试"的可操作文案，而不是并入通用"服务器拒绝"；它与源变化一样**不保留**续传条目。手机上的合法名字在宿主上可能非法（冒号、结尾点、保留设备名），而用户唯一能做的是改名——"服务器拒绝了"等于什么都没说。
- 完成与取消都让条目与缓存副本**一起**消失；他服务器的条目不展示；提权只在开会话时问一次，中途绝不再问。

**边界**：界面层的分派（`FilesViewModel.upload(uri)`：长度已知且 ≤ 4 MiB 走单发，否则走可续传）依赖 `Uri` 与 `viewModelScope`，因此不在纯 JVM 测试内，由 §10.2 的真机矩阵覆盖；本仓库不为单个分派引入 Robolectric。

### 10.2 真机矩阵（无法用编译代替）

| 场景 | 通过标准 |
| --- | --- |
| 3 GB 视频（本地存储，可寻址源） | 后台运行、锁屏后完成，通知进度与界面一致 |
| 3 GB 文件（云盘源，不可寻址） | 先"准备中"落盘，再上传；成功后缓存被删除 |
| 传到 40% 切 Wi-Fi → LTE | 自动续传，偏移不归零 |
| 传到 40% 强杀应用，重进 | 显示"可继续 40%"，继续后从权威偏移前进 |
| 传到 40% 点取消 | 通知消失、目标目录无残留、缓存被删除、服务端会话在 TTL 后消失 |
| 受保护目录 + 1 GB 文件 | 弹一次管理员授权，随后完成（验证 12 MiB 天花板消失） |
| 1 GB 文件传两遍（第二遍同名） | 覆盖语义与桌面端/单发一致 |
| 分片连续超时（弱网模拟） | 分片降级后仍能完成，界面不出现"假进度" |

### 10.3 与桌面端的一致性检查

- 同一文件分别用桌面端与 Android 上传，服务端最终 `FileEntryDto.Size` 与原文件哈希一致。
- 端到端的偏移规则只有一处实现（协议文档 §3.4），两端都不允许"用本地记忆推断服务端状态"。
