using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Startup;

/// <summary>Core-owned private listener. Module identity is authenticated by the current process token;
/// user identity comes exclusively from an opaque context issued by the authenticated Server proxy.</summary>
internal sealed class CoreModulePrivilegeBroker(IHostEnvironment environment, IConfiguration configuration,
    ServerModules modules, ModuleManagementStore management, IServiceProvider services) : IHostedService
{
    private sealed record RequestContext(string ModuleId, ClaimsPrincipal User, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, RequestContext> _contexts = new(StringComparer.Ordinal);
    private WebApplication? _application;
    internal ModulePrivilegePlanStore Plans { get; private set; } = null!;
    internal string? Url { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Plans = new ModulePrivilegePlanStore(environment.ContentRootPath, Resolve);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8192);
        _application = builder.Build();
        _application.MapPost("/prepare", async (ModulePrivilegeRequest request, HttpContext http) => await Respond(async () =>
        {
            var hosts = services.GetRequiredService<ModuleHostSupervisor>();
            var moduleId = hosts.Authenticate(http.Request.Headers[ModuleHostProtocol.ServiceTokenHeader].ToString())
                ?? throw new ModulePrivilegeException("module.privilege_denied", 403);
            var key = http.Request.Headers[ModulePrivilegeProtocol.RequestContextHeader].ToString();
            if (!_contexts.TryGetValue(key, out var context) || context.ModuleId != moduleId || context.ExpiresAt <= DateTimeOffset.UtcNow
                || !hosts.CanAccess(moduleId, context.User, configuration))
                throw new ModulePrivilegeException("module.privilege_background_denied", 403);
            using var scope = services.CreateScope();
            if (!scope.ServiceProvider.GetRequiredService<HostElevationSessionState>().IsSessionValid(context.User))
                throw new ModulePrivilegeException("module.privilege_denied", 403);
            return Results.Ok(await Plans.PrepareAsync(moduleId, Session(context.User), request));
        }));
        _application.MapGet("/plans/{id:guid}", async (Guid id, HttpContext http) => await Respond(async () =>
        {
            var module = services.GetRequiredService<ModuleHostSupervisor>()
                .Authenticate(http.Request.Headers[ModuleHostProtocol.ServiceTokenHeader].ToString())
                ?? throw new ModulePrivilegeException("module.privilege_denied", 403);
            return Results.Ok(await Plans.GetForModuleAsync(id, module));
        }));
        await _application.StartAsync(cancellationToken);
        Url = _application.Urls.Single();
    }
    internal string IssueRequestContext(string moduleId, ClaimsPrincipal principal)
    {
        foreach (var item in _contexts.Where(p => p.Value.ExpiresAt <= DateTimeOffset.UtcNow)) _contexts.TryRemove(item.Key, out _);
        if (_contexts.Count >= 4096) throw new ModulePrivilegeException("module.privilege_capacity", 503);
        _ = Session(principal);
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _contexts[key] = new(moduleId, new ClaimsPrincipal(principal), DateTimeOffset.UtcNow.AddMinutes(2));
        return key;
    }
    internal void ReleaseRequestContext(string key) => _contexts.TryRemove(key, out _);
    internal static string Session(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var sid = principal.FindFirstValue("sid");
        var tokenId = principal.FindFirstValue("jti");
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(tokenId))
            throw new ModulePrivilegeException("module.privilege_denied", 403);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject + "\n" + sid + "\n" + tokenId)));
    }
    private ModulePrivilegeIdentity Resolve(string moduleId)
    {
        if (!modules.IsEnabled(moduleId) || management.Writable && !management.DesiredIds.Contains(moduleId))
            throw new ModulePrivilegeException("module.privilege_denied", 403);
        var installed = InstalledModuleStore.Read(environment.ContentRootPath).SingleOrDefault(m => m.ModuleId == moduleId);
        if (installed is null || installed.Quarantined || installed.Runtime != "module-host")
            throw new ModulePrivilegeException("module.privilege_denied", 403);
        var consent = ModuleTrustStore.Read(environment.ContentRootPath, installed);
        if (consent?.Trusted != true || configuration["ModulePrivileges:Modules:" + moduleId + ":Policy"] is not (null or "EveryTime"))
            throw new ModulePrivilegeException("module.privilege_denied", 403);
        InstalledModuleStore.VerifyPackage(environment.ContentRootPath, installed);
        var context = ModulePackageHostConfiguration.Context(management.ServiceId, configuration, environment);
        var policy = ModulePackageHostConfiguration.Source(configuration, installed.SourceId);
        var catalogs = ModulePackageCatalogStore.Read(environment.ContentRootPath, management.ServiceId);
        var catalog = ModulePackageCatalogStore.ReadCurrentCatalog(catalogs, policy, context, DateTimeOffset.UtcNow);
        ModulePackageCatalogStore.EnsureNotRevoked(catalogs, installed.ReleaseId, installed.ArtifactSha256);
        var release = catalog.Catalog.Releases.SingleOrDefault(r => r.Manifest.ReleaseId == installed.ReleaseId);
        if (release is null || release.ArchiveSha256 != installed.ArtifactSha256 || release.ManifestSha256 != installed.ManifestSha256)
            throw new ModulePrivilegeException("module.privilege_plan_changed");
        var allowed = configuration.GetSection("ModulePrivileges:Services").Get<string[]>() ?? [];
        return new(moduleId, installed.Version, installed.PublisherId, installed.ArtifactSha256, installed.ManifestSha256,
            ModulePrivilegePlanStore.Hash(new { consentRevision = consent.Revision, policyRevision = policy.Revision, catalogRevision = catalogs.Revision, managementRevision = management.Revision }),
            release.Manifest.Privileges, allowed.Order(StringComparer.Ordinal).ToArray());
    }
    internal static async Task<IResult> Respond(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ModulePrivilegeException error)
        {
            return Results.Problem(statusCode: error.Status, title: error.Code,
                extensions: new Dictionary<string, object?> { ["problemCode"] = error.Code });
        }
        catch (Exception error) when (error is ModulePackageException or IOException or UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: 409, title: "module.privilege_denied",
                extensions: new Dictionary<string, object?> { ["problemCode"] = "module.privilege_denied" });
        }
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Url = null;
        _contexts.Clear();
        if (_application is not null)
        {
            await _application.StopAsync(cancellationToken);
            await _application.DisposeAsync();
            _application = null;
        }
    }
}
