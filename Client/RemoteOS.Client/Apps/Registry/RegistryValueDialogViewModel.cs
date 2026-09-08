using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Registry;
using Client.Localization;

namespace Client.Apps.Registry;

public sealed partial class RegistryValueDialogViewModel : ObservableObject
{
    private readonly IRegistryClient _client;
    private readonly Action<bool> _close;
    private readonly Action<RegistryEntryDto> _saved;
    [ObservableProperty] private string _valueText;
    [ObservableProperty] private string? _error;
    public RegistryEntryDto? Entry { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private RegistryValueType _valueType;
    public RegistryScope Scope { get; }
    public string Path { get; }
    public IReadOnlyList<RegistryValueType> ValueTypes { get; } = Enum.GetValues<RegistryValueType>();
    public bool IsJson => ValueType == RegistryValueType.Json;

    public RegistryValueDialogViewModel(RegistryEntryRow row, IRegistryClient client, Action<bool> close, Action<RegistryEntryDto> saved)
    {
        Entry = row.Source; _client = client; _close = close; _saved = saved; Scope = Entry.Scope; Path = Entry.Path; Name = Entry.Name; ValueType = Entry.ValueType;
        ValueText = IsJson ? JsonSerializer.Serialize(Entry.DesiredValue, new JsonSerializerOptions { WriteIndented = true }) : Entry.DesiredValue.GetRawText();
    }
    public RegistryValueDialogViewModel(RegistryScope scope, string path, IRegistryClient client, Action<bool> close, Action<RegistryEntryDto> saved)
    { Scope = scope; Path = path; _client = client; _close = close; _saved = saved; Name = "NewValue"; ValueType = RegistryValueType.String; ValueText = "\"\""; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            using var value = JsonDocument.Parse(ValueText);
            var compact = JsonSerializer.Serialize(value.RootElement);
            using var compactDocument = JsonDocument.Parse(compact);
            var result = await _client.SaveAsync(new PutRegistryEntryRequest(Scope, Path, Name, ValueType, compactDocument.RootElement.Clone(),
                Entry is { } entry && entry.Name == Name ? entry.Revision : 0));
            _saved(result);
            _close(true);
        }
        catch (Exception ex) { Error = string.Format(LocalizedText.Get("registry.error.invalid_value", "Invalid registry value: {0}"), ex.Message); }
    }
    [RelayCommand] private void Cancel() => _close(false);
}
