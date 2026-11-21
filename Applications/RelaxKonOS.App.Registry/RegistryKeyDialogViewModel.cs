using RelaxKonOS.AppSDK;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.App.Registry;

public sealed partial class RegistryKeyDialogViewModel : RegistryLocalizedObject
{
    private readonly IServerRegistry _client;
    private readonly Action<bool> _close;
    private readonly Action<RegistryKeyDto> _saved;
    public RegistryScope Scope { get; }
    public string ParentPath { get; }
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string? _error;

    public RegistryKeyDialogViewModel(RegistryScope scope, string parentPath, IServerRegistry client, Action<bool> close, Action<RegistryKeyDto> saved, ISystemLanguage? language = null) : base(language)
    { Scope = scope; ParentPath = parentPath; _client = client; _close = close; _saved = saved; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var name = Name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/', ':']) >= 0)
        { Error = Text.Get("registry.key.invalid", "Enter a valid key name."); return; }
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
