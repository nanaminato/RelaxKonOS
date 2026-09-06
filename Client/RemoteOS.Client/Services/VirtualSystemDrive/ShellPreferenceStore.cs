namespace Client.Services.VirtualSystemDrive;

/// <summary>Device-local materialization of the selected built-in Shell.</summary>
public sealed class ShellPreferenceStore(VirtualSystemDrive drive)
{
    private const string PreferencePath = "System/shell-preference.json";

    public async Task<string> LoadAsync()
    {
        try
        {
            var value = await drive.ReadJsonAsync<ShellPreference>(drive.ResolveRootChild(PreferencePath));
            return string.IsNullOrWhiteSpace(value.ShellId) ? "remoteos" : value.ShellId;
        }
        catch { return "remoteos"; }
    }

    public async Task SaveAsync(string shellId)
    {
        try { await drive.WriteJsonAtomicallyAsync(drive.ResolveRootChild(PreferencePath), new ShellPreference(shellId)); }
        catch { /* A preference write never breaks the current shell. */ }
    }

    private sealed record ShellPreference(string ShellId);
}
