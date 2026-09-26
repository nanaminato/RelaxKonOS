using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using RelaxKonOS.Protocol.Files;
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
            });

        // GET drives
        files.MapGet(FileApiRoutes.Drives, (IFileService fs) =>
            Results.Ok(fs.GetDrives()))
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
        files.MapGet(FileApiRoutes.List, async (string? path, HttpContext http, IFileService fs, IPrivilegedFileService privileged,
            IFileElevationSessionStore elevations, CancellationToken ct) =>
        {
            try { return Results.Ok(fs.GetDirectory(path)); }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex)
            {
                if (string.IsNullOrWhiteSpace(path) || !elevations.IsElevated(http.User, FileElevationCapability.Read, path))
                    return Problem(403, "elevation-required", "需要管理员权限", ex.Message);
                try { return Results.Ok(await privileged.ListDirectoryAsync(path, ct)); }
                catch (DirectoryNotFoundException privilegedEx) { return Problem(404, "not-found", "路径不存在", privilegedEx.Message); }
                catch (UnauthorizedAccessException privilegedEx) { return Problem(403, "access-denied", "访问被拒", privilegedEx.Message); }
                catch (InvalidOperationException helperEx) { return Problem(503, "privileged-helper-unavailable", "特权助手不可用", helperEx.Message); }
                catch (IOException helperEx) { return Problem(500, "io-error", "I/O 错误", helperEx.Message); }
            }
            catch (IOException ex) { return Problem(503, "device-unavailable", "设备不可用", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
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
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "访问被拒", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.List)
        .WithTags("Files");

        // GET download?path=
        files.MapGet(FileApiRoutes.Download, async (string path, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
        {
            try
            {
                var r = fs.OpenRead(path);
                if (r is not (var stream, var contentType, var fileName))
                    return Problem(404, "not-found", "文件不存在", $"找不到: {path}");
                return Results.File(stream, contentType, fileName);
            }
            catch (UnauthorizedAccessException ex)
            {
                if (!elevations.IsElevated(http.User, FileElevationCapability.Read, path)) return Problem(403, "elevation-required", "需要管理员权限", ex.Message);
                try
                {
                    var r = await privileged.OpenReadAsync(path, ct);
                    return Results.File(r.Stream, "application/octet-stream", r.FileName);
                }
                catch (FileNotFoundException) { return Problem(404, "not-found", "文件不存在", $"找不到: {path}"); }
                catch (UnauthorizedAccessException privilegedEx) { return Problem(403, "access-denied", "访问被拒", privilegedEx.Message); }
                catch (InvalidOperationException helperEx) { return Problem(503, "privileged-helper-unavailable", "特权助手不可用", helperEx.Message); }
                catch (IOException helperEx) { return Problem(500, "io-error", "I/O 错误", helperEx.Message); }
            }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Read)
        .WithTags("Files");

        // GET thumbnail?path=&maxEdge=
        files.MapGet(FileApiRoutes.Thumbnail, async (string path, int? maxEdge, HttpContext http, IFileService fs,
            IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
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
            catch (UnauthorizedAccessException ex)
            {
                if (!elevations.IsElevated(http.User, FileElevationCapability.Read, path)) return Problem(403, "elevation-required", "需要管理员权限", ex.Message);
                try
                {
                    var r = await privileged.OpenReadAsync(path, ct);
                    using (r.Stream) return Thumbnail(r.Stream, edge, path);
                }
                catch (FileNotFoundException) { return Problem(404, "not-found", "文件不存在", $"找不到: {path}"); }
                catch (UnauthorizedAccessException privilegedEx) { return Problem(403, "access-denied", "访问被拒", privilegedEx.Message); }
                catch (InvalidOperationException helperEx) { return Problem(503, "privileged-helper-unavailable", "特权助手不可用", helperEx.Message); }
                catch (IOException helperEx) { return Problem(500, "io-error", "I/O 错误", helperEx.Message); }
            }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Read)
        .WithTags("Files");

        files.MapGet(FileApiRoutes.Content, async (string path, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
        {
            try
            {
                var result = fs.OpenRead(path);
                if (result is not (var stream, var contentType, _))
                    return Problem(404, "not-found", "Not found", $"Cannot find {path}");
                return Results.File(stream, contentType);
            }
            catch (UnauthorizedAccessException ex)
            {
                if (!elevations.IsElevated(http.User, FileElevationCapability.Read, path)) return Problem(403, "elevation-required", "需要管理员权限", ex.Message);
                try
                {
                    var r = await privileged.OpenReadAsync(path, ct);
                    return Results.File(r.Stream, "application/octet-stream");
                }
                catch (FileNotFoundException) { return Problem(404, "not-found", "Not found", $"Cannot find {path}"); }
                catch (UnauthorizedAccessException privilegedEx) { return Problem(403, "access-denied", "Access denied", privilegedEx.Message); }
                catch (InvalidOperationException helperEx) { return Problem(503, "privileged-helper-unavailable", "Privileged helper unavailable", helperEx.Message); }
                catch (IOException helperEx) { return Problem(500, "io-error", "I/O error", helperEx.Message); }
            }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Read)
        .WithTags("Files");

        files.MapPut(FileApiRoutes.Content, async (string path, HttpRequest request, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations) =>
        {
            // A denied atomic write may happen after the incoming body has already been consumed.
            // Keep a replayable copy so the elevated retry writes the exact same bytes.
            await using var content = new MemoryStream();
            await request.Body.CopyToAsync(content, request.HttpContext.RequestAborted);
            content.Position = 0;
            try { return Results.Ok(await fs.WriteFileAsync(path, content, request.HttpContext.RequestAborted)); }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "Target directory not found", ex.Message); }
            catch (UnauthorizedAccessException ex)
            {
                if (!elevations.IsElevated(request.HttpContext.User, FileElevationCapability.Write, path)) return Problem(403, "elevation-required", "需要管理员权限", ex.Message);
                try
                {
                    content.Position = 0;
                    return Results.Ok(await privileged.WriteAsync(path, content, request.HttpContext.RequestAborted));
                }
                catch (DirectoryNotFoundException directoryEx) { return Problem(404, "not-found", "Target directory not found", directoryEx.Message); }
                catch (UnauthorizedAccessException privilegedEx) { return Problem(403, "access-denied", "Access denied", privilegedEx.Message); }
                catch (InvalidOperationException helperEx) { return Problem(503, "privileged-helper-unavailable", "Privileged helper unavailable", helperEx.Message); }
                catch (IOException helperEx) { return Problem(500, "io-error", "I/O error", helperEx.Message); }
            }
            catch (IOException ex) { return Problem(500, "io-error", "I/O error", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Write)
        .WithTags("Files");

        // First call without a password merely probes direct access. When it is denied, the
        // desktop prompts and repeats with the login user's host password. A successful grant
        // is constrained to this JWT and exact path for five minutes.
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
                    return GrantElevation(request, http, administrators, elevations);
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
                return GrantElevation(request, http, administrators, elevations);
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
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "Access denied", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.List)
        .WithTags("Files");

        files.MapPut(FileApiRoutes.Permissions, (UpdateUnixPermissionsRequest request, IFileService fs) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Problem(400, "invalid-path", "Invalid path", "Path cannot be empty.");
            try { return Results.Ok(fs.SetUnixPermissions(request.Path, request.UnixMode)); }
            catch (PlatformNotSupportedException ex) { return Problem(409, "unsupported-operation", "Unsupported operation", ex.Message); }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "Not found", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "Access denied", ex.Message); }
            catch (ArgumentOutOfRangeException ex) { return Problem(400, "invalid-mode", "Invalid permissions", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "Invalid path", ex.Message); }
        })
        .RequireAuthorization()
        .WithTags("Files");

        // POST directory?path=
        files.MapPost(FileApiRoutes.Directory, async (string path, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
        {
            try
            {
                fs.CreateDirectory(path);
                return Results.Created(GetInfoLocation(path), fs.GetInfo(path));
            }
            catch (IOException ex) { return Problem(409, "already-exists", "已存在", ex.Message); }
            catch (UnauthorizedAccessException ex)
            {
                if (!elevations.IsElevated(http.User, FileElevationCapability.CreateDirectory, path)) return Problem(403, "elevation-required", "需要管理员权限", ex.Message);
                try
                {
                    await privileged.CreateDirectoryAsync(path, ct);
                    return Results.Created(GetInfoLocation(path), fs.GetInfo(path));
                }
                catch (UnauthorizedAccessException privilegedEx) { return Problem(403, "access-denied", "访问被拒", privilegedEx.Message); }
                catch (InvalidOperationException helperEx) { return Problem(503, "privileged-helper-unavailable", "特权助手不可用", helperEx.Message); }
                catch (IOException helperEx) { return Problem(409, "already-exists", "已存在", helperEx.Message); }
            }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Write)
        .WithTags("Files");

        // DELETE files?path=
        files.MapDelete(FileApiRoutes.Delete, async (string path, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
        {
            try
            {
                fs.Delete(path);
                return Results.NoContent();
            }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "路径不存在", ex.Message); }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex)
            {
                if (!elevations.IsElevated(http.User, FileElevationCapability.Delete, path)) return Problem(403, "elevation-required", "需要管理员权限", ex.Message);
                try { await privileged.DeleteAsync(path, ct); return Results.NoContent(); }
                catch (UnauthorizedAccessException privilegedEx) { return Problem(403, "access-denied", "访问被拒", privilegedEx.Message); }
                catch (InvalidOperationException helperEx) { return Problem(503, "privileged-helper-unavailable", "特权助手不可用", helperEx.Message); }
                catch (IOException helperEx) { return Problem(500, "io-error", "IO 错误", helperEx.Message); }
            }
            catch (IOException ex) { return Problem(500, "io-error", "IO 错误", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Manage)
        .WithTags("Files");

        // POST rename
        files.MapPost(FileApiRoutes.Rename, async (RenameRequest req, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.SourcePath) || string.IsNullOrWhiteSpace(req.NewName))
                return Problem(400, "invalid-input", "输入无效", "sourcePath 与 newName 不能为空");
            try { return Results.Ok(fs.Rename(req.SourcePath, req.NewName)); }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "源路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex)
            {
                var target = RenameTarget(req.SourcePath, req.NewName);
                if (!elevations.IsElevated(http.User, FileElevationCapability.Rename, req.SourcePath, target)) return Problem(403, "elevation-required", "需要管理员权限", ex.Message);
                try { return Results.Ok(await privileged.RenameAsync(req.SourcePath, req.NewName, ct)); }
                catch (UnauthorizedAccessException privilegedEx) { return Problem(403, "access-denied", "访问被拒", privilegedEx.Message); }
                catch (InvalidOperationException helperEx) { return Problem(503, "privileged-helper-unavailable", "特权助手不可用", helperEx.Message); }
                catch (IOException helperEx) { return Problem(409, "already-exists", "目标已存在", helperEx.Message); }
            }
            catch (IOException ex) { return Problem(409, "already-exists", "目标已存在", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Manage)
        .WithTags("Files");

        // POST move
        files.MapPost(FileApiRoutes.Move, async (MoveRequest req, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.SourcePath) || string.IsNullOrWhiteSpace(req.DestinationPath))
                return Problem(400, "invalid-input", "输入无效", "sourcePath 与 destinationPath 不能为空");
            try { return Results.Ok(fs.Move(req.SourcePath, req.DestinationPath, req.Overwrite)); }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "源路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex)
            {
                if (!elevations.IsElevated(http.User, FileElevationCapability.Move, req.SourcePath, req.DestinationPath)) return Problem(403, "elevation-required", "需要管理员权限", ex.Message);
                try { return Results.Ok(await privileged.MoveAsync(req.SourcePath, req.DestinationPath, req.Overwrite, ct)); }
                catch (UnauthorizedAccessException privilegedEx) { return Problem(403, "access-denied", "访问被拒", privilegedEx.Message); }
                catch (InvalidOperationException helperEx) { return Problem(503, "privileged-helper-unavailable", "特权助手不可用", helperEx.Message); }
                catch (IOException helperEx) { return Problem(409, "already-exists", "目标已存在", helperEx.Message); }
            }
            catch (IOException ex) { return Problem(409, "already-exists", "目标已存在", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Manage)
        .WithTags("Files");

        // POST copy
        files.MapPost(FileApiRoutes.Copy, async (CopyRequest req, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.SourcePath) || string.IsNullOrWhiteSpace(req.DestinationPath))
                return Problem(400, "invalid-input", "输入无效", "sourcePath 与 destinationPath 不能为空");
            try { return Results.Ok(fs.Copy(req.SourcePath, req.DestinationPath, req.Overwrite)); }
            catch (FileNotFoundException ex) { return Problem(404, "not-found", "源路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex)
            {
                if (!elevations.IsElevated(http.User, FileElevationCapability.Copy, req.SourcePath, req.DestinationPath)) return Problem(403, "elevation-required", "需要管理员权限", ex.Message);
                try { return Results.Ok(await privileged.CopyAsync(req.SourcePath, req.DestinationPath, req.Overwrite, ct)); }
                catch (UnauthorizedAccessException privilegedEx) { return Problem(403, "access-denied", "访问被拒", privilegedEx.Message); }
                catch (InvalidOperationException helperEx) { return Problem(503, "privileged-helper-unavailable", "特权助手不可用", helperEx.Message); }
                catch (IOException helperEx) { return Problem(409, "already-exists", "目标已存在", helperEx.Message); }
            }
            catch (IOException ex) { return Problem(409, "already-exists", "目标已存在", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.Manage)
        .WithTags("Files");

        // POST upload?path= — the small-file fast path. Its ceiling is declared here rather than inherited
        // from Kestrel's 30 MB default and the form reader's 128 MiB default, so a client that overruns it
        // gets a named answer instead of an opaque 413 from an unrelated layer.
        files.MapPost(FileApiRoutes.Upload, async (HttpContext ctx, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations) =>
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
            catch (UnauthorizedAccessException ex)
            {
                if (!elevations.IsElevated(ctx.User, FileElevationCapability.Upload, path)) return Problem(403, "elevation-required", "需要管理员权限", ex.Message);
                try
                {
                    await using var retryStream = file.OpenReadStream();
                    var dto = await privileged.UploadAsync(path, file.FileName, retryStream, ctx.RequestAborted);
                    return Results.Created(GetInfoLocation(dto.Path), dto);
                }
                catch (DirectoryNotFoundException directoryEx) { return Problem(404, "not-found", "目标目录不存在", directoryEx.Message); }
                catch (UnauthorizedAccessException privilegedEx) { return Problem(403, "access-denied", "访问被拒", privilegedEx.Message); }
                catch (InvalidOperationException helperEx) { return Problem(503, "privileged-helper-unavailable", "特权助手不可用", helperEx.Message); }
                catch (IOException helperEx) { return Problem(500, "io-error", "IO 错误", helperEx.Message); }
            }
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

    private static IResult GrantElevation(FileElevationRequest request, HttpContext http, IHostAdministratorAuthenticator administrators, IFileElevationSessionStore elevations)
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
        return Results.Ok(new FileElevationResult(true, true, expires));
    }

    private static string RenameTarget(string sourcePath, string newName)
        => Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, newName);

    /// <summary>
    /// Builds an ASCII-safe resource URI for a created file-system entry. Host paths may contain
    /// Unicode (for example, Chinese file names), which cannot be written directly to Location.
    /// </summary>
    private static string GetInfoLocation(string path)
        => $"{FileApiRoutes.Info}?path={Uri.EscapeDataString(path)}";
}
