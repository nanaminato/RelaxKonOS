using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class PackageTransactionClientChecks
{
    public static async Task RunAsync(IAuthSession session, IAppSettingsClient settings)
    {
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
        using var task = new InstallationTaskViewModel(new InstallationClient(http, session), settings,
            InstallationServiceId.Nginx, "relaxkonos.component-center", () => Task.CompletedTask, () => Task.FromResult<string?>(null));
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
            object options = id switch { "squid" => new SquidInstallationRequest(true, execution),
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
            using var task = new InstallationTaskViewModel(new InstallationClient(http, session), settings, service,
                "relaxkonos.component-center", () => Task.CompletedTask, () => Task.FromResult<string?>(null), "installation." + id);
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
