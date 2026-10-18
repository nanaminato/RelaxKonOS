using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Built-in Windows host for the one fixed sing-box component scope; never a remote service selector.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSingBoxArchiveHost(string protectedRoot) : ISingBoxArchiveHost
{
    private readonly string root = ValidRoot(protectedRoot);

    private static string ValidRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new RuntimePolicyException("runtime.service_scope_invalid");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (full == Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar))
            throw new RuntimePolicyException("runtime.service_scope_invalid");
        return full;
    }

    public Task<SingBoxServiceObservation> ObserveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var registration = WindowsComponentService.ReadOwnedRegistration(ComponentKind.SingBox, root);
            if (registration is null) return Task.FromResult(new SingBoxServiceObservation(false, true, false, false, null, null));
            var process = WindowsComponentService.ReadVerifiedProcess(ComponentKind.SingBox, root);
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + registration.Name);
            bool? boot = key?.GetValue("Start") switch { 2 => true, 3 or 4 => false, _ => null };
            return Task.FromResult(new SingBoxServiceObservation(true, true, process is not null, boot,
                registration.Executable, process?.AppliedIdentity));
        }
        catch (UnauthorizedAccessException) { return Task.FromResult(new SingBoxServiceObservation(true, false, false, null, null, null)); }
    }

    public Task StopAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return WindowsComponentService.StopAsync(ComponentKind.SingBox, root); }
    public Task SetBootAsync(bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return WindowsComponentService.SetStartupAsync(ComponentKind.SingBox, root, enabled);
    }
    public Task StartAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return WindowsComponentService.StartAsync(ComponentKind.SingBox, root); }
    public Task RemoveOwnedRegistrationAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return WindowsComponentService.RemoveAsync(ComponentKind.SingBox, root); }

    public async Task ConfigureAsync(string verifiedBinary, string protectedConfiguration, string configurationIdentity, bool bootEnabled, CancellationToken ct)
    {
        ValidatePaths(root, verifiedBinary, protectedConfiguration, configurationIdentity);
        ct.ThrowIfCancellationRequested();
        await using var input = File.OpenRead(protectedConfiguration);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(input, ct))
            .Equals(configurationIdentity, StringComparison.OrdinalIgnoreCase)) throw new RuntimePolicyException("runtime.configuration_drift");
        await WindowsComponentService.InstallAsync(ComponentKind.SingBox, root, verifiedBinary, protectedConfiguration,
            bootEnabled, appliedIdentity: configurationIdentity);
    }

    internal static void ValidatePaths(string root, string binary, string configuration, string identity)
    {
        ValidRoot(root);
        if (!Path.IsPathFullyQualified(binary) || !Path.IsPathFullyQualified(configuration)
            || identity.Length != 64 || !identity.All(Uri.IsHexDigit)
            || !string.Equals(Path.GetFullPath(configuration), Path.Combine(Path.GetFullPath(root), "config.json"), StringComparison.OrdinalIgnoreCase))
            throw new RuntimePolicyException("runtime.service_scope_invalid");
        var versions = Path.Combine(Path.GetFullPath(root), "versions");
        var relative = Path.GetRelativePath(versions, Path.GetFullPath(binary));
        var parts = relative.Split(Path.DirectorySeparatorChar);
        if (parts.Length < 2 || !Guid.TryParseExact(parts[0], "N", out _) || parts[0] == Guid.Empty.ToString("N")
            || !Protocol.ManagedRuntimes.RuntimeArchiveRules.SafePath(string.Join('/', parts.Skip(1)))
            || Path.GetFileName(binary) != "sing-box.exe") throw new RuntimePolicyException("runtime.service_scope_invalid");
        WindowsManagedRuntimePolicy.RequireNoLinks(binary);
        WindowsManagedRuntimePolicy.RequireNoLinks(configuration);
    }

    public Task<bool> ListenersHealthyAsync(ProxyServerConfiguration published, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var process = WindowsComponentService.ReadVerifiedProcess(ComponentKind.SingBox, root);
        if (process is null) return Task.FromResult(false);
        var endpoints = WindowsOwnedListeners.Read();
        var healthy = published.Inbounds.Where(i => i.Enabled).All(inbound => endpoints.Any(endpoint =>
            endpoint.ProcessId == process.ProcessId && endpoint.Udp == ProxyServerPresets.IsUdp(inbound.Protocol)
            && endpoint.Port == inbound.ListenPort && endpoint.Address.Equals(IPAddress.Parse(inbound.ListenAddress))));
        // Recheck creation time after reading tables so a replaced PID cannot satisfy the observation.
        var after = WindowsComponentService.ReadVerifiedProcess(ComponentKind.SingBox, root);
        return Task.FromResult(healthy && after?.ProcessId == process.ProcessId && after.StartedAt == process.StartedAt);
    }
}
