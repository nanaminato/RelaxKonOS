using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Firewall;

/// <summary>Routes for the Linux host firewall facade.</summary>
public static class FirewallApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;
    public const string Status = $"/{V1}/firewall/status";
    public const string Rules = $"/{V1}/firewall/rules";
    public const string Rule = $"/{V1}/firewall/rules/{{number}}";
    public const string Enabled = $"/{V1}/firewall/enabled";
    public const string Defaults = $"/{V1}/firewall/defaults";
}
