namespace RelaxKonOS.Client.Services.VirtualSystemDrive;

/// <summary>Device-local last-known-good shell resolution. It is never uploaded as executable state.</summary>
public sealed class ShellPreferenceStore(VirtualSystemDrive drive)
{
    private const string PreferencePath = "System/shell-preference.json";

    public async Task<ShellPreference> LoadAsync()
    {
        try
        {
            var value = await drive.ReadJsonAsync<ShellPreference>(drive.ResolveRootChild(PreferencePath));
            return string.IsNullOrWhiteSpace(value.ShellId) ? new ShellPreference("remoteos.windows-like") : value;
        }
        catch { return new ShellPreference("remoteos.windows-like"); }
    }

    public async Task SaveAsync(string shellId, string? packageId = null, string? packageVersion = null, string? resolvedPackagePath = null)
    {
        try { await drive.WriteJsonAtomicallyAsync(drive.ResolveRootChild(PreferencePath), new ShellPreference(shellId, packageId, packageVersion, resolvedPackagePath)); }
        catch { /* A preference write never breaks the current shell. */ }
    }

    public sealed record ShellPreference(string ShellId, string? PackageId = null, string? PackageVersion = null,
        string? ResolvedPackagePath = null);
}
