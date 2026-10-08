using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Files;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>文件管理 REST 端点。路由常量见 <see cref="FileApiRoutes"/>。所有端点需 JWT（[Authorize]）。
/// 错误统一返回 RFC 7807 ProblemDetails，错误码通过 type URI 传递（仿 <see cref="AuthEndpoints"/>）。
/// 服务端以宿主 OS 进程身份执行 IO，复用宿主用户/权限。</summary>
public static class FileEndpoints
{
    private const string ProblemBase = "https://relaxkonos.app/problems/";

    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapFileOperationEndpoints();
        var files = app.MapGroup("")
            .AddEndpointFilter(async (EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
            {
                try { return await next(context); }
                catch (UserExecutionException exception)
                {
                    return (object)UserExecutionProblemResult.From(exception);
                }
                catch (HostFileExecutionException exception)
                {
                    return (object)Problem(exception.StatusCode, exception.ProblemCode,
                        exception.ProblemCode, exception.Message);
                }
            });

        files.MapGet(FileApiRoutes.Text, (string path, IFileService fs, CancellationToken ct) =>
            TextResult(async () => Results.Ok(await new TextFileEditor(fs).ReadAsync(path, ct))))
            .RequireAuthorization(FileAuthorizationPolicies.Read).WithTags("Files");
        files.MapPut(FileApiRoutes.Text, (string path, SaveTextFileRequest request, IFileService fs, CancellationToken ct) =>
            TextResult(async () => await new TextFileEditor(fs).SaveAsync(path, request, ct) is { } saved
                ? Results.Ok(saved) : Problem(409, "text-file-changed", "File changed", "Reload the file before saving.")))
            .RequireAuthorization(FileAuthorizationPolicies.Write).WithTags("Files")
            .WithMetadata(new RequestSizeLimitAttribute(2 * 1024 * 1024));
        files.MapPost(FileApiRoutes.Text, (string path, CreateTextFileRequest request, IFileService fs, CancellationToken ct) =>
            TextResult(async () => Results.Ok(await new TextFileEditor(fs).CreateAsync(path, request, ct))))
            .RequireAuthorization(FileAuthorizationPolicies.Write).WithTags("Files")
            .WithMetadata(new RequestSizeLimitAttribute(2 * 1024 * 1024));

        // GET drives
        files.MapGet(FileApiRoutes.Drives, (HttpContext http, IFileService fs, IHostFileAuthorizationService authorizations) =>
        {
            var drives = fs.GetDrives();
            if (!OperatingSystem.IsLinux() || !authorizations.IsRoot(http.User)) return Results.Ok(drives);

            // A root session can be restricted to selected Helper roots even though the host has a
            // POSIX '/' drive. Confirm its actual list capability before advertising that drive to
            // the client; a denied probe reveals no directory entries and avoids a dead-end row.
            return Results.Ok(drives.Select(drive => string.Equals(drive.Path, "/", StringComparison.Ordinal)
                ? drive with { IsBrowsable = CanListRootDirectory(fs) }
                : drive));
        })
           .RequireAuthorization(FileAuthorizationPolicies.List)
           .WithTags("Files");

        // GET special — 跨平台枚举家目录/桌面/文档/下载/图片/音乐/视频（已 Directory.Exists 过滤）
        files.MapGet(FileApiRoutes.Special, (HttpContext http, IFileService fs, IUserRepository users, IIdentityProvider identity) =>
        {
            var subject = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(subject, out var userId) || users.FindById(userId) is not { } user)
                return Results.Unauthorized();

            var homeDirectory = identity.GetUserInfo(user.Username).HomeDirectory;
            return Results.Ok(fs.GetSpecialLocations(homeDirectory));
        })
           .RequireAuthorization(FileAuthorizationPolicies.List)
           .WithTags("Files");

        // GET list?path=
        files.MapGet(FileApiRoutes.List, ListDirectoryResult)
        .RequireAuthorization(FileAuthorizationPolicies.List)
        .WithTags("Files");

        // GET info?path=
        files.MapGet(FileApiRoutes.Info, (string path, IFileService fs) =>
        {
            try
            {
                var dto = fs.GetInfo(path);
                return dto is null ? Problem(404, "not-found", "路径不存在", $"找不到: {path}") : Results.Ok(dto);
            }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.List)
        .WithTags("Files");

        // GET download?path=
        files.MapGet(FileApiRoutes.Download, (string path, IFileService fs) =>
        {
            try
            {
                var r = fs.OpenRead(path);
                if (r is not (var stream, var contentType, var fileName))
                    return Problem(404, "not-found", "文件不存在", $"找不到: {path}");
                return Results.File(stream, contentType, fileName);
            }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Read)
        .WithTags("Files");

        // GET thumbnail?path=&maxEdge=
        files.MapGet(FileApiRoutes.Thumbnail, (string path, int? maxEdge, IFileService fs) =>
        {
            var edge = maxEdge ?? ImageThumbnailRenderer.DefaultMaxEdge;
            if (edge is < ImageThumbnailRenderer.MinimumMaxEdge or > ImageThumbnailRenderer.MaximumMaxEdge)
                return Problem(400, "invalid-size", "缩略图尺寸无效",
                    $"maxEdge 须在 {ImageThumbnailRenderer.MinimumMaxEdge}–{ImageThumbnailRenderer.MaximumMaxEdge} 之间。");
            try
            {
                var r = fs.OpenRead(path);
                if (r is not (var stream, _, _))
                    return Problem(404, "not-found", "文件不存在", $"找不到: {path}");
                using (stream) return Thumbnail(stream, edge, path);
            }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Read)
        .WithTags("Files");

        files.MapGet(FileApiRoutes.Content, (string path, IFileService fs) =>
        {
            try
            {
                var result = fs.OpenRead(path);
                if (result is not (var stream, var contentType, _))
                    return Problem(404, "not-found", "Not found", $"Cannot find {path}");
                return Results.File(stream, contentType);
            }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Read)
        .WithTags("Files");

        files.MapPut(FileApiRoutes.Content, async (string path, HttpRequest request, IFileService fs) =>
        {
            // The file service owns the one permitted Helper fallback, so the body is consumed once.
            await using var content = new MemoryStream();
            await request.Body.CopyToAsync(content, request.HttpContext.RequestAborted);
            content.Position = 0;
            try { return Results.Ok(await fs.WriteFileAsync(path, content, request.HttpContext.RequestAborted)); }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "Target directory not found", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (IOException ex) { return Problem(500, "io-error", "I/O error", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Write)
        .WithTags("Files");

        // First call without a password merely probes direct access. When it is denied, the
        // desktop prompts and repeats with the login user's host password. A successful grant
        // is constrained to this JWT and capability for five minutes. Read authorization for a
        // directory covers its subtree so browsing and opening a file share the same grant.
        files.MapPost(FileApiRoutes.Elevation, (FileElevationRequest request, HttpContext http, IFileService fs,
            IHostAdministratorAuthenticator administrators, IFileElevationSessionStore elevations) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Problem(400, "invalid-path", "Invalid path", "Path cannot be empty.");
            try
            {
                // Mutating operations have already failed with elevation-required on the first
                // attempt. Their target may not be readable (and uploads need not exist yet), so
                // grant their requested directory scope after host authentication instead of
                // probing it with OpenRead.
                if (request.IncludeDescendants)
                    return GrantElevation(request, http, administrators, elevations, fs);
                var info = fs.GetInfo(request.Path);
                if (info is null) return Problem(404, "not-found", "Not found", $"Cannot find {request.Path}");
                if (info.Type == FileSystemEntryType.Directory)
                    _ = fs.GetDirectory(request.Path);
                else
                {
                    var direct = fs.OpenRead(request.Path);
                    if (direct is null) return Problem(404, "not-found", "Not found", $"Cannot find {request.Path}");
                    using var stream = direct.Value.Stream;
                }
                return Results.Ok(new FileElevationResult(false, false));
            }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "Not found", ex.Message); }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "Not found", ex.Message); }
            catch (UnauthorizedAccessException)
            {
                return GrantElevation(request, http, administrators, elevations, fs);
            }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Read)
        .AddEndpointFilter(new ServerModeEndpointFilter(ServerHostFeature.PrivilegedOperations))
        .WithTags("Files");

        files.MapGet(FileApiRoutes.Properties, (string path, IFileService fs) =>
        {
            try
            {
                var properties = fs.GetProperties(path);
                return properties is null ? Problem(404, "not-found", "Not found", $"Cannot find {path}") : Results.Ok(properties);
            }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.List)
        .WithTags("Files");

        files.MapPut(FileApiRoutes.Permissions, (UpdateUnixPermissionsRequest request, IFileService fs) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Problem(400, "invalid-path", "Invalid path", "Path cannot be empty.");
            try { return Results.Ok(fs.SetUnixPermissions(request.Path, request.UnixMode, request.Recursive)); }
            catch (PlatformNotSupportedException ex) { return Problem(409, "unsupported-operation", "Unsupported operation", ex.Message); }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "Not found", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (ArgumentOutOfRangeException ex) { return Problem(400, "invalid-mode", "Invalid permissions", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        // POST directory?path=
        files.MapPost(FileApiRoutes.Directory, (string path, IFileService fs) =>
        {
            try
            {
                fs.CreateDirectory(path);
                return Results.Created(GetInfoLocation(path), fs.GetInfo(path));
            }
            catch (IOException ex) { return Problem(409, "already-exists", "已存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Write)
        .WithTags("Files");

        // DELETE files?path=
        files.MapDelete(FileApiRoutes.Delete, (string path, IFileService fs) =>
        {
            try
            {
                fs.Delete(path);
                return Results.NoContent();
            }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "路径不存在", ex.Message); }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (IOException ex) { return Problem(500, "io-error", "IO 错误", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Manage)
        .WithTags("Files");

        // POST rename
        files.MapPost(FileApiRoutes.Rename, (RenameRequest req, IFileService fs) =>
        {
            if (string.IsNullOrWhiteSpace(req.SourcePath) || string.IsNullOrWhiteSpace(req.NewName))
                return Problem(400, "invalid-input", "输入无效", "sourcePath 与 newName 不能为空");
            try { return Results.Ok(fs.Rename(req.SourcePath, req.NewName)); }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "源路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (IOException ex) { return Problem(409, "already-exists", "目标已存在", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Manage)
        .WithTags("Files");

        // POST move
        files.MapPost(FileApiRoutes.Move, (MoveRequest req, IFileService fs) =>
        {
            if (string.IsNullOrWhiteSpace(req.SourcePath) || string.IsNullOrWhiteSpace(req.DestinationPath))
                return Problem(400, "invalid-input", "输入无效", "sourcePath 与 destinationPath 不能为空");
            try { return Results.Ok(fs.Move(req.SourcePath, req.DestinationPath, req.Overwrite)); }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "源路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (IOException ex) { return Problem(409, "already-exists", "目标已存在", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Manage)
        .WithTags("Files");

        // POST copy
        files.MapPost(FileApiRoutes.Copy, (CopyRequest req, IFileService fs) =>
        {
            if (string.IsNullOrWhiteSpace(req.SourcePath) || string.IsNullOrWhiteSpace(req.DestinationPath))
                return Problem(400, "invalid-input", "输入无效", "sourcePath 与 destinationPath 不能为空");
            try { return Results.Ok(fs.Copy(req.SourcePath, req.DestinationPath, req.Overwrite)); }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "源路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (IOException ex) { return Problem(409, "already-exists", "目标已存在", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Manage)
        .WithTags("Files");

        // POST upload?path= — the small-file fast path. Its ceiling is declared here rather than inherited
        // from Kestrel's 30 MB default and the form reader's 128 MiB default, so a client that overruns it
        // gets a named answer instead of an opaque 413 from an unrelated layer.
        files.MapPost(FileApiRoutes.Upload, async (HttpContext ctx, IFileService fs) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            if (string.IsNullOrWhiteSpace(path))
                return Problem(400, "invalid-input", "输入无效", "query path 不能为空");
            if (ctx.Request.ContentLength is { } declared && declared > FileUploadProtocol.SingleShotMaximumBytes)
                return Problem(413, FileUploadProblemCodes.TooLargeForSingleShot, "文件过大",
                    $"单发上传上限为 {FileUploadProtocol.SingleShotMaximumBytes} 字节，更大的文件请使用分块上传会话。");
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = FileUploadProtocol.SingleShotMaximumBytes;
            if (!ctx.Request.HasFormContentType)
                return Problem(415, "unsupported-media-type", "不支持的媒体类型", "需 multipart/form-data");

            IFormCollection form;
            try
            {
                form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            }
            catch (InvalidDataException)
            {
                return Problem(413, FileUploadProblemCodes.TooLargeForSingleShot, "文件过大",
                    $"单发上传上限为 {FileUploadProtocol.SingleShotMaximumBytes} 字节，更大的文件请使用分块上传会话。");
            }
            catch (BadHttpRequestException badRequest) when (badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Problem(413, FileUploadProblemCodes.TooLargeForSingleShot, "文件过大",
                    $"单发上传上限为 {FileUploadProtocol.SingleShotMaximumBytes} 字节，更大的文件请使用分块上传会话。");
            }
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Problem(400, "invalid-input", "输入无效", "未提供文件");
            // The name reaches Path.Combine, so it must be one component and nothing else. Renaming it
            // silently would leave the user unable to find the file they just uploaded.
            if (!FileUploadNamePolicy.IsValidFileName(file.FileName))
                return Problem(400, FileUploadProblemCodes.InvalidFileName, "文件名无效",
                    $"文件名必须是单一成分: {FileUploadNamePolicy.DescribeForLog(file.FileName)}");

            try
            {
                await using var stream = file.OpenReadStream();
                var dto = await fs.UploadAsync(path, file.FileName, stream, ctx.RequestAborted);
                return Results.Created(GetInfoLocation(dto.Path), dto);
            }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "目标目录不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
            catch (IOException ex) { return Problem(500, "io-error", "IO 错误", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Write)
        .WithTags("Files");

        // ---- Resumable upload sessions --------------------------------------------------------------
        // The data plane for large files. A chunk is raw bytes, so the request body is never buffered as a
        // form and never read into a byte[]; the server streams it into the staging file at the offset the
        // client declares, and only then confirms that offset.

        // POST uploads — open a session.
        files.MapPost(FileApiRoutes.Uploads, async (CreateUploadRequest req, HttpContext ctx, UploadSessionService sessions, CancellationToken ct) =>
        {
            try
            {
                var dto = await sessions.CreateAsync(ctx.User, req, ctx.Request.Headers[FileUploadProtocol.IdempotencyKeyHeader].ToString(), ct);
                ctx.Response.Headers[FileUploadProtocol.OffsetHeader] = dto.Offset.ToString(CultureInfo.InvariantCulture);
                return Results.Created($"{FileApiRoutes.Uploads}/{dto.UploadId}", dto);
            }
            catch (UploadSessionException ex) { return UploadProblem(ctx, ex); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Write)
        .WithTags("Files");

        // GET uploads/{uploadId} — the authoritative offset, and the only way to resolve doubt.
        files.MapGet(FileApiRoutes.UploadPattern, (string uploadId, HttpContext ctx, UploadSessionService sessions) =>
        {
            try { return Results.Ok(sessions.Get(ctx.User, uploadId)); }
            catch (UploadSessionException ex) { return UploadProblem(ctx, ex); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.List)
        .WithTags("Files");

        // PATCH uploads/{uploadId} — append one chunk of raw bytes.
        files.MapPatch(FileApiRoutes.UploadChunkPattern, async (string uploadId, HttpContext ctx, UploadSessionService sessions, CancellationToken ct) =>
        {
            // Declared for the same reason as the single-shot ceiling: the largest legal chunk must be
            // reachable, and anything larger must be rejected by name.
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = FileUploadProtocol.DefaultChunkSize + 65_536;
            var offset = long.TryParse(ctx.Request.Headers[FileUploadProtocol.OffsetHeader].ToString(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (long?)null;
            try
            {
                var newOffset = await sessions.AppendAsync(ctx.User, uploadId, offset, ctx.Request.ContentLength, ctx.Request.Body, ct);
                ctx.Response.Headers[FileUploadProtocol.OffsetHeader] = newOffset.ToString(CultureInfo.InvariantCulture);
                return Results.NoContent();
            }
            catch (UploadSessionException ex) { return UploadProblem(ctx, ex); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Write)
        .WithTags("Files");

        // POST uploads/{uploadId}/commit — the only step that creates or replaces the destination file.
        files.MapPost(FileApiRoutes.UploadCommitPattern, async (string uploadId, CommitUploadRequest? req, HttpContext ctx, UploadSessionService sessions, CancellationToken ct) =>
        {
            try
            {
                var dto = await sessions.CommitAsync(ctx.User, uploadId, req?.ContentHash, ct);
                return Results.Created(GetInfoLocation(dto.Path), dto);
            }
            catch (UploadSessionException ex) { return UploadProblem(ctx, ex); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Write)
        .WithTags("Files");

        // DELETE uploads/{uploadId} — abandon. Always 204: the caller's goal is "it is not there".
        files.MapDelete(FileApiRoutes.UploadPattern, async (string uploadId, HttpContext ctx, UploadSessionService sessions, CancellationToken ct) =>
        {
            await sessions.AbortAsync(ctx.User, uploadId, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(FileAuthorizationPolicies.Write)
        .WithTags("Files");

        return app;
    }

    private static IResult ListDirectoryResult(string? path, IFileService fs)
    {
        try { return Results.Ok(fs.GetDirectory(path)); }
        // Identity and privileged execution boundaries use FileNotFoundException for missing
        // targets, including directories. Preserve that failure instead of reporting device IO.
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        { return Problem(404, "not-found", "路径不存在", ex.Message); }
        catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", "需要管理员权限", ex.Message); }
        catch (IOException ex) { return Problem(503, "device-unavailable", "设备不可用", ex.Message); }
        catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
    }

    private static async Task<IResult> TextResult(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (InvalidDataException) { return Problem(422, "text-file-invalid", "Invalid text", "Binary or oversized content cannot be edited."); }
        catch (System.Text.DecoderFallbackException) { return Problem(422, "text-file-invalid", "Invalid encoding", "The file is not valid Unicode text."); }
        catch (System.Text.EncoderFallbackException) { return Problem(422, "text-file-invalid", "Invalid encoding", "The content is not valid Unicode text."); }
        catch (FileNotFoundException) { return Problem(404, "not-found", "Not found", "Text file not found."); }
        catch (DirectoryNotFoundException) { return Problem(404, "not-found", "Not found", "Target directory not found."); }
        catch (UnauthorizedAccessException) { return Problem(403, "access-denied", "Access denied", "Conditional text editing requires an ordinary host identity with direct file access."); }
        catch (ArgumentException) { return Problem(400, "text-file-invalid", "Invalid text request", "Check the path, encoding and version."); }
        catch (IOException) { return Problem(503, "text-file-unavailable", "File unavailable", "Refresh to verify the file before trying again."); }
    }

    private static bool CanListRootDirectory(IFileService files)
    {
        try
        {
            _ = files.GetDirectory("/");
            return true;
        }
        catch (HostFileExecutionException error) when (error.ProblemCode == "access-denied")
        {
            return false;
        }
    }

    private static IResult Problem(int status, string typeSuffix, string title, string detail)
        => Results.Problem(detail: detail, statusCode: status, title: title, type: ProblemBase + typeSuffix);

    /// <summary>
    /// Renders an upload-session refusal. The two "resynchronise and continue" answers travel with the
    /// authoritative offset in a header, so a client never needs a second round trip to learn where to
    /// resume; the problem code is present as both the <c>type</c> suffix and the <c>problemCode</c>
    /// extension, which is how every RelaxKonOS client reads a stable code.
    /// </summary>
    private static IResult UploadProblem(HttpContext ctx, UploadSessionException exception)
    {
        if (exception.AuthoritativeOffset is { } offset)
            ctx.Response.Headers[FileUploadProtocol.OffsetHeader] = offset.ToString(CultureInfo.InvariantCulture);
        if (exception.StatusCode == StatusCodes.Status429TooManyRequests)
            ctx.Response.Headers.RetryAfter = "30";
        return Results.Problem(detail: exception.Message, statusCode: exception.StatusCode, title: exception.ProblemCode,
            type: ProblemBase + exception.ProblemCode,
            extensions: new Dictionary<string, object?> { ["problemCode"] = exception.ProblemCode });
    }

    /// <summary>Renders the small copy of an image, or says that this file has none to give.</summary>
    private static IResult Thumbnail(Stream source, int maxEdge, string path)
    {
        // 415 rather than 404 or 500: the file is there and the account may read it — the answer is
        // about what can be made of its content. A client reads this as "show the icon, fetch the
        // original instead", never as a failure to report.
        var rendered = ImageThumbnailRenderer.Render(source, maxEdge);
        return rendered is null
            ? Problem(415, "thumbnail-unsupported", "无法生成缩略图", $"内容不是本服务可解码的图像: {path}")
            : Results.File(rendered.Bytes, rendered.ContentType);
    }

    private static IResult GrantElevation(FileElevationRequest request, HttpContext http, IHostAdministratorAuthenticator administrators, IFileElevationSessionStore elevations, IFileService fs)
    {
        var username = http.User.FindFirstValue(JwtRegisteredClaimNames.Name);
        if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
        var authentication = administrators.Authenticate(username, request.AdministratorUsername, request.Password);
        if (!authentication.Succeeded)
            return Problem(403, authentication.ProblemCode, "管理员认证失败", "宿主管理员认证未通过，未执行操作。");
        if (request.Capability is not { } capability)
            return Problem(400, "elevation-capability-required", "需要操作能力", "请选择需要授权的文件操作。");
        var paths = new[] { request.Path }.Concat(request.RelatedPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToArray();
        var expires = paths.Select(path => elevations.Grant(http.User, capability, path, request.IncludeDescendants,
            authentication.AuthenticationMethod, http.TraceIdentifier)).Max();
        // Resolve the entry type only after the exact-path grant exists: ordinary users may
        // not even be able to stat a protected directory. Files retain exact-path grants.
        if (capability == FileElevationCapability.Read && !request.IncludeDescendants
            && fs.GetInfo(request.Path)?.Type == FileSystemEntryType.Directory)
            expires = elevations.Grant(http.User, capability, request.Path, includeDescendants: true,
                authentication.AuthenticationMethod, http.TraceIdentifier);
        return Results.Ok(new FileElevationResult(true, true, expires));
    }

    /// <summary>
    /// Builds an ASCII-safe resource URI for a created file-system entry. Host paths may contain
    /// Unicode (for example, Chinese file names), which cannot be written directly to Location.
    /// </summary>
    private static string GetInfoLocation(string path)
        => $"{FileApiRoutes.Info}?path={Uri.EscapeDataString(path)}";
}
