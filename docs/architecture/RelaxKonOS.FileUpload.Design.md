# RelaxKonOS 大文件上传设计与实现规格

> 状态：已实现（P1 服务端 / P2 特权路径 / P3 桌面 / P4 Android / P5 文档均落地；自动化验证见 §9.1–§9.3，§9.4 的真实宿主验收需在目标环境执行）
> 日期：2026-09-24
> 适用范围：`.NET 10` Server、Avalonia 桌面客户端、Android 客户端、PrivilegedHelper、共享 Protocol
> 相关文档：[通信协议](./RelaxKonOS.Protocol.md)、[文件管理器](../applications/RelaxKonOS.Explorer.md)、[文件操作中心](../applications/RelaxKonOS.Explorer.Operations.md)、[特权操作](../platform/RelaxKonOS.PrivilegedOperations.Operations.md)、[Android 大文件上传（客户端细节）](../../Client/RelaxKonOS.Client.Android/docs/RelaxKonOS.Mobile.BulkUpload.Design.md)

---

## 0. 结论

> §0–§2 保留**改造前**的证据与目标，作为改动理由与回归基线（其中的"当前""今天"都指改造前）；落地状态、验证结论与分阶段状态见 §10，逐点实现位置见各章开头的落点说明。

当前上传是**一次性 `multipart/form-data` 单请求**，两端都如此。它不是一个"上限设小了"的问题：即使把每一层的长度上限都调到无限，上传依然会失败或行为错误，因为存在**六个互相独立、任何一个单独出现都足以摧毁大文件上传**的阻塞点：客户端整包缓冲进内存、客户端整请求超时、服务端整包缓冲、特权通道单请求 12 MiB 上限、没有续传、没有暂存磁盘守卫。这些点的证据见 §1。

因此本设计**替换数据面协议**，而不是放开参数：

- 新增基于会话的分块上传协议（`POST /files/uploads` → `PATCH .../{id}` → `POST .../{id}/commit`，可 `GET` 查询、可 `DELETE` 放弃）；
- 分片是**原始字节**请求，不经 multipart、不经表单缓冲，服务端边收边按偏移写入最终目标目录内的暂存文件；
- 提交是**同卷原子改名**，复用现有 `WriteAtomicallyAsync` 的终止语义，目标文件只有出现与不出现两种状态；
- 中断（网络抖动、切前后台、进程被杀、服务端重启、代理超时）后，从一个 `GET` 得到的权威偏移继续，最多重传一个分片；
- 内存占用与文件大小解耦：客户端 O(分片)，服务端 O(分片)。

小文件不再走这条路径：声明长度 ≤ 4 MiB 的条目继续用单发路由（但该路由获得**显式的**长度上限），避免"上传一千个小文件"被协议往返放大。两条路由共用同一个服务端写入器，不是双解析。

---

## 1. 为什么"调大允许的上传大小"不能解决问题

### 1.1 桌面端：整包进内存（第一阻塞点，且不可通过参数修复）

`AuthenticatedHttpHandler` 是所有受保护 API 的通信处理器。为了在 401 后用刷新过的 token 重放请求，它在**首次发送前把整个请求正文读成一个托管 `byte[]`**：

```csharp
// Client/RelaxKonOS.Client/Services/Auth/AuthenticatedHttpHandler.cs:24-27, 46-64
// Buffer once before the initial send. Cloning a StreamContent directly advances its
// underlying stream; without replacing the source content too, multipart uploads
// such as custom wallpapers arrive at the server as an empty file on their first attempt.
using var retry = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
...
var bytes = await originalContent.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
```

后果有三层，全部与"上限"无关：

1. **内存**：`ReadAsByteArrayAsync` 内部用 `MemoryStream` 增长式累积，峰值可达正文的 2–3 倍，落在 LOH 上。150 MB 文件即数百 MB 瞬时分配，数 GB 文件必然失败或触发 OOM。
2. **进度是假的**：`ProgressStreamContent.CopyContentToAsync`（`ExplorerClient.cs:169-180`）在**写入内存流**时报告字节数。也就是说进度条走满 100% 时，数据还在 RAM 里，尚未上线（见 §5.4 的修正）。
3. **重放语义错误**：为了一次 401 重放而缓冲整个文件，代价是内存；而分块协议下偏移重同步本来就是安全的重试方式（§3.4），所以这条缓冲在分块路径上必须去掉，而不是保留。

### 1.2 桌面端：整请求超时

Explorer 的 typed HttpClient 未覆盖 `HttpClient.Timeout`，沿用 100 秒默认值——它覆盖**从发出请求到读完响应**的全过程，包含正文上传：

```csharp
// Client/RelaxKonOS.Client/Services/Bootstrapper.cs:104-107
services.AddHttpClient<RelaxKonOS.Client.Apps.Explorer.IExplorerClient, RelaxKonOS.Client.Apps.Explorer.ExplorerClient>()
    .AddHttpMessageHandler(...)
    .AddHttpMessageHandler<AcceptLanguageHandler>()
    .AddRelaxKonOSAuthentication();
```

对照：应用部署的归档上传明确设成无超时（`Bootstrapper.cs:180`，`Timeout = Timeout.InfiniteTimeSpan`），因为它知道自己是长请求。Explorer 没有这个意识——100 秒内传不完的文件一律 `TaskCanceledException`，与文件大小上限无关。

### 1.3 服务端：整包缓冲 + 两层未声明的长度上限

```csharp
// RelaxKonOS.Server/Endpoints/FileEndpoints.cs:385-393
var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
var file = form.Files.FirstOrDefault();
...
await using var stream = file.OpenReadStream();
var dto = await fs.UploadAsync(path, file.FileName, stream, ctx.RequestAborted);
```

1. `ReadFormAsync` 在 `UploadAsync` 见到任何字节之前，已把整个正文缓冲完（超出内存阈值即落到框架临时文件），**同一份数据被完整写了两次**（框架临时文件 → 目标目录暂存文件）。
2. `FormOptions.MultipartBodyLengthLimit` 未配置，取默认 128 MiB，异常类型不属本端点已捕获的分支。
3. 该路由**从未声明请求体上限**：Kestrel 默认 30 MB 先生效（这一点在本仓库内也有旁证——`RelaxKonOS.Server.Tests/ApplicationDeploymentProgressVerification.cs:68` 明确称默认值为 30 MB）。应用部署路由则显式抬高（`ApplicationDeploymentEndpoints.cs:179-183`，先取 `IHttpMaxRequestBodySizeFeature` 再赋值），说明"声明式上限"在本仓库已是既定做法，只是文件路由漏了。

因此现场观察到的失败点会随文件大小与网络条件在 30 MB / 128 MiB / 100 s 之间跳变，表现各不相同——这本身就是设计缺陷的症状。

### 1.4 Android：一次性连接 + 屏幕级生命周期

```kotlin
// Client/RelaxKonOS.Client.Android/.../core/net/RelaxKonApi.kt:224-232
connection = openConnection(serverUrl, FileRoutes.UPLOAD + "?path=" + encode(targetDirectoryPath), "POST", accessToken)
connection.doOutput = true
...
if (total <= Int.MAX_VALUE) connection.setFixedLengthStreamingMode(total.toInt()) else connection.setFixedLengthStreamingMode(total)
```

- `readTimeout = 20_000` / `connectTimeout = 15_000`（同文件 `:545-546`）作用于单个连接；大文件在移动网络上必然跨过这个量级。
- 上传跑在**页面的** `viewModelScope` 里（`FilesScreen.kt:494-537`），`transferJob` 随页面销毁而取消；进程被杀、Doze、Wi-Fi 切 LTE 一律从头再来。
- 源是 SAF 的一次性 `InputStream`（`FilesScreen.kt:841-863` 的 `openPickedDocument`），**不可寻址**，所以"重试"在 Android 上等于"重传整个文件"。
- Manifest 里没有任何前台服务（只有 `INTERNET`/`USE_BIOMETRIC`/`USE_FINGERPRINT`），应用退到后台即失去长时间传输的资格。

### 1.5 特权路径：单请求 12 MiB 硬上限

受保护目录的上传走 Helper，而 Helper 的传输是"一条 JSON 消息承载全部内容"：

```csharp
// RelaxKonOS.Server/Privileged/PrivilegedFileService.cs:64-72
using var bytes = await ReadContentAsync(content, cancellationToken);
var result = await runner.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.FileUpload, Path: targetDirectoryPath,
    FileName: fileName, ContentBase64: Convert.ToBase64String(bytes.ToArray())), cancellationToken);
```

上限在协议里写死：`PrivilegedOperationProtocol.MaximumRequestBytes = 16 MiB`、`MaximumFileContentBytes = 12 MiB`（`Shared/RelaxKonOS.Protocol/Privileged/PrivilegedOperationContracts.cs:10-11`），Helper 侧同样校验（`RelaxKonOS.PrivilegedHelper/Program.cs:264-266`、`WindowsPrivilegedPipeServer.cs:136`）。所以**向受保护目录上传超过 12 MiB 的文件，今天在任何客户端都不可能成功**，这与 HTTP 层完全无关。分块设计必须包含这条路径，否则只是把失败点从 30 MB 挪到 12 MiB。

### 1.6 顺带发现的既有缺陷：`fileName` 未净化

```csharp
// RelaxKonOS.Server/Files/LocalFileService.cs:435-442
var dest = System.IO.Path.Combine(targetDirectoryPath, fileName);
```

`fileName` 直接来自 `IFormFile.FileName`（Content-Disposition）或 Android 的 SAF 显示名，`LocalFileService` 不做分离符/`..`/保留设备名检查。`Path.Combine(dir, "..\\..\\x")` 会逃出目标目录。本设计的新协议要求文件名解析为**单一文件名成分**并在服务端强制校验（§3.1），这个修复随新协议一起落地。

### 1.7 阻塞点汇总

| # | 阻塞点 | 位置 | 调大上限能否解决 | 症状 |
| --- | --- | --- | --- | --- |
| B1 | 客户端整包缓冲进内存 | `AuthenticatedHttpHandler.cs:60` | 否 | 大文件 OOM / 长时间 GC；进度条提前走满 |
| B2 | 客户端整请求 100 s 超时 | `Bootstrapper.cs:104` | 否 | 慢链路上任何大文件必失败 |
| B3 | 服务端 `ReadFormAsync` 整包缓冲 | `FileEndpoints.cs:385` | 否 | 双倍写盘、框架临时文件、延迟可见 |
| B4 | 服务端路由未声明体上限 | `FileEndpoints.cs:377` | 部分（30 MB→） | 413，无问题码 |
| B5 | 特权通道 16 MiB 请求 / 12 MiB 内容 | `PrivilegedOperationContracts.cs:10-11` | 否 | 受保护目录大文件永久失败 |
| B6 | 无续传、无暂存、无磁盘守卫 | 全链路 | 否 | 任何中断重传 100%；单个上传可写满宿主盘 |

---

## 2. 目标与非目标

### 2.1 目标

1. **无人工单文件上限**：受宿主暂存卷可用空间与配置上限（默认 1 TiB）约束，不再有 30 MB/128 MiB/100 s/12 MiB 这类隐性天花板。
2. **续传**：网络抖动、应用重启、服务端重启后，从权威偏移继续，最多重传一个分片；客户端不知道上一次分片是否送达时，用一次 `GET` 消除歧义，而不是靠猜测。
3. **内存与文件大小解耦**：两端峰值内存均为 O(分片)。
4. **进度真实**：数字来自服务端确认提交的字节数 + 当前分片在途字节，不来自本地读取。
5. **取消终态**：取消后目标目录里不存在半截目标文件；暂存文件在 TTL 内被清理。
6. **受保护目录同等可用**：通过新增两个有严格形状约束的 Helper 操作，把 12 MiB 天花板变成 6 MiB 分片下的无上限。
7. **两端一致**：桌面与 Android 共用同一份协议与同一套偏移规则。

### 2.2 非目标（明确排除，避免范围蔓延）

- **分片并行加速**：不支持同一会话内的多分片并发/乱序。跨文件的并发仍由客户端调度器限流。理由：乱序要求服务端维护空洞位图与更复杂的配额，而单文件并行上传在多数链路上收益远小于调度复杂度；如果将来需要，只影响 §3.3 而不动协议骨架。
- **秒传/哈希去重**：不引入"服务端已有此文件则跳过传输"。
- **跨设备续传**：会话绑定创建它的身份；不提供"手机开了头、电脑接着传"。
- **目录级事务**：不承诺"整批文件夹原子成功"。
- **把上传变成 `FileOperation`**：上传仍是**客户端驱动**的传输。服务端持有会话状态，但这不等于它进入 `FileOperationService` 的任务/决策/恢复模型；`RelaxKonOS.Explorer.Operations.md:33` 已明确不接受"伪装成服务端可恢复任务"，本设计遵守该结论。
- **不引入第三方传输协议栈**（例如 S3 分片、WebDAV）：见 `RelaxKonOS.FileServices.Specification.md` §30 的边界——那条边界针对 SMB/SFTP 数据面，与内置 Explorer 的 REST 上传不冲突，但同样不引入外部协议栈。

---

## 3. 协议

### 3.1 路由与契约

全部新增于 `Shared/RelaxKonOS.Protocol/Files/FileApiRoutes.cs`，Server 注册与两个客户端拼接共用同一常量（禁止硬编码字符串）：

```csharp
/// <summary>创建大文件上传会话（POST，需 JWT）。body: CreateUploadRequest，头: Idempotency-Key。返回 UploadSessionDto。</summary>
public const string Uploads = $"/{V1}/files/uploads";

/// <summary>会话子资源（GET 查询 / DELETE 放弃）。path: {uploadId}。</summary>
public const string UploadPattern = $"/{V1}/files/uploads/{{uploadId}}";

/// <summary>追加一个分片（PATCH .../{uploadId}，需 JWT）。头: Upload-Offset、Content-Length；body: application/octet-stream。</summary>
public const string UploadChunkPattern = $"/{V1}/files/uploads/{{uploadId}}";

/// <summary>提交会话为最终文件（POST .../{uploadId}/commit，需 JWT）。body: CommitUploadRequest。</summary>
public const string UploadCommitPattern = $"/{V1}/files/uploads/{{uploadId}}/commit";
```

DTO 按 Protocol 约定（`sealed record` + `[property: JsonPropertyName]`，线协议用 `System.Text.Json`）：

```csharp
public sealed record CreateUploadRequest(
    [property: JsonPropertyName("targetDirectoryPath")] string TargetDirectoryPath,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("lastModifiedUtc")] DateTimeOffset? LastModifiedUtc);

public sealed record UploadSessionDto(
    [property: JsonPropertyName("uploadId")] string UploadId,
    [property: JsonPropertyName("offset")] long Offset,          // 服务端已确认的字节数：续传的唯一真相
    [property: JsonPropertyName("length")] long Length,          // 会话声明的总长度
    [property: JsonPropertyName("chunkSize")] int ChunkSize,     // 服务端允许的单分片上限
    [property: JsonPropertyName("elevated")] bool Elevated,      // 该会话是否绑定提权授权（§4.5）
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

public sealed record CommitUploadRequest(
    [property: JsonPropertyName("contentHash")] string? ContentHash);   // 形如 "sha256-<hex>"，可空
```

**文件名净化（服务端强制，客户端也做同样的净化以给出可读错误）**：`fileName` 必须是单一文件名成分。
拒绝（规则取**最严格宿主 Windows** 的那一套，即使服务端跑在 Linux 上——名字最终指向的是宿主路径，被另一个客户端解析时也必须合法）：含 `/`、`\`、NUL 或任意控制字符；含 `:`、`*`、`?`、`"`、`<`、`>`、`|`；等于 `.` 或 `..`；空白或空串；以 `.`、空格或 `:` **结尾**（Windows 会静默吞掉结尾的点与空格，结尾冒号指向数据流）；Windows 保留设备名（`CON`、`PRN`、`AUX`、`NUL`、`COM1-9`、`LPT1-9`，**含带扩展名形式且不区分大小写**，如 `nul.txt`）；长度 > 255 个 UTF-16 单元。
拒绝时 `400` + `invalid-file-name`，**不静默改名**（改名会让用户找不到自己传的文件）。
`targetDirectoryPath` 必须绝对、已存在、目录，并继续经过 `EnsureUserModePath`（User Mode 下必须在 home 之下）。

服务端唯一入口是 `RelaxKonOS.Server/Files/FileUploadNamePolicy.cs`（同时负责暂存文件名的构建与解析——清理只碰"名字能解析出会话 id **且**该会话在索引里"的文件，绝不按"像临时文件"来删）。桌面客户端在 `Apps/Explorer/Uploads/LargeFileUploader.cs` 的 `FileUploadNamePolicyBridge` 里保留一份同规则的镜像：它存在的理由是"发出请求前就能给出可读错误"，**不是**第二份权威。Android 不重复实现，直接依赖服务端的 `400`。

### 3.2 创建会话

```
POST /api/v1.0/files/uploads
Idempotency-Key: <1–128 可打印 ASCII>
{ "targetDirectoryPath": "D:\\Work", "fileName": "big.iso", "length": 21474836480, "lastModifiedUtc": "2026-09-24T10:00:00Z" }
→ 201 { "uploadId": "9f2…", "offset": 0, "length": 21474836480, "chunkSize": 8388608, "elevated": false, "expiresAt": "…" }
```

创建时的动作与校验，顺序固定：

1. 校验 `fileName`（§3.1）与 `targetDirectoryPath`；`length` 必须 ≥ 0，且 ≤ 配置上限（默认 1 TiB）。
2. **磁盘守卫**：读取暂存卷可用空间，要求 `可用 ≥ length + 保留量(默认 1 GiB)`，否则 `507` + `insufficient-storage`。宁可拒绝启动，也不要在传输到 80% 时把宿主盘写满。
3. **会话数守卫**：同一身份最多 4 个未结束会话，全局最多 64 个，超出 `429` + `too-many-uploads`（带 `Retry-After`）。
4. **暂存文件探测与预留**：在目标目录内以 `.{净化后文件名}.{sessionId}.rkup` 创建 0 字节文件。
   - 成功 → `elevated: false`。
   - `UnauthorizedAccessException` 且 `elevations.IsElevated(user, FileElevationCapability.Upload, targetDirectoryPath)` 不成立 → `403` + `elevation-required`（**探测即写真实暂存文件，不额外发明"试写权限"接口**；0 字节探测是最便宜的诚实探针）。
   - `UnauthorizedAccessException` 且授权已存在 → 通过 Helper 创建同路径暂存文件（§4.5），成功后 `elevated: true`。
   - `DirectoryNotFoundException` → `404` + `not-found`。
5. 写入会话记录（§4.2），返回 `UploadSessionDto`。

**幂等**：相同 `Idempotency-Key` + 相同身份 + 内容一致 → 返回**同一个**会话（不新建）。这样创建请求在网络失败后重试不会泄漏会话或暂存文件。键相同但内容不一致 → `409` + `idempotency-conflict`（与 `ApplicationDeploymentEndpoints` 对同一头的处理保持一致）。

### 3.3 追加分片

```
PATCH /api/v1.0/files/uploads/9f2…
Upload-Offset: 16777216
Content-Length: 8388608
Content-Type: application/octet-stream

<8388608 原始字节>
→ 204 No Content
Upload-Offset: 25165824
```

语义，逐条固定：

- **正文是原始字节**，不是 multipart、不是 base64（提权会话除外，见 §4.5）。服务端不调用 `ReadFormAsync`，不读进 `byte[]`，按固定缓冲（80 KiB）边收边写到暂存文件的 `Upload-Offset` 处。
- **`Upload-Offset` 必须等于会话当前已确认长度**，否则 `409` + `upload-offset-mismatch`，响应同时携带权威 `Upload-Offset`（客户端据此重同步，不需要额外 `GET`）。
- **接受即确认**：只有在"字节全部写入并 `FlushAsync` 成功"之后才更新会话偏移并返回 204。因此 204 是客户端可以安全推进本地偏移的唯一依据。
- **长度校验**：`Upload-Offset + 本次长度 > 会话 Length` → `409` + `upload-length-exceeded`，丢弃本次数据，偏移不变。
- **分片上限**：单分片长度 > 会话 `chunkSize` → `413` + `upload-chunk-too-large`。`Content-Length` 缺失（chunked 传输）→ `411` + `length-required`：偏移算术要求长度是已知的，不接受无界正文。
- **单分片并发**：同一会话同时有两个分片在写 → `409` + `upload-concurrent-chunk`。服务端用会话级门闩（而非仅靠偏移比较）判定，避免两个分片交错到同一偏移。
- **会话过期**：`410` + `upload-session-expired`；不存在或不属于当前身份 → `404` + `upload-session-not-found`（**不存在与无权访问返回同一个码**，不泄漏其他身份会话的存在性）。
- **提权会话**：`elevated: true` 的会话，分片由服务端直送 Helper（§4.5），不再先尝试本地写；`chunkSize` 在该会话上被钳到 6 MiB。

### 3.4 查询会话（续传的唯一真相）

```
GET /api/v1.0/files/uploads/9f2…
→ 200 { "uploadId": "9f2…", "offset": 41943040, "length": …, "chunkSize": …, "elevated": true, "expiresAt": "…" }
```

客户端在任何不确定状态下（分片响应丢失、进程重启、上一条连接中断）都必须先 `GET` 再继续，**不允许**用"我发出去了"推断服务端已收到。这条规则换来的是：偏移永远单调、无需去重表、无需分片序号、无需幂等摘要。它是整个协议里最简单也最重要的一条。

### 3.5 提交

```
POST /api/v1.0/files/uploads/9f2…/commit
{ "contentHash": "sha256-<hex>" }   // 可空；给出时必须匹配，否则 409 + upload-hash-mismatch
→ 201 Location: /api/v1.0/files/info?path=…  body: FileEntryDto
```

- 偏移 ≠ 声明长度 → `409` + `upload-incomplete`，并携带权威 `Upload-Offset`。
- 校验通过后：关闭暂存文件 → `File.Move(staging, dest, overwrite: true)`（**与 `LocalFileService.WriteAtomicallyAsync` 的终止语义完全一致**：今天的上传就是静默覆盖，新协议不改变该语义，也不引入 `already-exists` 分支）。
- 提交是**唯一**创建/替换目标文件的步骤。因此"取消"和"失败"永远不会留下半截目标文件。
- 提交涉及的目录权限失败走与今天相同的两段式：先 `elevation-required`，授权后由 Helper 执行改名（§4.5）。
- 提交成功后删除会话记录，`206`? 否——`201 Created` + `Location`，`FileEntryDto` 带真实 `Size`、`Modified`、`MimeType`。

### 3.6 放弃会话

```
DELETE /api/v1.0/files/uploads/9f2…   → 204（即使会话已不存在也返回 204）
```

幂等：删除暂存文件（失败不报错，交给 TTL 清理）→ 移除会话记录 → 释放配额。客户端取消、切换目标、以及在创建后发现本地文件已变化时都会调用它。

### 3.7 问题码

沿用现有风格（`type: https://relaxkonos.app/problems/<suffix>` + 可选 `problemCode`），新增后缀：

| 后缀 | 状态 | 何时 |
| --- | --- | --- |
| `invalid-file-name` | 400 | 文件名不是单一成分 / 保留名 / 超长 |
| `insufficient-storage` | 507 | 暂存卷可用空间不足（含保留量） |
| `too-many-uploads` | 429 | 身份/全局会话数超限，带 `Retry-After` |
| `idempotency-conflict` | 409 | `Idempotency-Key` 复用但请求内容不同 |
| `upload-session-not-found` | 404 | 不存在或不属于当前身份 |
| `upload-session-expired` | 410 | 超过 TTL |
| `upload-offset-mismatch` | 409 | 偏移与服务端不一致（响应带权威 `Upload-Offset`） |
| `upload-concurrent-chunk` | 409 | 同会话并发分片 |
| `upload-length-exceeded` | 409 | 本次分片越过声明长度 |
| `upload-chunk-too-large` | 413 | 超过会话 `chunkSize` |
| `length-required` | 411 | 分片缺 `Content-Length` |
| `upload-incomplete` | 409 | 提交时偏移不足（响应带权威 `Upload-Offset`） |
| `upload-hash-mismatch` | 409 | 声明哈希与实收不符 |
| `elevation-required` / `access-denied` | 403 | 与现有文件操作同义，不自造新词 |

客户端映射要求：`upload-offset-mismatch` / `upload-incomplete` **不是失败**，是"重同步后继续"的正常答复（与 `thumbnail-unsupported` 在图片预览里的地位相同）；`chunk-too-large` / `concurrent-chunk` / `length-required` 同样归入"调整后继续"而不是终态。真正需要用户决策的终态是 `insufficient-storage`、`too-many-uploads`、`upload-session-expired`、`upload-hash-mismatch`，以及 **`invalid-file-name`**——最后一个尤其不能并入通用的"服务器拒绝"：手机上的合法文件名（含冒号、结尾点、保留设备名）在宿主上可能非法，用户唯一能做的是重命名，所以它必须自成一类并给出"重命名后重试"的文案。两端都已如此：桌面端在发出请求前就按 §3.1 的镜像规则拒绝并给出原因，Android 端把 `invalid-file-name` 映射成独立的失败类别（`UploadFailure.NameUnusable`）并附上同名说明。

### 3.8 分片大小

| 会话 | `chunkSize` | 理由 |
| --- | --- | --- |
| 普通（`elevated: false`） | 8 MiB | 在 1–3 Mbit/s 移动链路上单分片数十秒内完成，重传代价可接受；8 MiB 也是 80 KiB 缓冲的 100 倍，CPU 无感 |
| 提权（`elevated: true`） | 6 MiB | base64 后 ≈ 8 MiB，加 JSON 信封远低于 `MaximumRequestBytes = 16 MiB`；解码后 6 MiB 低于 `MaximumFileContentBytes = 12 MiB` |

客户端**必须**把分片长度限制在服务端下发的 `chunkSize` 以内——服务端正是按这个值设定该路由的请求体上限，发得更大只会收到 `413`。自适应缩小（连续超时后减半）的下限取 `min(客户端偏好 1 MiB, 服务端下发的 chunkSize)`：把下限钉死在 1 MiB 会让"服务端只允许 32 KiB"的会话陷入"每次都被拒、每次都不缩小"的循环，直到重试预算耗尽。`chunkSize` 由服务端下发而不是客户端写死，是为了让"上限"只有一个权威来源。

### 3.9 与单发上传的关系

保留 `POST /files/upload`（`FileApiRoutes.Upload`），但把它重新定义为**小文件快路径**：

- 客户端策略：声明长度 ≤ 4 MiB 走单发；否则走会话。阈值是客户端的调度选择，不是协议契约。
- 服务端契约：该路由**显式**声明请求体上限 16 MiB（用 `IHttpMaxRequestBodySizeFeature`，与 `ApplicationDeploymentEndpoints.cs:179-183` 同一手法）并在超限时返回 `413` + `upload-too-large-for-single-shot`。上限从"默认值"变成"声明值"。
- 两条路由最终写入**同一个** `LocalFileService` 写入器（单发 = 一个分片 + 提交，见 §4.1 的复用点），因此覆盖语义、`FileEntryDto` 形状、提权两段式都不可能分叉。
- 这不是 `AGENTS.md` 所禁止的"向后兼容别名/双格式解析"：两个路由的契约互斥且各自被显式声明（≤ 4 MiB 单发 vs `length` 由头承载的分块），没有对同一份载荷做两种解析。

---

## 4. 服务端实现

### 4.1 暂存布局与同卷原子提交

暂存文件直接落在**目标目录内**：

```
D:\Work\.big.iso.9f2c1a…rkup        ← 传输中的暂存文件（会话创建时 0 字节，边收边写）
D:\Work\big.iso                     ← 提交后才有；只由一次原子改名产生
```

理由：

1. **提交是同卷改名**，不是跨卷复制。跨卷暂存会让"提交"退化成一次 20 GB 的复制，还制造一个长时间的非原子窗口。
2. **权限模型自然正确**：程序自己就能写进去的目录，暂存与目标同权限；受保护目录则由 Helper 在同一目录内完成同样的步骤，用户授权的正是这个目录。
3. **与既有实现同源**：`WriteAtomicallyAsync`（`LocalFileService.cs:482-510`）今天就是"同目录 `.{name}.{guid}.tmp` → `File.Move(overwrite: true)`"。本设计只是让暂存文件的生命周期跨越多个请求，并把随机 guid 换成会话 id。

命名必须同时满足：以 `.` 开头（Linux/macOS 下隐藏）；含目标文件名（用户/运维可读）；含 32 位十六进制会话 id；以 `.rkup` 结尾。**清理程序只处理能从名字解析出会话 id 且该 id 在会话索引中的文件**——绝不根据"看起来像临时文件"删除任何东西（这条是硬约束：用户完全可能真有一个叫 `.foo.rkup` 的文件，而它不是我们的）。

### 4.2 会话索引与重启恢复

会话记录包含：`sessionId`、目标目录、净化文件名、暂存路径、声明长度、已确认偏移、`chunkSize`、`elevated`、身份键（用户/工作区/设备，与 `ExplorerOperationCenter.SessionKey` 同一构造口径）、创建/最后活动时间、幂等键、可选 `Idempotency-Key` 摘要。

存放位置：**服务端自有存储区的独立索引文件**（`{ApplicationRoot}/{RootDirectory}/uploads/index.json`，与 `ApplicationDeploymentStagingStore` 同样以 `ContentRootPath` 为根、不进入业务 SQLite 域），写入采用"临时文件 + 原子替换"，随每次偏移推进更新（一次分片一次小文件替换，可接受）。

- 为什么不用纯内存：50 GB 上传耗时可跨小时，服务端重启（部署、崩溃、User Mode 下的用户会话重启）后如果偏移归零，客户端只能从 0 重传——这不是"支持大文件"。索引让重启后续传成立。
- 为什么索引必须落盘（而不是扫描文件系统重建）：清理与恢复都只能针对**我们自己创建的东西**。没有索引，"找到所有暂存文件"就退化成全盘扫描，而那既昂贵又危险。
- 索引损坏或条目丢失时：对应暂存文件成为无法归属的孤儿，客户端 `GET` 得到 `upload-session-not-found`，按"服务端不认识这个会话"处理——**重新开始**，并且不删除该孤儿（交由人工/后续版本的孤儿回收；宁留文件不误删）。

### 4.3 长度、配额与磁盘守卫

| 约束 | 默认值 | 位置 | 违反时 |
| --- | --- | --- | --- |
| 单文件长度上限 | 1 TiB（可配） | 创建时 | 400 `invalid-input` |
| 暂存卷保留量 | 1 GiB | 创建时 | 507 `insufficient-storage` |
| 每身份会话数 | 4 | 创建时 | 429 `too-many-uploads` |
| 全局会话数 | 64 | 创建时 | 429 `too-many-uploads` |
| 单分片长度 | `chunkSize`（8/6 MiB） | 每个分片 | 413 `upload-chunk-too-large` |
| 单发请求体 | 16 MiB | 单发路由 | 413 |
| 空闲 TTL | 2 小时 | 读写时 + 清理 | 410 `upload-session-expired` |
| 绝对 TTL | 7 天 | 同上 | 同上 |

创建时的磁盘守卫在**写入 0 字节之前**执行；同时要求暂存文件每次 `FlushAsync` 后校验实际长度未超过声明长度，超过即中止并删除暂存文件（防止"声称 1 GB、实传 100 GB"）。

### 4.4 清理与 TTL

一个后台服务（`UploadSessionSweeper`，周期 15 分钟）只做三件事，且都从索引出发：

1. 索引中超过 TTL 的会话 → 删除暂存文件（受保护目录经 Helper 删除）→ 移除条目。
2. 索引中暂存文件已不存在（用户删了目录、外部清理）的会话 → 移除条目并记账，使客户端下次 `GET` 得到可解释的 `404`。
3. 索引文件本身损坏/条目不可解析 → 不做任何删除，只记录警告（宁留孤儿不误删）。

不承诺"服务端崩溃后自动重放/恢复**传输进度以外的东西**"；恢复的对象只有"偏移 + 暂存文件"，这与 `RelaxKonOS.Explorer.Operations.md:27` 对服务端任务"重启不重放"的结论不矛盾——上传的驱动方始终是客户端。

### 4.5 授权：会话级提权固定

今天的提权模型是 5 分钟滑动窗口（`IFileElevationSessionStore`），用户在弹窗里授权"对目录 X 做 upload"，过期即失效。用它承载一个三小时的大文件上传会得到两种坏结果：要么每 5 分钟弹一次窗（滥用用户），要么在传输中途 403（不可用）。

设计：**会话记录自己的提权决定（`elevated: true`），并且只在创建时向提权存储求证一次**。

- 创建：目标目录不可写 → `403 elevation-required` → 客户端弹窗 → 授权后重试创建 → 服务端确认 `IsElevated(user, Upload, targetDirectoryPath)` 成立 → 通过 Helper 创建暂存文件 → 记录 `elevated: true`。
- 分片与提交：`elevated` 会话**不再查询提权存储**，直接走 Helper。这就是"固定"，也是为什么提权会话的每个分片不会先白白失败一次（对比今天 `FileEndpoints.cs:397-410` 的"先失败再重试"模式，那会让每个分片浪费一次 I/O）。
- 审计与可撤销：会话创建、提交各写一条审计（actor、`jti` 引用、路径哈希、字节数、结果、会话 id）；`DELETE` 会话立即撤销；TTL 上限（7 天）是这条长期授权的硬边界。
- 不能成为"任意文件写入"的提权原语。新增的两个 Helper 操作（`FileUploadChunk`、`FileUploadCommit`）形状固定，且 Helper 侧强制校验：
  - `FileUploadChunk`：目标路径必须以 `.` 开头、以 `.rkup` 结尾、文件名含 32 位十六进制会话 id；`Offset` 必须落在 `[0, MaximumFileContentBytes × 上限]`；`ContentBase64` 解码后 ≤ 6 MiB。
  - `FileUploadCommit`：`Path`（暂存）必须在 `TargetDirectoryPath` 之内且满足同一命名规则；不接受任意外部路径；`Overwrite` 恒为 true（与既有 `FileUpload` 的覆盖语义一致）。
  - 校验失败 → `PrivilegedProblemCode.ResourceNotAllowed`，并记账。这样即使 Server 侧被攻破，Helper 也不会变成"以管理员身份写任意路径"的通道。

### 4.6 审计

新建/提交/放弃/超限拒绝各写一条结构化日志（不写文件名全文，写路径哈希与字节数，与 SMB 模块的审计口径一致）。分片本身不逐条审计（量级太大），只在会话结束时汇总已收字节数与分片数。

### 4.7 服务端改动清单

| 文件 | 改动 |
| --- | --- |
| `Shared/RelaxKonOS.Protocol/Files/FileApiRoutes.cs` | 新增 4 个路由常量 |
| `Shared/RelaxKonOS.Protocol/Files/UploadContracts.cs`（新） | `CreateUploadRequest` / `UploadSessionDto` / `CommitUploadRequest` |
| `Shared/RelaxKonOS.Protocol/Privileged/PrivilegedOperationContracts.cs` | 新增 `FileUploadChunk` / `FileUploadCommit` 两个 `PrivilegedOperationKind`，字段沿用现有 `Path`/`FileName`/`ContentBase64`，新增 `Offset`（`long`） |
| `RelaxKonOS.Server/Files/UploadSessionStore.cs`（新） | 会话索引：创建、查询、推进偏移、移除、重启加载、TTL 扫描 |
| `RelaxKonOS.Server/Files/UploadSessionService.cs`（新） | 创建/追加/提交/放弃的业务实现；磁盘守卫、配额、门闩、幂等 |
| `RelaxKonOS.Server/Files/FileUploadNamePolicy.cs`（新） | 文件名净化与暂存名解析（服务端唯一入口） |
| `RelaxKonOS.Server/Endpoints/FileEndpoints.cs` | 新增 4 个映射；单发路由加显式体上限；`fileName` 改走净化策略（§1.6 的修复） |
| `RelaxKonOS.Server/Files/LocalFileService.cs` | 提取"提交暂存文件到目标"的终止步骤供单发与会话共用；`UploadAsync` 的单发路径保持行为不变 |
| `RelaxKonOS.Server/Privileged/PrivilegedFileService.cs` | 新增 `AppendChunkAsync` / `CommitAsync`；`UploadAsync` 保留给单发快路径 |
| `RelaxKonOS.PrivilegedHelper/Program.cs` | 新增两个操作的分发与形状校验 |
| `RelaxKonOS.Server/Program.cs` | 注册 `UploadSessionStore`（单例）、`UploadSessionService`（单例）、`UploadSessionSweeper`（托管服务） |
| `RelaxKonOS.Server.Tests/UploadSessionChecks.cs`（新） | 会话校验（§9.1，86 条）：净化、幂等、偏移、长度、并发、提交、清理、重启恢复、提权路径 |
| `RelaxKonOS.Server.Tests/Program.cs` | 新增 `--uploads-only` 窄口径开关（与既有开关并列） |

---

## 5. 桌面客户端

### 5.1 必须同时修的三件事

只加协议不改这三处，桌面端仍然传不了大文件：

1. **去掉上传路径上的整包缓冲**：分块上传使用**独立的** `HttpClient`（类型化客户端 `IExplorerUploadChannel` → `ExplorerUploadChannel`），它的消息处理器链不包含 `AuthenticatedHttpHandler` 的缓冲逻辑，而是只做"取 token → 设置头"（`UploadAuthenticationHandler`）。401 的处理改为：**不重放**，把 401 交给上传编排器 → 编排器刷新 token → 取其权威偏移 → 从其继续。这正是分块协议带来的红利：重试不再需要正文副本。
2. **去掉整请求超时**：该客户端 `Timeout = Timeout.InfiniteTimeSpan`（同 `ApplicationDeployments` 的做法），并**同时**引入分片级停滞看门狗——无超时的 HttpClient 遇到半死连接会永久挂住，必须由看门狗负责：每个分片带一个滑动过期的 `CancellationTokenSource`（每写入一个缓冲即续期），超过 60 秒无字节推进即取消该分片、重试。
3. **进度来自提交而非读取**：`ProgressStreamContent` 在分块路径上的角色改为"在途字节"；界面进度 = `已确认偏移 + 在途字节`，且**永不超过** `已确认偏移 + chunkSize`。本地读得再快，界面也不会先走满。

### 5.2 上传编排器

新增 `Client/RelaxKonOS.Client/Apps/Explorer/Uploads/LargeFileUploader.cs`（普通类 + 接口 `ILargeFileUploader`），职责单一：拿一个"本地文件 → 远端目标"的计划项，把它传完。

```text
EnsureSessionAsync()
  ├─ 续传日志里有可用会话 → GET /files/uploads/{id}
  │     ├─ 200 → 采用服务端 offset（可能小于本地记忆）
  │     ├─ 404/410（会话没了）/ 403（不是本身份的会话）→ 丢弃日志条目，走新建
  │     └─ 其他（离线、服务端正在重启）→ 上抛"无法确认上次进度"，**保留条目**
  │           ——绝不静默另开一个会话：那会泄漏一个暂存文件并把传输劈成两半
  └─ 无 → POST /files/uploads
        └─ 403 elevation-required → 现有提权弹窗 → 重试创建（只重试创建，不重传数据）

PumpAsync()
  ceiling = 服务端下发的 chunkSize；floor = min(1 MiB 偏好, ceiling)；chunkSize 从 ceiling 起步
  loop:
    offset = 已确认偏移；若 offset == length → 退出
    chunk  = 读取 [offset, min(offset+chunkSize, length))   // 永远 ≤ ceiling
    写入 PATCH，带 Upload-Offset: offset，带停滞看门狗
      ├─ 204 → 已确认偏移 = 响应 Upload-Offset；失败计数归零；chunkSize 向 ceiling 翻倍
      ├─ 会话丢失（session-not-found / expired）→ 删除条目，新建会话，从新会话偏移继续
      ├─ 401 → 刷新 token，GET 取权威偏移，继续（不重放正文）
      ├─ upload-offset-mismatch / concurrent-chunk / chunk-too-large / length-required
      │     → 采用响应携带的权威偏移继续（`chunk-too-large` 额外把 chunkSize 减半，下限 floor）
      │     → **偏移没有前进就计入同一份重试预算**：服务端反复给同一个答复时必须有出口
      ├─ 5xx / 传输异常 → 退避后 GET 取权威偏移，继续；chunkSize 减半（下限 floor）
      └─ 4xx 终态（507/429/413/文件名）→ 抛出，保留会话以便用户处理后继续

CommitAsync() → POST .../commit（带 sha256 时先本地算）
    ├─ upload-incomplete → 回到 PumpAsync（权威偏移已带回）
    └─ 201 → 发布 FileEntryDto，删除续传日志条目
```

失败预算：单文件最多 10 次传输级失败（指数退避 + 抖动，上限 30 秒）；**"服务端反复回答同一偏移"这类活锁与传输失败共用这份预算**——`upload-offset-mismatch` 一类答复都是"不是失败"，若不计入预算，一个始终不前进的答复就能把循环变成永久挂起，而且不报任何错。耗尽后**不删除会话**，把作业标记为失败并保留日志条目——用户点"重试"就是从权威偏移继续，而不是从头开始。

### 5.3 续传日志

`Client/RelaxKonOS.Client/Apps/Explorer/Uploads/UploadResumeJournal.cs`，落在客户端应用私有配置区（`docs/development/RelaxKonOS.AppSettings.md` 的存储，不写注册表/不写仓库）：

```
{ "entries": [ { "uploadId": "9f2…", "serverKey": "<serverUrl>/<user>/<workspace>/<device>",
                 "targetDirectoryPath": "D:\\Work", "fileName": "big.iso",
                 "sourcePath": "C:\\Users\\nana\\big.iso", "sourceLength": 21474836480,
                 "sourceLastWriteUtc": "2026-09-24T09:12:00Z", "recordedAt": "…" } ] }
```

规则：

- **条目里不存偏移**。续传的唯一真相是服务端，所以条目只记录"这是哪个会话、它的源是谁"，恢复时一律 `GET /files/uploads/{id}` 取权威偏移。少存一个字段就少一种"本地记忆与服务端不一致"的可能，也让写入频率与分片数无关。
- **只在会话边界写入**：创建会话成功后、以及会话丢失后重建成功时。不按分片写、不做高频写。
- 恢复时除了 `GET` 会话，还要核对 `sourcePath + sourceLength + sourceLastWriteUtc`——本地文件被改过就必须重新开始（否则会拼出一个既非旧也非新的文件）。
- `serverKey` 与 `ExplorerOperationCenter.SessionKey` 同口径：切服务器/账号/工作区后**不得**用旧日志去新服务器续传（与 `RelaxKonOS.Explorer.Operations.md:27` 的结论一致）。
- 条目上限 64，超过按最旧淘汰；读取时丢弃超过 7 天的条目（与服务端绝对 TTL 对齐）。
- 日志文件损坏 → 视为空，不影响上传（最坏情况是从头传）。
- **删除条目的一定是终态动作**：提交成功、用户显式取消/放弃、源已变化、会话丢失后重建（替换同文档的旧条目）。失败（含重试预算耗尽）**不删**。

### 5.4 进度语义

- 单一窗口内只报告两类数字：**已提交字节**（服务端 204 之后才计入）与**在途字节**（当前分片已写入 socket 的字节）。
- 百分比分母是声明长度；长度未知（会话要求声明长度，故分块路径必然已知）时不显示百分比。
- 速度/剩余时间：只有在"最近 10 秒内至少确认过两个分片"时才显示，用已提交字节的实测斜率；否则显示不确定进度。不伪造（沿用 `RelaxKonOS.Explorer.Operations.md:9` 的既有要求）。
- 重同步导致权威偏移回退时（例如服务端只收到半个分片），已提交字节数**不允许**回退显示；界面显示"正在与服务器核对进度"，核对完成后以权威值为准继续单调前进。两条落地要求：
  - 核对态必须是**独立字段**而不是"字节数为 0 的报告"：`0` 会被监视进度的代码读成"从头开始"。桌面端 `LargeFileUploadProgress.Reconciling`、Android 端 `UploadState.resynchronising` 都只为回答"此刻有没有值得显示的数字"。
  - 核对期间**不显示字节数**而显示文案（`explorer.status.upload_reconciling`、`files_upload_reconciling`），且前台通知与界面同口径。操作中心每个条目的行只承载"路径 + 字节数"（`FileOperationDto` 没有状态文案字段），因此在操作中心里核对只表现为**计数停住**——这是该界面既有契约的边界，不是遗漏。

### 5.5 取消与重试

- 取消 → 立即停住分片循环 → **先删续传日志条目、再 `DELETE` 会话**（尽力而为，失败交给 TTL）→ 目标目录里不留任何东西（目标文件只由提交产生）。顺序是有意的：条目先没，才不会有"用户取消了、下次却还被问要不要继续"的状态。
- 唯一兜不住的那个窗口：取消恰好落在**创建会话的那一次往返**里。服务端可能已经建好会话，而它的 id 从未到达客户端——此时客户端既不能删也记不住，只能交给会话的空闲 TTL（2 小时）。这是 HTTP 取消的固有边界，也正是服务端必须有 TTL 的原因。
- 传播方式：`OperationCanceledException` 必须**原样上抛**而不是包装成失败。UI 层把它呈现为"已取消"，把它当失败显示会让用户以为传输出错。
- 与现有 `ExplorerOperationCenter` 的取消语义一致：取消是终态，不提供"暂停/恢复"（暂停/恢复是后续功能，协议已经具备支持它的全部条件：`GET` 偏移 + 保留会话）。操作中心的取消令牌与"身份/服务器切换"共用一个源，因此切换登录时正在跑的传输也会走这条成对清理的路径——那正是想要的结果：旧身份的会话不该留着。
- 提权在会话创建阶段解决，因此"传了 40 分钟才弹提权框"这类体验问题在新路径上不可能出现。

### 5.6 与操作中心的集成

`ExplorerOperationCenter.QueueUpload` 的形状（`items, totalBytes, Func<Action<string,long,int>, CancellationToken, Task>`）保持不变，编排器放在它下游：

- 大文件条目逐个进入编排器，**文件级串行**；跨文件的并发沿用操作中心既有调度（全局最多两个任务、重叠路径串行）。
- 每个条目的"当前路径"取自编排器上报（分片边界更新），字节数来自已提交偏移。
- `Dropbox`/剪贴板粘贴/文件夹递归上传三条入口全部经 `LocalUploadPlan`，因此**不需要**各自适配：阈值判定发生在同一个地方（`LocalUploadPlan` 之后、队列之前）。
- 编排器向上报告的是三元组 `(已确认字节, 在途字节, 是否在核对)`（`Action<long,long,bool>`），两条分支各自把它翻译成自己那套界面契约，**不允许**把 `Reconciling` 丢掉当成一个字节数：
  - 操作中心分支：每个条目的行只有"路径 + 字节数"，因此核对时**直接不上报新数字**，表现为计数停住（`RelaxKonOS.Explorer.Operations.md` 的行形状不变）。
  - 无操作中心（直接传输）分支：`TransferText` 切到 `explorer.status.upload_reconciling`，进度条原地不动。
- 这两条分支的分工在 `Tests/Client/RelaxKonOS.Explorer.Tests/LargeUploadChecks.cs` 里被同时锁住：一条检查"真实编排器至少上报一次 `Reconciling`"，另一条检查"上报的进度永不回退"。

### 5.7 桌面改动清单

| 文件 | 改动 |
| --- | --- |
| `Services/Bootstrapper.cs` | 注册 `UploadAuthenticationHandler`、类型化 `HttpClient`（`Timeout = InfiniteTimeSpan`、`ConnectTimeout = 15s`、不缓冲正文）、`UploadResumeJournal` 与 `ILargeFileUploader`（`serverKey` 与 `ExplorerOperationCenter.SessionKey` 同口径） |
| `Services/Auth/UploadAuthenticationHandler.cs`（新） | 只给上传请求附加当前 token 的轻处理器；401 **不重放**，原样返回给编排器 |
| `Apps/Explorer/Uploads/IExplorerUploadChannel.cs`（新） | 上传数据面接口；`UploadChannelException` 同时承载"服务端有没有给出裁决"（`IsTransport`）与"服务端实际持有多少"（`AuthoritativeOffset`） |
| `Apps/Explorer/Uploads/ExplorerUploadChannel.cs`（新） | 实现：固定长度流式分片、`Upload-Offset` 头、问题码与权威偏移的解析 |
| `Apps/Explorer/Uploads/LargeFileUploader.cs`（新） | 编排器（§5.2）；分片停滞看门狗、重试预算、`Reconciling` 上报都在这里 |
| `Apps/Explorer/Uploads/UploadResumeJournal.cs`（新） | 续传日志（§5.3） |
| `Apps/Explorer/ExplorerApp.cs` | 把 `ILargeFileUploader` 注入 `ExplorerViewModel`；解析不到时全部退回单发路由（仍可传，只是传不了大文件） |
| `Apps/Explorer/ViewModels/ExplorerViewModel.cs` | `UploadSourcesAsync` 的两条分支（有/无操作中心）都改为"按阈值分派到单发或编排器"；进度合成改为 §5.4 口径 |
| `Localization/{zh-CN,en-US,ja-JP}/explorer.json` | 新增 `explorer.status.upload_reconciling`；三语键集保持一致 |
| **未改动** | `Apps/Explorer/IExplorerClient.cs` / `ExplorerClient.cs` 的单发路由契约不变（它本来就接收 `fileName`）；`AuthenticatedHttpHandler` 行为不变——`UploadAuthenticationHandler` 的注释写明"上传通道禁止缓冲正文"的原因 |

---

## 6. Android 客户端（摘要）

Android 的客户端细节由 [`RelaxKonOS.Mobile.BulkUpload.Design.md`](../../Client/RelaxKonOS.Client.Android/docs/RelaxKonOS.Mobile.BulkUpload.Design.md) 拥有（AGENTS.md 的文档归属规则）。此处只固定与本协议耦合、不允许两端分歧的四点：

1. **源必须是可寻址的**。SAF 的一次性 `InputStream` 不能用于续传。策略：优先以 `openFileDescriptor` + 可定位通道按偏移读取；**不可寻址或长度未知的源先落应用私有缓存的临时文件**（阶段显示"准备中"），再从缓存文件分块上传；缓存文件在成功/取消/失败后删除，并在开始前检查可用空间。
2. **传输必须移出页面作用域**：跑在前台服务（`dataSync` 类型，带进度通知与通知内取消）中，而不是 `FilesScreen.viewModelScope`；转移卡片的状态持有者上移到 `MobileNavHost` 可见的应用级作用域（与 `MobileNavHost.kt:84` 的既有约定一致：下载/上传的生命周期长于发起它的页面）。
3. **每分片一次请求**，`Upload-Offset` 与固定长度流式模式（`setFixedLengthStreamingMode(long)`）；分片请求使用独立的读超时（不沿用 20 秒的普通 API 读超时），网络切换视为可重试的传输失败。
4. **同一份协议与同一套偏移规则**：Android 的 `RelaxKonGateway`/`RelaxKonApi` 不得自建偏移算术；`GET` 会话后按权威偏移继续。改网关接口必须同步 `FakeGateway`，否则 JVM 测试直接编译不过。

---

## 7. 反向代理与部署

分块协议对中间层更友好（每个请求都小），但**必须显式配置**，否则新的上限会替代旧的：

```nginx
# 目标：/api/v1.0/files/uploads 相关路由
client_max_body_size 12m;          # ≥ 会话 chunkSize(8 MiB) + 信封余量；不要用 1m 默认值
client_body_timeout 120s;          # 单分片写入间隔，不是整个文件
proxy_request_buffering off;       # 关键：不要缓冲整个分片再转发
proxy_buffering off;
proxy_read_timeout 120s;           # 与分片停滞看门狗同一量级
proxy_send_timeout 120s;
```

- 只对 `files/uploads` 前缀放宽，其余 API 保持既有严格值。
- 仓库内的 Nginx 管理器在生成站点配置时，对 `DisableBuffering` 路由已输出 `proxy_request_buffering off`（`RelaxKonOS.Server/WebServer/NginxWebServerManager.cs:696`），因此"关闭缓冲"在本仓库是既有能力，不是新概念。
- 用 IIS/ARR 或其它前置时按同一组语义配置；任何"单请求上限低于一个分片"的中间层都必须检查（它的失败模式是"每个分片都被拒"，而不是"传不了大文件"）。
- 这组值已写进部署文档的 **「反向代理与分块上传」** 一节（[`deployment/README.md`](../../deployment/README.md)），并说明 User Mode 下 Server 只绑定 `127.0.0.1`、控制套接字不经代理，SSH 本地转发不存在代理缓冲。

---

## 8. 迁移与一次性切换

按 `AGENTS.md` 的 API 演进策略，**不保留兼容垫片**：

- 新协议落地时，两个客户端的上传**一次性**切到"阈值分派"；不存在"老客户端 + 新服务端"或"新客户端 + 老服务端"的组合需要支持（同仓库同版本发布）。
- `POST /files/upload` 保留但其契约被重新声明为"≤ 4 MiB 单发"，服务端对超出该契约的请求返回 `413` + `upload-too-large-for-single-shot`。这**不是**旧版本的兼容路由，而是一个被重新定义的服务端点；文档（`RelaxKonOS.Explorer.md`、`RelaxKonOS.Explorer.Progress.md`）在同一改动内更新。
- 旧的行为差异必须在同一改动内对齐记录：`LocalFileService.UploadAsync` 的静默覆盖语义被会话提交继承（不引入 `already-exists`）；`file.FileName` 未净化的缺陷（§1.6）在同一次改动修掉，并补一条服务端回归检查。

---

## 9. 验收

### 9.1 服务端自动化（`RelaxKonOS.Server.Tests`，新增 `--uploads-only` 窄口径）

窄口径开关是 `--uploads-only`（`UploadSessionChecks.RunAsync`，86 条），与既有的 `--performance-only` / `--settings-only` / `--file-operations-only` / `--git-conflicts-only` 并列。**特权路径没有独立的窄口径开关**：第 6 组直接在完整套件里跑（`UPLOAD 69–86`），因为那组断言依赖 Helper 替身与真实提权存储两个夹具，拆开会失去它们同处一个进程的意义。注意未知开关会被静默忽略（等于跑全量）。

1. 创建：名称净化（分隔符/`..`/保留名/超长各一条）、目录不存在、磁盘守卫（伪造可用空间）、会话数上限、幂等键同/异内容。
2. 分片：偏移不一致 → 409 + 权威偏移；越过声明长度 → 409 且偏移不变；超 `chunkSize` → 413；缺 `Content-Length` → 411；同会话并发 → 409；异身份访问 → 404（且不泄漏存在性）。
3. 提交：偏移不足 → 409 + 权威偏移；长度匹配提交 → 目标内容逐字节等于源、`FileEntryDto.Size` 正确、暂存文件消失；覆盖既有文件成功（与单发语义一致）；哈希不匹配 → 409 且目标未被替换。
4. 清理：TTL 过期 → 暂存删除 + 条目移除；外部删除暂存文件 → 条目移除且不抛；**索引不可解析时不删除任何文件**。
5. 重启恢复：写入若干分片 → 重新构造存储 → 偏移保持、可继续、可提交。
6. 提权路径：目标目录不可写 → 创建返回 `elevation-required`；授权后创建成功且 `elevated: true`；分片与提交**不再**查询提权存储（可用计数替身断言）；Helper 侧形状校验拒绝不合命名规则的路径。
7. 单发路由：声明上限生效（16 MiB → 413 + 问题码）；≤ 4 MiB 行为与旧版一致。

### 9.2 桌面客户端自动化

1. 编排器（假传输）：正常完成；分片响应丢失 → `GET` 后从权威偏移继续；权威偏移**小于**本地记忆 → 界面不回退且从权威值继续；401 → 刷新后不重放正文；停滞看门狗在无进展时取消并重试；失败 10 次后保留会话与日志条目。
2. 续传日志：往返、越龄淘汰、`serverKey` 变化后不续传、本地文件被改（长度/时间戳）后不续传、损坏文件视为空。
3. 阈值分派：4 MiB 走单发、4 MiB + 1 走会话；文件夹递归与剪贴板粘贴两条入口都生效。
4. 断言峰值内存：上传一个 256 MiB 的**稀疏**文件时，进程托管堆增长不超过 64 MiB（这条测试直接锁死 B1 不回归）。
5. 报告形状：真实编排器（不是测试替身）在一次"分片响应丢失"里至少上报一次 `Reconciling`。缺少这一条时，界面唯一能做的就是把核对态的 `0` 当成进度显示出来。
6. 三条"没有它就会退化成挂起、永久拒绝或泄漏"的反向检查：
   - 服务端反复回答同一个不前进的答复（`upload-offset-mismatch`）时，预算必须耗尽并给出失败，会话与日志条目保留——**这条检查在修复前会直接把测试挂死**。
   - 会话下发的 `chunkSize` 低于客户端偏好的下限（32 KiB）时，分片长度必须始终 ≤ 该值且传输能完成；下限取 1 MiB 会让这种会话每次都被拒。
   - 取消必须**成对清理**：会话被放弃（`DELETE`）、条目被删除、没有提交。少任何一半都会留下"用户取消了却还能继续"或"取消后目录里躺着一个隐藏暂存文件直到两小时后"。

   前两条还要求测试替身像真实服务端那样**拒绝超过自己声明大小的分片**，否则检查会假绿。

实现落点：`Tests/Client/RelaxKonOS.Explorer.Tests/LargeUploadChecks.cs`（**196 条检查**，含内存不变量、"丢失的响应没有让任何字节二次过线"、停滞答复的预算、分片上限与取消的成对清理）。

### 9.3 Android

完整验收清单由 [`RelaxKonOS.Mobile.BulkUpload.Design.md` §10](../../Client/RelaxKonOS.Client.Android/docs/RelaxKonOS.Mobile.BulkUpload.Design.md) 拥有（AGENTS.md 的文档归属规则）。这里固定两端**不允许分歧**、且已在 JVM 层被锁住的四点：

1. **同一份偏移规则**：Android 断言的是"权威偏移小于本地记忆时采用权威值且不回退显示"，而不是自己算一遍偏移；`RelaxKonGateway`/`RelaxKonApi` 里没有偏移算术。
2. **核对态是独立字段**：`UploadState.resynchronising`（桌面端对应 `LargeFileUploadProgress.Reconciling`），文案键 `files_upload_reconciling`；核对期间界面与前台通知都不显示字节数。
3. **无进展的答复与传输失败共用重试预算**（`MAXIMUM_CHUNK_FAILURES = 10`）：服务端反复回答同一偏移时必须有出口；耗尽后**会话与续传条目都保留**，只把条目状态标为失败。
4. **分片长度 ≤ 服务端下发的 `chunkSize`**，自适应缩小的下限同样被该值夹住（`min(客户端偏好, ceiling)`）。

JVM 覆盖（`app/src/test/java/app/relaxkonos/mobile/data/`，共 **41 项**，与全仓 273 项一起全绿）：`UploadCoordinatorTest`(14)、`UploadSourceStagerTest`(14)、`UploadResumeJournalTest`(13)。真机矩阵（前台服务、网络切换、强杀重启、受保护目录提权）**无法**用编译或 JVM 测试代替，见 Android 文档 §10.2。

### 9.4 真实宿主验收（必须在目标环境执行，不能以编译代替）

| 场景 | 通过标准 |
| --- | --- |
| 20 GB 单文件，局域网 | 完成，峰值内存稳定，目标文件哈希一致 |
| 传到 50% 杀客户端，重启后重试 | 从权威偏移继续，不重传前 50% |
| 传到 50% 重启服务端，再重试 | 同上（会话索引恢复生效） |
| 传到 50% 拔网线 30 秒再接回 | 自动续传，用户无需操作 |
| Wi-Fi → LTE 切换（Android） | 分片级重试后继续，不从头 |
| 受保护目录（如 `C:\Program Files\...`）传 1 GB | 弹一次提权，随后完成（验证 12 MiB 天花板已消失） |
| 取消一个 5 GB 上传 | 目标目录无目标文件、无暂存残留（或 TTL 后被清理） |
| 1000 个 100 KB 文件 | 走单发快路径，无会话泄漏 |

---

## 10. 分阶段交付

| 阶段 | 内容 | 出口条件 | 状态 |
| --- | --- | --- | --- |
| P1 协议 + 服务端 | 路由/DTO/问题码、`UploadSessionStore`/`Service`、清理服务、单发路由显式上限、文件名净化 | §9.1 全绿；两个旧客户端仍可用单发路由（≤ 4 MiB） | **已完成**：`--uploads-only` 86/86 PASS |
| P2 特权路径 | `FileUploadChunk` / `FileUploadCommit`、Helper 形状校验、会话级提权固定 | §9.1 第 6 组全绿（该组无独立窄口径开关，见 §9.1） | **已完成**：完整套件内 `UPLOAD 69–86` 全绿，含 FILE SERVICES / FILE JOB / 缩略图等既有组 |
| P3 桌面客户端 | 上传通道 HttpClient、编排器、续传日志、进度语义、操作中心集成 | §9.2 全绿 + §9.4 前三行 | **已完成**：`LargeUploadChecks` 196/196 全绿；`RelaxKonOS.Client` 与校验项目均 0 错误 0 新警告。§9.4 前三行属真机验收，未执行 |
| P4 Android | 可寻址策略与缓存落盘、前台服务、分片循环、续传日志 | Android 文档验收节 | **已完成（JVM 层）**：全仓 273 项全绿，其中上传相关 41 项；真机矩阵待执行 |
| P5 文档与部署 | README 索引、Explorer 文档、Android 文档、`deployment/` 代理配置说明 | 文档与代码同一次改动内一致 | **已完成**：本文件、`RelaxKonOS.Protocol.md` 路由表、`RelaxKonOS.Explorer.md`/`.Operations.md`/`.Progress.md`、两份 `docs/README.md`、Android 大上传设计、`deployment/README.md`（反向代理一节）均与代码同批更新 |

> 复现命令：服务端 `RelaxKonOS.Server.Tests.exe --uploads-only`（或直接跑全量）；桌面端 `RelaxKonOS.Explorer.Tests.exe`（打印"196 Explorer regression checks passed."）；Android `gradle :app:testDebugUnitTest`。

---

## 11. 风险与取舍

| 风险 | 取舍 |
| --- | --- |
| 会话级提权把 5 分钟窗口延长到小时级 | 换取可用性。缓解：绑定身份、路径形状校验、会话 TTL 上限、即时撤销、审计；Helper 侧不接受任意路径（§4.5） |
| 暂存文件出现在用户目录里 | 换取同卷原子提交与正确的权限上下文。缓解：`.` 前缀 + 确定性命名 + 会话创建时 0 字节即预留；不跨卷复制 20 GB |
| 一次分片一次 HTTP 往返 | 8 MiB 分片下往返开销可忽略（对比：单发路由仍在，小文件不受影响） |
| 不并行分片 → 单文件无法跑满高带宽链路 | 明确取舍（§2.2）。协议已预留（偏移头是显式的），将来加乱序只需服务端支持空洞位图 |
| 服务端索引是新的可损坏状态 | 换取重启续传。损坏时按"会话不存在"降级为重新开始；任何情况下不按猜测删文件 |
| Android 需要新前台服务 | 换取后台可用性。需要新权限声明、通知渠道与用户可取消；这是 Android 平台上"长时间传输"的唯一正当做法 |

---

## 参考

- [RFC 9110 PATCH](https://www.rfc-editor.org/rfc/rfc9110#name-patch)、[Content-Range / 部分表示](https://www.rfc-editor.org/rfc/rfc9110#name-range-requests)
- [tus resumable upload protocol](https://tus.io/protocols/resumable-upload)（本协议与其 `Upload-Offset` 头一脉相承，但不引入其扩展机制；本仓库不接受标准未定义的分片元数据）
- [ASP.NET Core 请求体大小上限](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/options)、[IHttpMaxRequestBodySizeFeature](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.features.ihttpmaxrequestbodysizefeature)
- [HttpClient.Timeout 语义](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.timeout)
- [Android 前台服务类型 dataSync](https://developer.android.com/develop/background-work/services/fg-service-types)
- 本仓库：[文件操作中心](../applications/RelaxKonOS.Explorer.Operations.md)（上传仍是客户端驱动传输的结论来源）、[特权操作](../platform/RelaxKonOS.PrivilegedOperations.Operations.md)（Helper 不接受任意路径的原则来源）
