using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Client.Apps.Registry;

/// <summary>
/// Renames one custom value. The owner, key path and revision are captured at construction, so a
/// background selection or tree refresh can never retarget the write.
/// </summary>
public sealed partial class RegistryRenameDialogViewModel : LocalizedObservableObject
{
    private readonly RegistryEntryDto _entry;
    private readonly IRegistryClient _client;
    private readonly Action<bool> _close;
    private readonly Action<RegistryEntryDto> _renamed;

    [ObservableProperty] private string _newName;
    [ObservableProperty] private string? _error;

    public string TargetLabel => $"{_entry.Address.ServerId} → {_entry.OwnerUsername} ({_entry.Address.OwnerUserId:D}) → {_entry.Address.Scope} → {_entry.Path}";
    public string CurrentName => _entry.Name;
    public string ImpactText => LocalizedText.Get("registry.rename.impact", "Only the value name changes; the stored value and its owner stay the same.");

    public RegistryRenameDialogViewModel(RegistryEntryRow row, IRegistryClient client, Action<bool> close, Action<RegistryEntryDto> renamed)
    {
        _entry = row.Source; _client = client; _close = close; _renamed = renamed; _newName = _entry.Name;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = null;
        var name = NewName?.Trim() ?? "";
        if (name.Length == 0 || name.IndexOfAny(['\\', '/', ':']) >= 0)
        { Error = LocalizedText.Get("registry.rename.invalid", "Enter a valid value name."); return; }
        if (string.Equals(name, _entry.Name, StringComparison.Ordinal)) { _close(false); return; }
        try
        {
            var result = await _client.RenameAsync(new RenameRegistryEntryRequest(_entry.Scope, _entry.Path, _entry.Name, name, _entry.Revision));
            if (result.Address != _entry.Address) throw new InvalidOperationException("Registry response target mismatch.");
            _renamed(result);
            _close(true);
        }
        catch (RelaxKonOSAuthException ex) when (ex.Status == 409)
        { Error = LocalizedText.Get("registry.rename.conflict", "The value changed or the new name is already used in this key. Refresh and try again."); }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand] private void Cancel() => _close(false);
}
