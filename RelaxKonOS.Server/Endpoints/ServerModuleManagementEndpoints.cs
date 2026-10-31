using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Endpoints;

internal static class ServerModuleManagementEndpoints
{
    internal const string ManagePolicy = "ServerModulesManage";
    internal static IEndpointRouteBuilder MapServerModuleManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization().WithTags("Server modules");
        group.MapGet(ServerModuleManagementRoutes.Detail, async (string moduleId, HttpContext http,
            ServerModules modules, ServerModuleContext context, CancellationToken ct) => await Respond(async () =>
        {
            var module = (await modules.DescribeAsync(http.RequestServices, context, ct)).SingleOrDefault(x => x.Id == moduleId);
            return module is null ? Problem("module-not-found", 404) : Results.Ok(ForActor(module, http.User, context.Configuration));
        }));
        group.MapPost(ServerModuleManagementRoutes.Plans, async (ServerModulePlanRequest request, HttpContext http,
            ModuleManagementStore store, ServerModules modules, ServerModuleContext context, CancellationToken ct) =>
            await Respond(async () => Results.Ok(store.Preview(Actor(http.User), request,
                await modules.DescribeAsync(http.RequestServices, context, ct)))))
            .RequireAuthorization(ManagePolicy);
        group.MapGet(ServerModuleManagementRoutes.Plan, (Guid planId, HttpContext http, ModuleManagementStore store) =>
            Respond(() => Task.FromResult<IResult>(Results.Ok(store.PlanStatus(Actor(http.User), planId)))))
            .RequireAuthorization(ManagePolicy);
        group.MapPost(ServerModuleManagementRoutes.Apply, async (Guid planId, HttpContext http,
            ModuleManagementExecutor executor, ServerModules modules, ServerModuleContext context, CancellationToken ct) =>
            await Respond(async () => Results.Ok(executor.Apply(Actor(http.User), planId,
                await modules.DescribeAsync(http.RequestServices, context, ct)))))
            .RequireAuthorization(ManagePolicy);
        group.MapGet(ServerModuleManagementRoutes.Operation, (Guid operationId, HttpContext http, ModuleManagementStore store) =>
            Respond(() => Task.FromResult<IResult>(Results.Ok(store.Operation(Actor(http.User), operationId)))))
            .RequireAuthorization(ManagePolicy);
        group.MapGet(ServerModuleManagementRoutes.Operations, (string? moduleId, int? offset, int? limit,
            HttpContext http, ModuleManagementStore store) => Respond(() => Task.FromResult<IResult>(
                Results.Ok(store.Operations(Actor(http.User), moduleId, offset ?? 0, limit ?? 25)))))
            .RequireAuthorization(ManagePolicy);
        group.MapGet(ServerModuleManagementRoutes.Releases, (string moduleId, string sourceId, int? offset, int? limit, string? expectedCatalogRevision,
            ModuleManagementStore store, ServerModules modules, ServerModuleContext context) => Respond(() => Task.FromResult<IResult>(
                Results.Ok(ModulePackageReleaseDirectory.Read(context, store, modules, moduleId, sourceId, offset ?? 0, limit ?? 25, DateTimeOffset.UtcNow, expectedCatalogRevision)))));
        group.MapGet(ServerModuleManagementRoutes.ModuleStoreSearch, (string? q, string? sourceId, int? offset, int? limit,
            ServerModuleContext context) => Respond(() => Task.FromResult<IResult>(Results.Ok(ModulePackageStore.Search(
                context.ContentRootPath, context.Configuration, context.Environment, q, sourceId, offset ?? 0,
                limit ?? 25, DateTimeOffset.UtcNow)))))
            .RequireAuthorization(ManagePolicy);
        group.MapPost(ServerModuleManagementRoutes.ModuleStoreDownload, async (ServerModuleStoreDownloadRequest request,
            ServerModuleContext context, CancellationToken cancellationToken) => await Respond(async () =>
            Results.Ok(await ModulePackageDownloader.DownloadAsync(context.ContentRootPath, context.Configuration,
                context.Environment, request.SourceId, request.ReleaseId, DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken))))
            .RequireAuthorization(ManagePolicy);
        group.MapGet(ServerModuleManagementRoutes.InstalledPackages, (ServerModuleContext context) => Respond(() =>
        {
            var current = InstalledModuleStore.Read(context.ContentRootPath);
            return Task.FromResult<IResult>(Results.Ok(new ServerInstalledModulePageDto(
                current.Select(module => InstalledDto(module, module)).ToArray())));
        })).RequireAuthorization(ManagePolicy);
        group.MapGet(ServerModuleManagementRoutes.PackageVersions, (string moduleId, ServerModuleContext context) => Respond(() =>
        {
            if (!ModulePackageHostConfiguration.Identifier(moduleId)) return Task.FromResult<IResult>(Problem("module-request-invalid", 400));
            var current = InstalledModuleStore.Read(context.ContentRootPath).SingleOrDefault(module => module.ModuleId == moduleId);
            return Task.FromResult<IResult>(Results.Ok(new ServerInstalledModulePageDto(
                InstalledModuleStore.ReadVersions(context.ContentRootPath, moduleId)
                    .Select(module => InstalledDto(module, current)).ToArray())));
        })).RequireAuthorization(ManagePolicy);
        group.MapGet(ServerModuleManagementRoutes.PackageOperations, (ServerModuleContext context) => Respond(() =>
            Task.FromResult<IResult>(Results.Ok(new ServerPackageOperationPageDto(
                ModulePackageOperationStore.Read(context.ContentRootPath)
                    .OrderByDescending(operation => operation.UpdatedAt)
                    .Select(OperationDto).ToArray())))))
            .RequireAuthorization(ManagePolicy);
        group.MapPost(ServerModuleManagementRoutes.PackageUpload, async (HttpRequest request,
            ServerModuleContext context, CancellationToken cancellationToken) => await Respond(async () =>
        {
            const long limit = 1L << 30;
            if (request.ContentLength is > limit) return Problem("module-package-upload-too-large", 413);
            var size = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = limit;
            var directory = Path.Combine(context.ContentRootPath, "data", "module-management", "staging", "uploads");
            Directory.CreateDirectory(directory);
            var uploadId = Guid.NewGuid().ToString("N");
            var path = Path.Combine(directory, uploadId + ".rkmodule");
            var temporary = path + ".tmp";
            long bytes = 0;
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                65536, FileOptions.Asynchronous))
                {
                    var buffer = new byte[65536];
                    int read;
                    while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        bytes += read;
                        if (bytes > limit)
                        {
                            try { File.Delete(temporary); } catch (IOException) { }
                            return Problem("module-package-upload-too-large", 413);
                        }
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                }
                if (bytes == 0) return Problem("module-package-upload-empty", 400);
                File.Move(temporary, path);
                return Results.Ok(new ServerModulePackageUploadDto(uploadId, bytes,
                    Convert.ToHexStringLower(hash.GetHashAndReset())));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        })).RequireAuthorization(ManagePolicy);
        group.MapPost(ServerModuleManagementRoutes.PackageInstall, async (ServerModulePackageInstallRequest request,
            ServerModuleContext context, CancellationToken cancellationToken) => await Respond(async () =>
        {
            if (!ModulePackageHostConfiguration.Identifier(request.SourceId)
                || !ModulePackageHostConfiguration.Identifier(request.ReleaseId)
                || !Guid.TryParseExact(request.UploadId, "N", out _))
                return Problem("module-package-install-request-invalid", 400);
            var path = Path.Combine(context.ContentRootPath, "data", "module-management", "staging", "uploads",
                request.UploadId + ".rkmodule");
            if (!File.Exists(path)) return Problem("module-package-upload-not-found", 404);
            var record = await InstalledModuleInstaller.InstallFromAcceptedCatalogAsync(context.ContentRootPath,
                context.Configuration, context.Environment, request.SourceId, path, request.ReleaseId,
                DateTimeOffset.UtcNow, cancellationToken);
            var previous = InstalledModuleStore.ReadVersions(context.ContentRootPath, record.ModuleId)
                .Where(module => module.Version != record.Version)
                .OrderByDescending(module => System.Version.Parse(module.Version)).FirstOrDefault();
            var operation = ModulePackageOperationStore.Record(context.ContentRootPath, "install", record,
                previous?.Version, DateTimeOffset.UtcNow);
            try { File.Delete(path); } catch (IOException) { }
            return Results.Ok(new ServerModulePackageInstallResultDto(operation.OperationId, InstalledDto(record, record)));
        })).RequireAuthorization(ManagePolicy);
        group.MapPost(ServerModuleManagementRoutes.PackagePlans, async (ServerModulePackageInstallRequest request,
            HttpContext http, ServerModuleContext context, CancellationToken cancellationToken) => await Respond(async () =>
        {
            if (!ModulePackageHostConfiguration.Identifier(request.SourceId)
                || !ModulePackageHostConfiguration.Identifier(request.ReleaseId)
                || !Guid.TryParseExact(request.UploadId, "N", out _))
                return Problem("module-package-plan-request-invalid", 400);
            var path = Path.Combine(context.ContentRootPath, "data", "module-management", "staging", "uploads",
                request.UploadId + ".rkmodule");
            if (!File.Exists(path)) return Problem("module-package-upload-not-found", 404);
            var candidate = await InstalledModuleInstaller.InspectAsync(context.ContentRootPath, context.Configuration,
                context.Environment, request.SourceId, path, request.ReleaseId, DateTimeOffset.UtcNow,
                takeHostLock: false, cancellationToken);
            var plan = ModulePackagePlanStore.Create(context.ContentRootPath, Actor(http.User), request.UploadId,
                candidate, DateTimeOffset.UtcNow);
            return Results.Ok(PlanDto(plan));
        })).RequireAuthorization(ManagePolicy);
        group.MapPost(ServerModuleManagementRoutes.PackageAutoPlan, async (ServerModulePackageAutoPlanRequest request,
            HttpContext http, ServerModuleContext context, CancellationToken cancellationToken) => await Respond(async () =>
        {
            if (!Guid.TryParseExact(request.UploadId, "N", out _))
                return Problem("module-package-plan-request-invalid", 400);
            var path = Path.Combine(context.ContentRootPath, "data", "module-management", "staging", "uploads",
                request.UploadId + ".rkmodule");
            if (!File.Exists(path)) return Problem("module-package-upload-not-found", 404);
            var info = new FileInfo(path);
            await using var stream = File.OpenRead(path);
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            var serviceId = ModuleManagementStore.ReadRecoveryReport(context.ContentRootPath).ServiceId;
            var document = ModulePackageCatalogStore.Read(context.ContentRootPath, serviceId);
            var candidates = new List<AcceptedModulePackageCandidate>();
            foreach (var sourceId in document.Sources.Keys.OrderBy(id => id == "official" ? 0 : 1).ThenBy(id => id, StringComparer.Ordinal))
            {
                try
                {
                    var policy = ModulePackageHostConfiguration.Source(context.Configuration, sourceId);
                    var verified = ModulePackageCatalogStore.ReadCurrentCatalog(document, policy,
                        ModulePackageHostConfiguration.Context(serviceId, context.Configuration, context.Environment), DateTimeOffset.UtcNow);
                    foreach (var release in verified.Catalog.Releases.Where(release =>
                                 release.ArchiveSha256 == digest && release.ArchiveBytes == info.Length))
                    {
                        try
                        {
                            candidates.Add(await InstalledModuleInstaller.InspectAsync(context.ContentRootPath,
                                context.Configuration, context.Environment, sourceId, path, release.Manifest.ReleaseId,
                                DateTimeOffset.UtcNow, takeHostLock: false, cancellationToken));
                        }
                        catch (ModulePackageException) { }
                    }
                }
                catch (ModulePackageException) { }
            }
            var groups = candidates.GroupBy(candidate => (candidate.Manifest.ModuleId, candidate.Manifest.Version,
                candidate.ArchiveSha256)).ToArray();
            if (groups.Length == 0) return Problem("module-package-upload-not-authorized", 404);
            if (groups.Length > 1) return Problem("module-package-upload-ambiguous", 409);
            var selected = groups[0].OrderBy(candidate => candidate.SourceId == "official" ? 0 : 1).First();
            var plan = ModulePackagePlanStore.Create(context.ContentRootPath, Actor(http.User), request.UploadId,
                selected, DateTimeOffset.UtcNow);
            return Results.Ok(PlanDto(plan));
        })).RequireAuthorization(ManagePolicy);
        group.MapPost(ServerModuleManagementRoutes.PackagePlanApply, async (Guid planId, HttpContext http,
            ServerModuleContext context, CancellationToken cancellationToken) => await Respond(async () =>
        {
            var actor = Actor(http.User);
            var now = DateTimeOffset.UtcNow;
            var plan = ModulePackagePlanStore.Get(context.ContentRootPath, planId, actor, now);
            if (plan.AppliedOperationId is { } applied)
            {
                var current = InstalledModuleStore.Read(context.ContentRootPath)
                    .SingleOrDefault(module => module.ModuleId == plan.ModuleId && module.Version == plan.Version
                        && module.ArtifactSha256 == plan.ArtifactSha256);
                return current is null ? Problem("module-package-plan-target-missing", 409)
                    : Results.Ok(new ServerModulePackageInstallResultDto(applied, InstalledDto(current, current)));
            }
            var path = Path.Combine(context.ContentRootPath, "data", "module-management", "staging", "uploads",
                plan.UploadId + ".rkmodule");
            if (!File.Exists(path)) return Problem("module-package-upload-not-found", 404);
            var candidate = await InstalledModuleInstaller.InspectAsync(context.ContentRootPath, context.Configuration,
                context.Environment, plan.SourceId, path, plan.ReleaseId, now, takeHostLock: false, cancellationToken);
            if (candidate.Manifest.ModuleId != plan.ModuleId || candidate.Manifest.Version != plan.Version
                || candidate.Manifest.PublisherId != plan.PublisherId || candidate.ArchiveSha256 != plan.ArtifactSha256
                || candidate.ManifestSha256 != plan.ManifestSha256)
                return Problem("module-package-plan-target-changed", 409);
            var previous = InstalledModuleStore.Read(context.ContentRootPath)
                .SingleOrDefault(module => module.ModuleId == plan.ModuleId);
            var record = await InstalledModuleInstaller.InstallFromAcceptedCatalogAsync(context.ContentRootPath,
                context.Configuration, context.Environment, plan.SourceId, path, plan.ReleaseId, now, cancellationToken);
            var operation = ModulePackageOperationStore.Record(context.ContentRootPath, "install", record, previous?.Version, now);
            ModulePackagePlanStore.MarkApplied(context.ContentRootPath, plan.PlanId, operation.OperationId, now);
            try { File.Delete(path); } catch (IOException) { }
            return Results.Ok(new ServerModulePackageInstallResultDto(operation.OperationId, InstalledDto(record, record)));
        })).RequireAuthorization(ManagePolicy);
        group.MapPost(ServerModuleManagementRoutes.PackageRollback, async (ServerModulePackageRollbackRequest request,
            ServerModuleContext context, CancellationToken cancellationToken) => await Respond(async () =>
        {
            if (!ModulePackageHostConfiguration.Identifier(request.ModuleId) || !Version(request.Version))
                return Problem("module-package-rollback-request-invalid", 400);
            var current = InstalledModuleStore.Read(context.ContentRootPath).SingleOrDefault(module => module.ModuleId == request.ModuleId);
            if (current is null) return Problem("module-package-installed-module-not-found", 404);
            var record = await InstalledModuleInstaller.RollbackAsync(context.ContentRootPath, request.ModuleId,
                request.Version, cancellationToken);
            var operation = ModulePackageOperationStore.Record(context.ContentRootPath, "rollback", record,
                current.Version, DateTimeOffset.UtcNow);
            return Results.Ok(new ServerModulePackageInstallResultDto(operation.OperationId, InstalledDto(record, record)));
        })).RequireAuthorization(ManagePolicy);
        return app;
    }

    private static bool Version(string? value) => System.Version.TryParse(value, out var parsed)
        && parsed.Build >= 0 && parsed.Revision == -1 && parsed.ToString(3) == value;

    private static ServerModulePackagePlanDto PlanDto(ModulePackagePlan plan) =>
        new(plan.PlanId, plan.SourceId, plan.ReleaseId, plan.ModuleId, plan.Version, plan.PublisherId,
            plan.ArtifactSha256, plan.ManifestSha256, plan.ExpiresAt, plan.AppliedOperationId);

    private static ServerInstalledModuleDto InstalledDto(InstalledModuleRecord module, InstalledModuleRecord? current) =>
        new(module.ModuleId, module.Version, module.ReleaseId, module.SourceId, module.PublisherId, module.ArtifactSha256,
            module.ManifestSha256, module.PackagePath, module.EntryAssembly, module.EntryType, module.Capabilities,
            module.Requires, module.InstalledAt,
            current is not null && current.ModuleId == module.ModuleId && current.Version == module.Version
                && current.ArtifactSha256 == module.ArtifactSha256,
            module.Quarantined);

    private static ServerPackageOperationDto OperationDto(ModulePackageOperation operation) =>
        new(operation.OperationId, operation.Action, operation.ModuleId, operation.Version, operation.ReleaseId,
            operation.ArtifactSha256, operation.PreviousVersion, operation.State, operation.ProblemCode,
            operation.CreatedAt, operation.UpdatedAt, operation.ProcessGeneration);

    internal static bool CanManage(ClaimsPrincipal user, IConfiguration configuration)
    {
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(subject, out var id) && configuration.GetSection("ModuleManagement:Administrators")
            .GetChildren().Any(entry => Guid.TryParse(entry.Value, out var administrator) && administrator == id);
    }
    internal static ServerModuleDto ForActor(ServerModuleDto module, ClaimsPrincipal actor, IConfiguration configuration) => CanManage(actor, configuration) ? module :
        module with { AllowedActions = Enum.GetValues<ServerModuleAction>().Select(action =>
            new ServerModuleActionAdmission(action, false, [new("module-forbidden")])).ToArray() };
    private static string Actor(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub") ?? throw new ModuleManagementException("module-forbidden", 403);
    private static async Task<IResult> Respond(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ModuleManagementException exception) { return Problem(exception.Code, exception.Status); }
        catch (ModulePackageException exception) { return Problem(exception.ProblemCode,
            exception.ProblemCode is "module-package-source-invalid" ? 400
                : exception.ProblemCode is "module-package-source-unconfigured" or "module-package-source-not-accepted" ? 404 : 503); }
        catch (Exception exception) when (exception is IOException or System.UnauthorizedAccessException)
        { return Problem("module-state-unavailable", 503); }
    }
    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status,
        type: "https://relaxkonos.app/problems/" + code, title: code,
        extensions: new Dictionary<string, object?> { ["problemCode"] = code });
}
