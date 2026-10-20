using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>
/// Closed vocabulary of host facts a managed runtime may require. A signed catalog never carries one:
/// requirements are derived by the Host adapter from the artifact it verified and the configuration it
/// is about to publish, so a publisher cannot widen what the Host probes or make an install depend on
/// a free-form command. Adding a member is a contract change, not a per-release declaration.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeDependencyKind>))]
public enum RuntimeDependencyKind { Platform, ServiceHost, DiskSpace, FileSystem, KernelFeature }

/// <summary>Unknown is deliberately distinct from Missing: both block, but only Missing states a wrong answer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeDependencyState>))]
public enum RuntimeDependencyState { Satisfied, Missing, Unknown }

/// <summary>
/// Identity is a capability name — <c>linux/amd64</c>, <c>systemd</c>, <c>protected-root</c>,
/// <c>ipv6</c> — never a path, command, package list, or URL. Only the byte-sized facts carry a minimum.
/// </summary>
public sealed record RuntimeDependencyRequirement(RuntimeDependencyKind Kind, string Identity, long? MinimumBytes = null);

/// <summary>
/// What the Host measured for one requirement. <paramref name="Measured"/> names the observed value when it
/// differs from the required identity, and the byte fields carry both sides of a size comparison, so a
/// refusal can say what the Host actually is instead of only that something failed.
/// </summary>
public sealed record RuntimeDependencyObservation(RuntimeDependencyKind Kind, string Identity,
    RuntimeDependencyState State, long? AvailableBytes = null, string? Measured = null, long? RequiredBytes = null);

/// <summary>
/// Host-measured answers, safe to render: capability names and byte counts only, never a path, command,
/// credential or configuration text.
/// </summary>
public sealed record RuntimeDependencyReport(IReadOnlyList<RuntimeDependencyObservation> Observations)
{
    public IReadOnlyList<RuntimeDependencyObservation> Unmet =>
        [.. Observations.Where(o => o.State != RuntimeDependencyState.Satisfied)];

    /// <summary>
    /// Exact coverage, not merely "nothing failed": one observation per requirement with the same identity.
    /// A report measured for a different release, or an incomplete report, never admits this one.
    /// </summary>
    public bool Satisfies(IReadOnlyList<RuntimeDependencyRequirement> requirements) =>
        Observations.Count == requirements.Count
        && Unmet.Count == 0
        && requirements.All(r => Observations.Count(o => o.Kind == r.Kind && o.Identity == r.Identity) == 1);
}

/// <summary>
/// The requirements one archive transaction derives from its own signed artifact plus the configuration it
/// will publish. Free space covers the download, the exact expanded layout, and a reserve for the atomic
/// switch that follows, so a nearly full disk fails before anything is downloaded.
/// </summary>
public static class RuntimeArchiveDependencies
{
    public const long ReservedBytes = 64L * 1024 * 1024;

    public static IReadOnlyList<RuntimeDependencyRequirement> For(RuntimeRelease release)
    {
        var target = release.Target;
        return
        [
            new(RuntimeDependencyKind.Platform, target.Os + "/" + target.Architecture),
            new(RuntimeDependencyKind.ServiceHost, target.ServiceHost),
            new(RuntimeDependencyKind.DiskSpace, "protected-root",
                release.Artifact.MaximumBytes + release.Artifact.Layout.MaximumExpandedBytes + ReservedBytes),
            new(RuntimeDependencyKind.FileSystem, "protected-root")
        ];
    }

    /// <summary>
    /// A listener bound to an IPv6 address cannot work on a Host with IPv6 disabled. Nothing else in the
    /// generated configuration depends on a kernel feature, so this list stays empty for IPv4-only setups.
    /// </summary>
    public static IReadOnlyList<RuntimeDependencyRequirement> ForConfiguration(ProxyServerConfiguration? configuration) =>
        configuration is not null && configuration.Inbounds.Any(inbound => inbound is not null
            && IPAddress.TryParse(inbound.ListenAddress, out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6)
            ? [new(RuntimeDependencyKind.KernelFeature, "ipv6")]
            : [];
}
