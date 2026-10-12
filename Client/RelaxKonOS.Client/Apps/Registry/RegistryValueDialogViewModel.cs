using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Registry;

public sealed partial class RegistryValueDialogViewModel : ObservableObject
{
    private readonly IRegistryClient _client;
    private readonly Action<bool> _close;
    private readonly Action<RegistryEntryDto> _saved;
    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private string? _error;
    public RegistryEntryDto? Entry { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private RegistryValueType _valueType;
    public RegistryScope Scope { get; }
    public string Path { get; }
    public IReadOnlyList<RegistryValueType> ValueTypes { get; } = Enum.GetValues<RegistryValueType>();
    public bool IsJson => ValueType == RegistryValueType.Json;
    public bool IsBoolean => ValueType == RegistryValueType.Boolean;
    public bool IsEnum => Entry?.Schema.Editor == RegistryEditorKind.Enum;
    public bool IsNumber => ValueType == RegistryValueType.Number && !IsEnum;
    public bool IsColor => Entry?.Schema.Editor == RegistryEditorKind.Color;
    public bool IsText => !IsBoolean && !IsEnum && !IsNumber;
    public IReadOnlyList<RegistryValueChoiceDto> Choices => Entry?.Schema.Choices ?? [];
    [ObservableProperty] private RegistryValueChoiceDto? _selectedChoice;
    [ObservableProperty] private decimal? _numberValue;
    public decimal Minimum => (decimal)(Entry?.Schema.Minimum ?? -1e12);
    public decimal Maximum => (decimal)(Entry?.Schema.Maximum ?? 1e12);
    public string ApplyText => LocalizedText.Get(Entry?.ApplyMode == RegistryApplyMode.Immediate ? "registry.apply.immediate" : "registry.apply.reopen");
    public Avalonia.Media.IBrush ColorPreview => Avalonia.Media.Color.TryParse(ValueText, out var color)
        ? new Avalonia.Media.SolidColorBrush(color) : Avalonia.Media.Brushes.Transparent;
    partial void OnValueTextChanged(string value) => OnPropertyChanged(nameof(ColorPreview));
    public bool CanChangeIdentity => Entry is null;
    public bool CanReset => Entry?.Schema.DefaultValue is not null;
    public string TargetLabel => Entry is { } entry ? $"{entry.Address.ServerId} → {entry.OwnerUsername} ({entry.Address.OwnerUserId:D}) → {Path}" : Path;
    [ObservableProperty] private bool _booleanValue;
    partial void OnValueTypeChanged(RegistryValueType value)
    { OnPropertyChanged(nameof(IsJson)); OnPropertyChanged(nameof(IsBoolean)); OnPropertyChanged(nameof(IsText)); OnPropertyChanged(nameof(IsNumber)); }


    public RegistryValueDialogViewModel(RegistryEntryRow row, IRegistryClient client, Action<bool> close, Action<RegistryEntryDto> saved)
    {
        Entry = row.Source; _client = client; _close = close; _saved = saved; Scope = Entry.Scope; Path = Entry.Path; Name = Entry.Name; ValueType = Entry.ValueType;
        SetValue(Entry.DesiredValue);
    }
    public RegistryValueDialogViewModel(RegistryScope scope, string path, IRegistryClient client, Action<bool> close, Action<RegistryEntryDto> saved)
    { Scope = scope; Path = path; _client = client; _close = close; _saved = saved; Name = "NewValue"; ValueType = RegistryValueType.String; ValueText = ""; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            if (Entry is { } baseline && (Name != baseline.Name || ValueType != baseline.ValueType))
                throw new InvalidOperationException("The field name and type are fixed.");
            var value = IsEnum ? SelectedChoice?.Value ?? throw new InvalidOperationException("Choose a value.") : ValueType switch
            {
                RegistryValueType.String => JsonSerializer.SerializeToElement(ValueText),
                RegistryValueType.Boolean => JsonSerializer.SerializeToElement(BooleanValue),
                RegistryValueType.Number => JsonSerializer.SerializeToElement(NumberValue ?? throw new InvalidOperationException("Enter a number.")),
                _ => JsonSerializer.Deserialize<JsonElement>(ValueText),
            };
            var result = await _client.SaveAsync(new PutRegistryEntryRequest(Scope, Path, Name, ValueType, value,
                Entry is { } entry && entry.Name == Name ? entry.Revision : 0));
            if (Entry is { } expected && result.Address != expected.Address) throw new InvalidOperationException("Registry response target mismatch.");
            _saved(result);
            _close(true);
        }
        catch (RelaxKonOS.Client.Services.Auth.RelaxKonOSAuthException ex) when (ex.Status == 409)
        {
            try
            {
                var latest = (await _client.ListValuesAsync(Scope, Path)).SingleOrDefault(value => value.Name == Name);
                Error = string.Format(LocalizedText.Get("registry.error.conflict"), latest?.DesiredValue.ToString() ?? "");
            }
            catch { Error = LocalizedText.Get("registry.error.conflict_unknown"); }
        }
        catch (Exception ex) { Error = string.Format(LocalizedText.Get("registry.error.invalid_value", "Invalid registry value: {0}"), ex.Message); }
    }
    private void SetValue(JsonElement value)
    {
        ValueText = ValueType == RegistryValueType.String ? value.GetString() ?? "" : IsJson
            ? JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) : value.GetRawText();
        if (IsBoolean) BooleanValue = value.GetBoolean();
        if (IsNumber) NumberValue = value.GetDecimal();
        if (IsEnum) SelectedChoice = Choices.FirstOrDefault(choice => choice.Value.GetRawText() == value.GetRawText());
    }
    [RelayCommand] private void Reset()
    {
        if (Entry?.Schema.DefaultValue is { } value) SetValue(value);
    }
    [RelayCommand] private void Cancel() => _close(false);
}
