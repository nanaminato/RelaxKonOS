using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Firewall;

/// <summary>Owns one request-only Firewall facade. Borrowed Host ports and external OS rules are never disposed here.</summary>
public sealed class FirewallExecutionModule : IFirewallExecutionModule
{
    private IHostFirewallService? _firewall;
    public FirewallExecutionModule(FirewallExecutionContext context) => _firewall = context.Platform switch
    {
        HostPlatformKind.Linux => new LinuxUfwFirewallService(context.Host, context.Log),
        HostPlatformKind.Windows => new WindowsFirewallService(context.Host),
        _ => new UnavailableHostFirewallService(),
    };
    public IHostFirewallService Firewall => _firewall ?? throw new ObjectDisposedException(nameof(FirewallExecutionModule));
    public ValueTask DisposeAsync() { _firewall = null; return ValueTask.CompletedTask; }
}
