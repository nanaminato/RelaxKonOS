using System.Text.Json;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

internal static class TailscalePackageInterlock
{
    internal const string PackageRoot = "/var/lib/relaxkonos-component-packages";
    internal static bool HasPending(string packageRoot, Action<string> protect)
    {
        try
        {
            var directory = Path.Combine(packageRoot, "tailscale");
            if (Directory.Exists(directory)) protect(directory);
            foreach (var name in new[] { "operation.json", "change.json" })
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                protect(path);
                if (new FileInfo(path).Length > 262144) return true;
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                TailscaleNativeControl.Canonical(document.RootElement);
                if (!document.RootElement.TryGetProperty("Committed", out var committed) || committed.ValueKind != JsonValueKind.True) return true;
                var record = document.RootElement;
                if (!record.GetProperty("OperationId").TryGetGuid(out var id) || id == Guid.Empty) return true;
                if (name == "operation.json")
                {
                    if (record.GetProperty("Target").Deserialize<ComponentPackageTarget>() is not { PackageName: "tailscale" } target
                        || !AptPackageResolution.IsUsableExpectation(target)
                        || record.GetProperty("ComponentId").GetString() != "tailscale"
                        || record.GetProperty("ExecutableSha256").GetString() is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)) return true;
                    continue;
                }
                // An uninstall freezes only the verified installed identity; it intentionally has no
                // repository candidate or old download URL to compare.
                if (record.GetProperty("Change").Deserialize<ComponentPackageChange>() is not { } change
                    || change.Change is not (RuntimeChange.Update or RuntimeChange.Uninstall)
                    || change.InstalledPackage is not { } installed
                    || !PackageIdentityPolicy.Valid(installed) || installed.PackageName != "tailscale") return true;
                if (change.Change == RuntimeChange.Update)
                {
                    if (record.GetProperty("Target").Deserialize<ComponentPackageTarget>() is not { PackageName: "tailscale" } target
                        || !AptPackageResolution.IsUsableExpectation(target)) return true;
                }
                else if (record.GetProperty("Target").ValueKind != JsonValueKind.Null) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { return true; }
    }
}
