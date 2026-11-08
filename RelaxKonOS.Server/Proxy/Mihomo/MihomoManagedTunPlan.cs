using System.Globalization;
using System.Net;
using System.Net.Sockets;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Platform;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Freezes a descriptor-mode candidate against host-owned scope and observed management routing.
/// The caller holds the publication gate and supplies confirmed, protected settings and management routing.</summary>
internal sealed record MihomoManagedTunPlan(RuntimeConfigurationPlan Plan, MihomoPublishedRuntime Candidate, MihomoTunNetworkIntent Intent)
{
    internal static async Task<MihomoManagedTunPlan> FreezeAsync(Guid operation, RuntimeConfigurationPlan plan,
        MihomoPublishedRuntime draft, ProxyTunSettingsDto settings, ProxyManagementRouteSnapshot snapshot, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()
            || !uint.TryParse(ServerProcessIdentity.CurrentStableIdentity(), CultureInfo.InvariantCulture, out var uid))
            throw new RuntimePolicyException("runtime.network_platform_unsupported");
        if (settings is not { Stack: "gvisor", AutoRoute: false, StrictRoute: false, AutoDetectInterface: false, DnsHijack: "", Mtu: 1500 }
            || draft.NetworkTransitionRequired || MihomoConfigurationPolicy.Validate(draft).Material.NetworkTransitionRequired)
            throw new RuntimePolicyException("runtime.network_transaction_required");
        var scope = LinuxMihomoTunScope.Read();
        if (scope.Device != settings.DeviceName || scope.ServerUserId != uid)
            throw new RuntimePolicyException("runtime.network_scope_changed");
        if (!snapshot.ManagementPathSafe || snapshot.ManagementAddresses is not [var endpoint]
            || !IPAddress.TryParse(endpoint, out var management) || management.AddressFamily != AddressFamily.InterNetwork
            || !IPAddress.TryParse(snapshot.DefaultGateway, out var gateway) || gateway.AddressFamily != AddressFamily.InterNetwork
            || snapshot.LinuxRoutes is not { Count: > 0 } baseline
            || baseline.Any(route => route.Interface == settings.DeviceName
                || route.Source == scope.Address || route.Source == scope.Destination)
            || gateway.ToString() == scope.Address || gateway.ToString() == scope.Destination)
            throw new RuntimePolicyException("runtime.management_route_unknown");
        if (plan.NetworkIntentIdentity is not null || plan.CandidateIdentity != MihomoPublicationStore.Identity(draft.Yaml))
            throw new RuntimePolicyException("runtime.authorization_changed");

        var managementHash = await MihomoLinuxManagementBaseline.ReadAsync(management, uid, LinuxProxyManagementRoutes.RunIpAsync, ct);
        var current = await LinuxProxyManagementRoutes.ReadAsync(snapshot, ct);
        if (current is null || !baseline.SequenceEqual(current))
            throw new RuntimePolicyException("runtime.management_route_changed");
        var intent = new MihomoTunNetworkIntent(scope.Device, scope.Address, scope.Destination,
            management.ToString(), uid, managementHash, scope.DnsRoutingDomain);
        var candidate = MihomoConfigurationPolicy.Describe(MihomoManagedConfiguration.WithDescriptorTun(draft.Yaml, intent.Device)).Material;
        var frozen = plan with { CandidateIdentity = MihomoPublicationStore.Identity(candidate.Yaml) };
        frozen = frozen with { NetworkIntentIdentity = MihomoRuntimeMaterial.ConfigurationTunIntentIdentity(operation, frozen, intent) };
        MihomoRuntimeMaterial.RequireConfigurationTunIntent(operation, frozen, candidate, intent);
        return new(frozen, candidate, intent);
    }
}
