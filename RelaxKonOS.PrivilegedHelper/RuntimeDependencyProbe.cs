using System.Net.Sockets;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Read-only measurement of the facts a managed runtime may require. Every answer is one of three states:
/// measured and enough, measured and not enough, or not measurable at all. Nothing here executes a command,
/// writes a file, or reads a credential, so it is safe during a query and under the instance lock.
/// </summary>
internal interface IRuntimeDependencyEnvironment
{
    /// <summary>Exact <c>os/architecture</c> of this Host, or null when it is not a supported identity.</summary>
    string? Platform { get; }

    /// <summary>The service host this Helper actually drives, or null when it is absent.</summary>
    string? ServiceHost { get; }

    long? FreeBytes(string path);
    bool? PrivateDirectory(string path);
    bool? KernelFeature(string identity);
}

internal sealed class HostDependencyEnvironment : IRuntimeDependencyEnvironment
{
    public string? Platform => SingBoxManifest.PlatformName is { Length: > 0 } os && SingBoxManifest.ArchitectureName is { Length: > 0 } architecture
        ? os + "/" + architecture : null;

    // A distribution can carry systemd as a package without running it as PID 1; the runtime directory is
    // the honest signal, and a fixed service host never guesses at an init script.
    public string? ServiceHost => OperatingSystem.IsWindows() ? "scm"
        : OperatingSystem.IsLinux() ? Directory.Exists("/run/systemd/system") ? "systemd" : null
        : null;

    public long? FreeBytes(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                .MaxBy(d => d.RootDirectory.FullName.Length);
            return drive?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    public bool? PrivateDirectory(string path)
    {
        try
        {
            RuntimeSourceStore.RequireNoLinks(path);
            // A missing root is not a problem: the transaction creates it private before writing anything.
            return !File.Exists(path);
        }
        catch (RuntimePolicyException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    public bool? KernelFeature(string identity) => identity switch
    {
        "ipv6" => Ipv6(),
        _ => null
    };

    private static bool? Ipv6()
    {
        if (OperatingSystem.IsWindows()) return Socket.OSSupportsIPv6;
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var path = "/proc/sys/net/ipv6/conf/all/disable_ipv6";
            if (!File.Exists(path)) return false;
            return File.ReadAllText(path).Trim() switch { "0" => true, "1" => false, _ => null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Turns requirements into Host measurements. The report echoes each requirement's identity so a report
/// measured for one release can never satisfy another release's exact-coverage check.
/// </summary>
internal static class RuntimeDependencyProbe
{
    /// <summary>
    /// What the Host is, with no requirement to compare against: used by the read-only snapshot. Here a
    /// satisfied observation means "measured and present", because no release has asked for anything yet.
    /// </summary>
    internal static RuntimeDependencyReport Environment(IRuntimeDependencyEnvironment environment, string protectedRoot) =>
        new([
            new(RuntimeDependencyKind.Platform, environment.Platform ?? "unsupported",
                Verdict(environment.Platform is not null, environment.Platform), Measured: environment.Platform),
            new(RuntimeDependencyKind.ServiceHost, environment.ServiceHost ?? "unsupported",
                Verdict(environment.ServiceHost is not null, environment.ServiceHost), Measured: environment.ServiceHost),
            Size(environment.FreeBytes(protectedRoot)),
            Private(environment.PrivateDirectory(protectedRoot)),
            Feature(environment.KernelFeature("ipv6"), "ipv6")
        ]);

    /// <summary>One observation per requirement, in requirement order.</summary>
    internal static RuntimeDependencyReport For(IReadOnlyList<RuntimeDependencyRequirement> requirements,
        IRuntimeDependencyEnvironment environment, string protectedRoot) =>
        new([.. requirements.Select(requirement => Observe(requirement, environment, protectedRoot))]);

    private static RuntimeDependencyObservation Observe(RuntimeDependencyRequirement requirement,
        IRuntimeDependencyEnvironment environment, string protectedRoot) => requirement.Kind switch
    {
        RuntimeDependencyKind.Platform => new(requirement.Kind, requirement.Identity,
            Verdict(environment.Platform == requirement.Identity, environment.Platform), Measured: environment.Platform),
        RuntimeDependencyKind.ServiceHost => new(requirement.Kind, requirement.Identity,
            Verdict(environment.ServiceHost == requirement.Identity, environment.ServiceHost), Measured: environment.ServiceHost),
        RuntimeDependencyKind.DiskSpace => environment.FreeBytes(protectedRoot) is { } free
            ? new(requirement.Kind, requirement.Identity,
                free >= (requirement.MinimumBytes ?? 0) ? RuntimeDependencyState.Satisfied : RuntimeDependencyState.Missing,
                AvailableBytes: free, RequiredBytes: requirement.MinimumBytes)
            : new(requirement.Kind, requirement.Identity, RuntimeDependencyState.Unknown, RequiredBytes: requirement.MinimumBytes),
        RuntimeDependencyKind.FileSystem => environment.PrivateDirectory(protectedRoot) switch
        {
            true => new(requirement.Kind, requirement.Identity, RuntimeDependencyState.Satisfied),
            false => new(requirement.Kind, requirement.Identity, RuntimeDependencyState.Missing),
            null => new(requirement.Kind, requirement.Identity, RuntimeDependencyState.Unknown)
        },
        RuntimeDependencyKind.KernelFeature => Feature(environment.KernelFeature(requirement.Identity), requirement.Identity),
        _ => new(requirement.Kind, requirement.Identity, RuntimeDependencyState.Unknown)
    };

    private static RuntimeDependencyObservation Size(long? free) => free is { } bytes
        ? new(RuntimeDependencyKind.DiskSpace, "protected-root", RuntimeDependencyState.Satisfied, bytes)
        : new(RuntimeDependencyKind.DiskSpace, "protected-root", RuntimeDependencyState.Unknown);

    private static RuntimeDependencyObservation Private(bool? privateDirectory) => new(RuntimeDependencyKind.FileSystem,
        "protected-root", privateDirectory switch
        {
            true => RuntimeDependencyState.Satisfied,
            false => RuntimeDependencyState.Missing,
            null => RuntimeDependencyState.Unknown
        });

    private static RuntimeDependencyObservation Feature(bool? present, string identity) =>
        new(RuntimeDependencyKind.KernelFeature, identity, present switch
        {
            true => RuntimeDependencyState.Satisfied,
            false => RuntimeDependencyState.Missing,
            null => RuntimeDependencyState.Unknown
        });

    /// <summary>A capability either matches, is measurably something else, or was not measurable at all.</summary>
    private static RuntimeDependencyState Verdict(bool matches, string? measured) =>
        matches ? RuntimeDependencyState.Satisfied
            : measured is null ? RuntimeDependencyState.Unknown : RuntimeDependencyState.Missing;
}
