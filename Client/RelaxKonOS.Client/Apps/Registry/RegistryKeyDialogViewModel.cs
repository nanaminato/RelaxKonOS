using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Client.Apps.Registry;

public sealed partial class RegistryKeyDialogViewModel : ObservableObject
{
    private readonly IRegistryClient _client;
    private readonly Action<bool> _close;
    private readonly Action<RegistryKeyDto> _saved;
    public RegistryScope Scope { get; }
    public string ParentPath { get; }
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _error;

    public RegistryKeyDialogViewModel(RegistryScope scope, string parentPath, IRegistryClient client, Action<bool> close, Action<RegistryKeyDto> saved)
    { Scope = scope; ParentPath = parentPath; _client = client; _close = close; _saved = saved; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var name = Name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/', ':']) >= 0) { Error = "Enter a valid key name."; return; }
        try
        {
            var saved = await _client.CreateKeyAsync(new CreateRegistryKeyRequest(Scope, ParentPath + "\\" + name));
            _saved(saved);
            _close(true);
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand] private void Cancel() => _close(false);
}
