using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Tunnels;

internal static class DomainComponentObservationChecks
{
    public static async Task RunAsync()
    {
        var squid = new SquidStatus(true, true, false, false, false, "revision", true);
        var observed = SquidComponentObserver.FromStatus(squid, "linux");
        Check(observed.Found && observed.Management.Resolve() == Text("external"), "Squid preserves external ownership");
        Check(SquidComponentObserver.FromStatus(squid with { RecoveryRequired = true, Running = true }, "linux")
            .State.Resolve() == Text("recovery"), "Squid recovery overrides running status");
        Check(!SquidComponentObserver.FromStatus(squid with { Supported = false }, "windows").Found,
            "Unsupported Squid is not reported as installed");
        Check(!SquidComponentObserver.FromStatus(squid with { Installed = false }, "linux").Found,
            "Squid explicit absent status is preserved");
        Check(observed.Version.Resolve() == "—", "Squid never invents an executable version from configuration revision");
        var runtime = new TunnelRuntimeDto("frp", TunnelRuntimeMode.Managed, TunnelRuntimeState.Available,
            "0.61.0", "/runtime/frpc", IntegrityVerified: true);
        var frpc = TunnelComponentObserver.FromClient(runtime, "linux");
        Check(frpc.ComponentId == "frpc" && frpc.Found && frpc.Version.Resolve() == "0.61.0",
            "frpc has its own observed executable identity and version");
        Check(TunnelComponentObserver.FromClient(runtime with { Mode = TunnelRuntimeMode.External }, "linux")
            .Management.Resolve() == Text("external"), "External frpc is not upgraded to managed ownership");
        foreach (var state in new[] { TunnelRuntimeState.NotInstalled, TunnelRuntimeState.ExternalInvalid, TunnelRuntimeState.Unknown })
        {
            var unavailable = TunnelComponentObserver.FromClient(runtime with { State = state }, "linux");
            Check(!unavailable.Found && unavailable.Version.Resolve() == "—" && unavailable.State.Resolve() == Text("unknown"),
                "frpc invalid or missing executable does not display retained version: " + state);
        }
        var server = new ManagedFrpsConfigurationDto("127.0.0.1", 7000, [], null, null, true, true,
            false, "127.0.0.1", null, null, false, ManagedFrpsState.NotConfigured, 1, null,
            Token: "secret-token", DashboardPassword: "secret-password");
        foreach (var state in new[] { ManagedFrpsState.NotConfigured, ManagedFrpsState.Stopped, ManagedFrpsState.RuntimeUnavailable })
        {
            var frps = TunnelComponentObserver.FromServer(server with { State = state }, "windows");
            Check(!frps.Found && frps.Version.Resolve() == "—" && frps.State.Resolve() == Text("unknown"),
                "frps configuration state is not installation evidence: " + state);
        }
        var running = TunnelComponentObserver.FromServer(server with { State = ManagedFrpsState.Running }, "windows");
        Check(running.ComponentId == "frps" && running.Found && running.Management.Resolve() == Text("unknown")
            && !running.Note.Resolve().Contains("secret"), "frps running does not disclose secrets or invent executable ownership");
        Check(ComponentCatalog.Find("frpc") is not null && ComponentCatalog.Find("frps") is not null
            && ComponentCatalog.Find("frp") is null, "Component catalog has independent frpc and frps without a legacy alias");
        Check(await new SquidComponentObserver(null!).ObserveAsync("frpc", null, default) is null
            && await new TunnelComponentObserver(null!).ObserveAsync("squid", null, default) is null,
            "Domain observers do not query unrelated components");
    }
    private static string Text(string key) => LocalizedStatus.Key("component_center.domain." + key).Resolve();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
