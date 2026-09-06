namespace Client.Services.VirtualSystemDrive;

/// <summary>Device-local materialization of the selected built-in Shell.</summary>
public sealed class ShellPreferenceStore(VirtualSystemDrive drive)
{
    private const string PreferencePath = "System/shell-preference.json";

    public string Load()
    {
        try
        {
            var value = drive.ReadJsonAsync<ShellPreference>(drive.ResolveRootChild(PreferencePath)).GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(value.ShellId) ? "remoteos" : value.ShellId;
        }
        catch { return "remoteos"; }
    }

    public void Save(string shellId)
    {
        try { drive.WriteJsonAtomicallyAsync(drive.ResolveRootChild(PreferencePath), new ShellPreference(shellId)).GetAwaiter().GetResult(); }
        catch { /* A preference write never breaks the current shell. */ }
    }

    private sealed record ShellPreference(string ShellId);
}
