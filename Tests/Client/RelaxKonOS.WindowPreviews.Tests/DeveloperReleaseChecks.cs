using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Protocol.Developer;
using RelaxKonOS.Runtime;
using System.Net;
using System.Text.Json;

internal static class DeveloperReleaseChecks
{
    public static void Run()
    {
        if (DeveloperProfile.IsDebug) throw new InvalidOperationException("Build this check in Release.");
        var scratch = Path.Combine(AppContext.BaseDirectory, "preview-qa", "developer-release", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("RELAXKONOS_DEV_DATA_HOME", scratch);
        Environment.SetEnvironmentVariable("RELAXKONOS_DEV_PROFILE", "ignored-in-release");
        Environment.SetEnvironmentVariable("RELAXKONOS_DEV_PORT", "47389");
        var productionDrive = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS", "SystemDrive");
        if (new VirtualSystemDrive().Root != productionDrive || DeveloperProfile.Port != DeveloperBridgeTarget.ReleasePort)
            throw new InvalidOperationException("Release resolved a Debug path or port.");

        using var services = new ServiceCollection().BuildServiceProvider();
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        var applications = new ApplicationManager(windows, services);
        var packages = new DeveloperPackageManager(applications, null!, windows, null!, new UriSchemeRoutingDiagnostics(),
            new VirtualSystemDrive(Path.Combine(scratch, "test-drive")));
        var mode = new DeveloperModeService(Path.Combine(scratch, "developer-mode.json"));
        mode.SetEnabled(true);
        using var bridge = new DeveloperBridgeService(mode, packages);
        if (!bridge.IsRunning) throw new InvalidOperationException("Could not bind the Release bridge port.");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(mode.Endpoint), Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.Add(DeveloperBridgeTarget.TokenHeader, mode.PairingToken);
        client.DefaultRequestHeaders.Add(DeveloperBridgeTarget.ProfileHeader, "ignored-in-release");
        using var forbidden = client.PostAsync("deploy", new ByteArrayContent([])).GetAwaiter().GetResult();
        using var result = JsonDocument.Parse(forbidden.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        if (forbidden.StatusCode != HttpStatusCode.Forbidden || result.RootElement.GetProperty("code").GetString() != "debug_required"
            || packages.Installed.Count != 0)
            throw new InvalidOperationException("Release accepted a Direct Deploy request.");

        client.DefaultRequestHeaders.Remove(DeveloperBridgeTarget.ProfileHeader);
        using var apps = client.GetAsync("apps").GetAwaiter().GetResult();
        apps.EnsureSuccessStatusCode();
        Console.WriteLine("Release VSD path, bridge port, authenticated Direct Deploy rejection, and legacy apps route passed.");
    }
}
