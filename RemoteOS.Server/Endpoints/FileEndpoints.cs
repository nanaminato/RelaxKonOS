using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RemoteOS.Protocol.Files;
using Server.Identity;
using Server.Storage;
using Server.Files;
using Server.Privileged;

namespace Server.Endpoints;

/// <summary>文件管理 REST 端点。路由常量见 <see cref="FileApiRoutes"/>。所有端点需 JWT（[Authorize]）。
/// 错误统一返回 RFC 7807 ProblemDetails，错误码通过 type URI 传递（仿 <see cref="AuthEndpoints"/>）。
/// 服务端以宿主 OS 进程身份执行 IO，复用宿主用户/权限。</summary>
public static class FileEndpoints
{
    private const string ProblemBase = "https://remoteos.app/problems/";

    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapFileOperationEndpoints();

        // GET drives
        app.MapGet(FileApiRoutes.Drives, (IFileService fs) =>
            Results.Ok(fs.GetDrives()))
           .RequireAuthorization(FileAuthorizationPolicies.List)
           .WithTags("Files");

        // GET special — 跨平台枚举家目录/桌面/文档/下载/图片/音乐/视频（已 Directory.Exists 过滤）
        app.MapGet(FileApiRoutes.Special, (HttpContext http, IFileService fs, IUserRepository users, IIdentityProvider identity) =>
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
        app.MapGet(FileApiRoutes.List, (string? path, IFileService fs) =>
        {
            try { return Results.Ok(fs.GetDirectory(path)); }
            catch (DirectoryNotFoundException ex) { return Problem(404, "not-found", "路径不存在", ex.Message); }
            catch (UnauthorizedAccessException ex) { return Problem(403, "access-denied", "访问被拒", ex.Message); }
            catch (IOException ex) { return Problem(503, "device-unavailable", "设备不可用", ex.Message); }
            catch (ArgumentException ex) { return Problem(400, "invalid-path", "路径无效", ex.Message); }
        })
        .RequireAuthorization(FileAuthorizationPolicies.List)
        .WithTags("Files");

        // GET info?path=
        app.MapGet(FileApiRoutes.Info, (string path, IFileService fs) =>
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
        app.MapGet(FileApiRoutes.Download, async (string path, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
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

        app.MapGet(FileApiRoutes.Content, async (string path, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
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

        app.MapPut(FileApiRoutes.Content, async (string path, HttpRequest request, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations) =>
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
        app.MapPost(FileApiRoutes.Elevation, (FileElevationRequest request, HttpContext http, IFileService fs,
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
                var direct = fs.OpenRead(request.Path);
                if (direct is null) return Problem(404, "not-found", "Not found", $"Cannot find {request.Path}");
                using var stream = direct.Value.Stream;
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
        .WithTags("Files");

        app.MapGet(FileApiRoutes.Properties, (string path, IFileService fs) =>
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

        app.MapPut(FileApiRoutes.Permissions, (UpdateUnixPermissionsRequest request, IFileService fs) =>
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
        app.MapPost(FileApiRoutes.Directory, async (string path, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
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
        app.MapDelete(FileApiRoutes.Delete, async (string path, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
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
        app.MapPost(FileApiRoutes.Rename, async (RenameRequest req, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
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
        app.MapPost(FileApiRoutes.Move, async (MoveRequest req, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
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
        app.MapPost(FileApiRoutes.Copy, async (CopyRequest req, HttpContext http, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations, CancellationToken ct) =>
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

        // POST upload?path=
        app.MapPost(FileApiRoutes.Upload, async (HttpContext ctx, IFileService fs, IPrivilegedFileService privileged, IFileElevationSessionStore elevations) =>
        {
            var path = ctx.Request.Query["path"].ToString();
            if (string.IsNullOrWhiteSpace(path))
                return Problem(400, "invalid-input", "输入无效", "query path 不能为空");
            if (!ctx.Request.HasFormContentType)
                return Problem(415, "unsupported-media-type", "不支持的媒体类型", "需 multipart/form-data");

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Problem(400, "invalid-input", "输入无效", "未提供文件");

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

        return app;
    }

    private static IResult Problem(int status, string typeSuffix, string title, string detail)
        => Results.Problem(detail: detail, statusCode: status, title: title, type: ProblemBase + typeSuffix);

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
