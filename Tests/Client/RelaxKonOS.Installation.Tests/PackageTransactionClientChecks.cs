using RelaxKonOS.AppSDK;
using InstallationApiException = RelaxKonOS.AppSDK.InstallationApiException;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class PackageTransactionClientChecks
{
    public static async Task RunAsync(IAuthSession session, IAppSettingsClient settings)
    {
        var backend = DispatchProxy.Create<IAppSettingsClient, InstallationSettingsFixture>();
        var fixture = (InstallationSettingsFixture)(object)backend;
        var databaseSettings = new ExternalAppSettings(new("relaxkonos.database"), backend);
        var operationId = Guid.NewGuid();
        var previous = await databaseSettings.GetAsync(key: "mysql-installation");
        await databaseSettings.SetAsync(JsonSerializer.SerializeToElement(new { operationId }),
            key: "mysql-installation", expectedRevision: previous?.Revision);
        Check(fixture.ReadAddress == ("relaxkonos.database", AppSettingsScope.Workspace, "mysql-installation")
            && fixture.WriteAddress == fixture.ReadAddress && fixture.ExpectedRevision == 7
            && fixture.Value.GetProperty("operationId").GetGuid() == operationId,
            "Database operation settings preserve workspace identity, document shape, and revision checks");

        var failed = new InstallationOperationDto(Guid.Empty, InstallationServiceId.Nginx,
            InstallationOperationKind.Install, InstallationOperationState.Failed, InstallationStage.Failed,
            null, "installation.connection_unavailable", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, false);
        using var restoreHttp = new HttpClient(new Handler(request => request.RequestUri?.AbsolutePath ==
            InstallationApiRoutes.Operation(failed.OperationId)
            ? new(HttpStatusCode.OK) { Content = JsonContent.Create(failed) }
            : new(HttpStatusCode.NotFound)));
        var restoreClient = new InstallationClient(restoreHttp, session);
        using var retained = new InstallationTaskViewModel(DesktopInstallationText.Instance, restoreClient, databaseSettings,
            failed.Service, retainFailedReceipt: true, () => Task.CompletedTask, () => Task.FromResult(false));
        using var dismissed = new InstallationTaskViewModel(DesktopInstallationText.Instance, restoreClient, databaseSettings,
            failed.Service, retainFailedReceipt: false, () => Task.CompletedTask, () => Task.FromResult(false));
        await retained.RestoreAsync();
        await dismissed.RestoreAsync();
        Check(retained.Operation?.OperationId == failed.OperationId && dismissed.Operation is null,
            "Failed installation receipts are retained only when the caller requests them");

        var package = new ComponentPackageTarget("nginx", "1.26.3-1", "amd64", "repo", "https://repo.example/",
            "stable", "main", "https://repo.example/nginx.deb", new('a', 64), 1024, [new("acl.deb", "https://repo.example/acl.deb", 100, new('b', 64))]);
        var plan = new ComponentPlanRequest("nginx", "host", RuntimeChange.Install, 1,
            ComponentSourceMode.SystemPackage, null, 0, 0, null, package.Version, true, "original-frozen-key");
        var options = new NginxInstallationRequest(true, FrozenPackage: new("nginx", plan, ComponentFrozenTarget.FromPackage(package)));
        var keys = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath ==
                InstallationApiRoutes.Start(InstallationServiceId.Nginx, InstallationOperationKind.Install),
                "Frozen package submits through the authorized installation ledger route");
            keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            var body = request.Content!.ReadFromJsonAsync<NginxInstallationRequest>().GetAwaiter().GetResult();
            Check(body?.FrozenPackage?.Expected?.Package?.Sha256 == package.Sha256
                && body.FrozenPackage.Request == plan, "The exact displayed target and frozen revisions reach the ledger");
            return new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { problemCode = "installation.connection_unavailable" }) };
        }));
        using var task = new InstallationTaskViewModel(DesktopInstallationText.Instance, new InstallationClient(http, session), new ExternalAppSettings(new("relaxkonos.component-center"), settings),
            InstallationServiceId.Nginx, true, () => Task.CompletedTask, () => Task.FromResult(false));
        await task.SubmitAsync(InstallationOperationKind.Install, options);
        await task.SubmitAsync(InstallationOperationKind.Install, options);
        Check(keys.Count == 2 && keys.All(key => key == plan.IdempotencyKey),
            "Uncertain acceptance retries preserve the frozen key instead of creating a second install identity");
        Check(task.HasMessage && !task.IsActive, "Transport failure stays visible without inventing an active or successful installation");
        var bound = System.Reflection.DispatchProxy.Create<IAuthSession, PreviewSession>();
        var auth = (PreviewSession)(object)bound;
        using var blockedHttp = new HttpClient(new Handler(_ => throw new Exception("A stale installation window sent HTTP")));
        var boundClient = new InstallationClient(blockedHttp, bound);
        auth.Login = auth.Login with { };
        var blocked = false;
        try { await boundClient.StartAsync(InstallationServiceId.Nginx, InstallationOperationKind.Install, options, plan.IdempotencyKey, default); }
        catch (InstallationApiException ex) { blocked = ex.Status == HttpStatusCode.Unauthorized; }
        Check(blocked, "A changed login prevents an old installation window from sending a mutation");
        boundClient = new InstallationClient(blockedHttp, bound);
        auth.BaseUrl = "https://other-host.example/";
        blocked = false;
        try { await boundClient.GetAsync(Guid.NewGuid(), default); }
        catch (InstallationApiException ex) { blocked = ex.Status == HttpStatusCode.Unauthorized; }
        Check(blocked, "Installation observation remains bound to its original host");

    }
    public static async Task RunAllComponentsAsync(IAuthSession session, IAppSettingsClient settings)
    {
        foreach (var (id, service) in new[] { ("squid", InstallationServiceId.Squid), ("samba", InstallationServiceId.Smb),
            ("sing-box", InstallationServiceId.SingBox), ("mihomo", InstallationServiceId.Mihomo),
            ("docker", InstallationServiceId.Docker), ("tailscale", InstallationServiceId.Tailscale), ("frpc", InstallationServiceId.Frp), ("frps", InstallationServiceId.Frp) })
        {
            var bundle = id is "frpc" or "frps";
            var plan = new ComponentPlanRequest(id, "host", RuntimeChange.Install, 1,
                bundle ? ComponentSourceMode.ManagedArchive : ComponentSourceMode.SystemPackage,
                bundle ? "host-pins" : null, bundle ? 3 : 0, 0, bundle ? "release" : null,
                bundle ? "v0.71.0" : null, true, "frozen-" + id);
            var frozen = bundle ? ComponentFrozenTarget.FromPin(new("release", "v0.71.0", "win-x64",
                "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_windows_amd64.zip", new('a', 64),
                "zip", 1024, ["frpc", "frps"])) : ComponentFrozenTarget.FromPackage(new(id, "1.2.3", "amd64",
                "repo|stable|main|amd64", "https://repo.example", "stable", "main", "https://repo.example/package.deb", new('a', 64), 1024, []));
            var execution = new ComponentExecuteRequest(id, plan, frozen);
            object options = id switch { "sing-box" => new ManagedRuntimeInstallationRequest(true, FrozenPackage: execution),
                "mihomo" => new MihomoInstallationRequest(true, FrozenPackage: execution),
                "squid" => new SquidInstallationRequest(true, execution),
                "tailscale" => new TailscaleInstallationRequest(true, execution),
                "samba" => new SmbInstallationRequest(true, execution), "docker" => new DockerInstallationRequest(true, execution),
                _ => new FrpInstallationRequest(true, FrozenArchive: execution) };
            var seen = new List<string>();
            using var http = new HttpClient(new Handler(request => {
                Check(request.RequestUri!.AbsolutePath == InstallationApiRoutes.Start(service, InstallationOperationKind.Install),
                    id + " routes installation to its own authorized durable service");
                seen.Add(request.Headers.GetValues("Idempotency-Key").Single());
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var field = bundle ? "frozenArchive" : "frozenPackage";
                Check(body.RootElement.GetProperty(field).GetProperty("componentId").GetString() == id,
                    id + " serialized installation retains the selected component identity");
                return new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { problemCode = "installation.connection_unavailable" }) };
            }));
            using var task = new InstallationTaskViewModel(DesktopInstallationText.Instance, new InstallationClient(http, session), new ExternalAppSettings(new("relaxkonos.component-center"), settings), service,
                true, () => Task.CompletedTask, () => Task.FromResult(false), "installation." + id);
            await task.SubmitAsync(InstallationOperationKind.Install, options);
            await task.SubmitAsync(InstallationOperationKind.Install, options);
            Check(seen.Count == 2 && seen.All(key => key == plan.IdempotencyKey), id + " retry retains its frozen operation key");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}

public class InstallationSettingsFixture : DispatchProxy
{
    public (string, AppSettingsScope, string) ReadAddress { get; private set; }
    public (string, AppSettingsScope, string) WriteAddress { get; private set; }
    public long? ExpectedRevision { get; private set; }
    public JsonElement Value { get; private set; }

    protected override object Invoke(MethodInfo? method, object?[]? args)
    {
        var address = ((string)args![0]!, (AppSettingsScope)args[1]!, (string)args[2]!);
        switch (method?.Name)
        {
            case nameof(IAppSettingsClient.GetAsync):
                ReadAddress = address;
                return Task.FromResult<AppSettingsDocumentDto?>(new(address.Item2, address.Item3,
                    JsonSerializer.SerializeToElement(new { operationId = Guid.Empty }), 1, 7, DateTimeOffset.UtcNow));
            case nameof(IAppSettingsClient.SaveAsync):
                WriteAddress = address;
                Value = (JsonElement)args[3]!;
                ExpectedRevision = (long?)args[5];
                return Task.FromResult(new AppSettingsDocumentDto(address.Item2, address.Item3, Value, 1, 8, DateTimeOffset.UtcNow));
            default:
                throw new NotSupportedException(method?.Name);
        }
    }
}
