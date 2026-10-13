using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Registry;

/// <summary>Schema-driven value editor. The target address is fixed at construction and never follows a background selection.</summary>
public sealed partial class RegistryValueDialogViewModel : LocalizedObservableObject
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

    /// <summary>Logical editor the server declared. A new custom value has no schema, so it starts as plain text.</summary>
    public RegistryEditorKind Editor => Entry?.Schema.Editor ?? RegistryEditorKind.String;
    public bool IsBoolean => ValueType == RegistryValueType.Boolean;
    public bool IsEnum => Editor == RegistryEditorKind.Enum;
    public bool IsStringList => Editor == RegistryEditorKind.StringList;
    public bool IsInteger => !IsEnum && Editor == RegistryEditorKind.Integer;
    public bool IsNumber => ValueType == RegistryValueType.Number && !IsEnum && !IsInteger;
    public bool IsColor => Editor == RegistryEditorKind.Color;
    public bool IsJson => !IsStringList && ValueType == RegistryValueType.Json;
    public bool IsText => IsJson ? IsRawJson : !IsBoolean && !IsEnum && !IsNumber && !IsInteger && !IsStringList && !IsColor;
    public bool IsStructuredJson => IsJson && !IsRawJson;
    [ObservableProperty] private bool _isRawJson;

    public ObservableCollection<RegistryJsonNode> JsonRoots { get; } = [];
    [ObservableProperty] private RegistryJsonNode? _selectedJsonNode;

    partial void OnIsRawJsonChanged(bool value)
    {
        try
        {
            if (value && JsonRoots.Count > 0)
                ValueText = JsonSerializer.Serialize(JsonRoots[0].BuildValue(), new JsonSerializerOptions { WriteIndented = true });
            else if (!value && IsJson)
            {
                var root = new RegistryJsonNode(Name, System.Text.Json.Nodes.JsonNode.Parse(ValueText)) { IsExpanded = true };
                JsonRoots.Clear();
                JsonRoots.Add(root);
                SelectedJsonNode = root;
            }
            Error = null;
        }
        catch (Exception ex)
        {
            _isRawJson = !value;
            OnPropertyChanged(nameof(IsRawJson));
            Error = string.Format(LocalizedText.Get("registry.error.invalid_value", "Invalid registry value: {0}"), ex.Message);
        }
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsStructuredJson));
    }

    public IReadOnlyList<RegistryValueChoiceDto> Choices => Entry?.Schema.Choices ?? [];
    [ObservableProperty] private RegistryValueChoiceDto? _selectedChoice;
    [ObservableProperty] private decimal? _numberValue;
    public decimal Minimum => (decimal)(Entry?.Schema.Minimum ?? -1e12);
    public decimal Maximum => (decimal)(Entry?.Schema.Maximum ?? 1e12);

    /// <summary>Integer values can be inspected in hexadecimal without ever sending a non-numeric payload.</summary>
    [ObservableProperty] private bool _isHex;
    [ObservableProperty] private string _hexText = "";
    partial void OnIsHexChanged(bool value)
    {
        if (value) HexText = NumberValue is { } number ? FormatHex((long)number) : "";
        else if (TryParseHex(HexText, out var parsed)) NumberValue = parsed;
    }
    partial void OnHexTextChanged(string value)
    {
        if (IsHex && TryParseHex(value, out var parsed)) NumberValue = parsed;
    }

    /// <summary>Structured string list: add or remove rows instead of hand-writing JSON.</summary>
    public ObservableCollection<RegistryListItem> ListItems { get; } = [];
    [ObservableProperty] private string _newListItem = "";
    public bool HasListItems => ListItems.Count > 0;

    /// <summary>A small, valid swatch set so a colour never needs to be typed or guessed.</summary>
    public IReadOnlyList<RegistryColorSwatch> Palette { get; } =
    [
        new("#000000"), new("#FFFFFF"), new("#808080"), new("#CCCCCC"), new("#E74856"),
        new("#16C60C"), new("#3B78FF"), new("#F9F1A5"), new("#00B7C3"), new("#B4009E"),
    ];

    public string ApplyText => LocalizedText.Get(Entry?.ApplyMode == RegistryApplyMode.Immediate ? "registry.apply.immediate" : "registry.apply.reopen");
    public Avalonia.Media.IBrush ColorPreview => Avalonia.Media.Color.TryParse(ValueText, out var color)
        ? new Avalonia.Media.SolidColorBrush(color) : Avalonia.Media.Brushes.Transparent;
    partial void OnValueTextChanged(string value) => OnPropertyChanged(nameof(ColorPreview));
    public bool CanChangeIdentity => Entry is null;
    public bool CanReset => Entry?.Schema.DefaultValue is not null;
    public string TargetLabel => Entry is { } entry
        ? $"{entry.Address.ServerId} → {entry.OwnerUsername} ({entry.Address.OwnerUserId:D}) → {entry.Address.Scope} → {Path}"
        : Path;
    [ObservableProperty] private bool _booleanValue;

    partial void OnValueTypeChanged(RegistryValueType value)
    {
        JsonRoots.Clear();
        SelectedJsonNode = null;
        if (IsJson)
        {
            ValueText = "{}";
            var root = new RegistryJsonNode(Name, new System.Text.Json.Nodes.JsonObject()) { IsExpanded = true };
            JsonRoots.Add(root);
            SelectedJsonNode = root;
        }
        OnPropertyChanged(nameof(IsStructuredJson));
        OnPropertyChanged(nameof(IsJson)); OnPropertyChanged(nameof(IsBoolean)); OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsNumber)); OnPropertyChanged(nameof(IsInteger));
    }

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
        Error = null;
        try
        {
            if (Entry is { } baseline && (Name != baseline.Name || ValueType != baseline.ValueType))
                throw new InvalidOperationException(LocalizedText.Get("registry.error.fixed_identity", "The field name and type are fixed."));
            if (IsColor && !Avalonia.Media.Color.TryParse(ValueText, out _))
                throw new InvalidOperationException(LocalizedText.Get("registry.error.invalid_color", "Enter a valid colour value."));
            if (IsHex && !TryParseHex(HexText, out _))
                throw new InvalidOperationException(LocalizedText.Get("registry.error.invalid_number", "Enter a valid number."));
            var value = IsEnum ? SelectedChoice?.Value ?? throw new InvalidOperationException(LocalizedText.Get("registry.error.choose_value", "Choose a value."))
                : IsStringList ? JsonSerializer.SerializeToElement(ListItems.Select(item => item.Value).ToArray())
                : IsInteger ? JsonSerializer.SerializeToElement(ParseInteger())
                : ValueType switch
                {
                    RegistryValueType.String => JsonSerializer.SerializeToElement(ValueText),
                    RegistryValueType.Boolean => JsonSerializer.SerializeToElement(BooleanValue),
                    RegistryValueType.Number => JsonSerializer.SerializeToElement(NumberValue ?? throw new InvalidOperationException(LocalizedText.Get("registry.error.invalid_number", "Enter a valid number."))),
                    _ => !IsRawJson && JsonRoots.Count > 0 ? JsonSerializer.SerializeToElement(JsonRoots[0].BuildValue()) : JsonSerializer.Deserialize<JsonElement>(ValueText),
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

    [RelayCommand]
    private void AddListItem()
    {
        var item = NewListItem?.Trim() ?? "";
        if (item.Length == 0) return;
        ListItems.Add(new RegistryListItem(item));
        NewListItem = "";
        OnPropertyChanged(nameof(HasListItems));
    }

    [RelayCommand]
    private void RemoveListItem(RegistryListItem? item)
    {
        if (item is null || !ListItems.Remove(item)) return;
        OnPropertyChanged(nameof(HasListItems));
    }

    [RelayCommand]
    private void PickColor(string? color)
    {
        if (!string.IsNullOrWhiteSpace(color)) ValueText = color;
    }

    private long ParseInteger() => NumberValue is { } number
        ? (long)number : throw new InvalidOperationException(LocalizedText.Get("registry.error.invalid_number", "Enter a valid number."));

    private static string FormatHex(long value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    private static bool TryParseHex(string? text, out long value)
    {
        value = 0;
        var trimmed = text?.Trim() ?? "";
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return trimmed.Length > 0 && long.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private void SetValue(JsonElement value)
    {
        JsonRoots.Clear();
        SelectedJsonNode = null;
        if (IsJson)
        {
            var root = new RegistryJsonNode(Name, System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())) { IsExpanded = true };
            JsonRoots.Add(root);
            SelectedJsonNode = root;
        }
        if (IsStringList)
        {
            ListItems.Clear();
            if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) ListItems.Add(new RegistryListItem(item.GetString() ?? ""));
            OnPropertyChanged(nameof(HasListItems));
        }
        ValueText = ValueType == RegistryValueType.String ? value.GetString() ?? "" : IsJson
            ? JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) : value.GetRawText();
        if (IsBoolean) BooleanValue = value.GetBoolean();
        if (IsNumber || IsInteger) NumberValue = value.GetDecimal();
        if (IsEnum) SelectedChoice = Choices.FirstOrDefault(choice => choice.Value.GetRawText() == value.GetRawText());
    }
    [RelayCommand] private void Reset()
    {
        if (Entry?.Schema.DefaultValue is { } value) SetValue(value);
    }
    [RelayCommand] private void Cancel() => _close(false);
}

/// <summary>One editable row of a structured string list.</summary>
public sealed partial class RegistryListItem : ObservableObject
{
    [ObservableProperty] private string _value;
    public RegistryListItem(string value) => _value = value;
}

/// <summary>One palette entry: the canonical colour text plus the brush rendered for it.</summary>
public sealed class RegistryColorSwatch(string value)
{
    public string Value { get; } = value;
    public Avalonia.Media.IBrush Brush { get; } = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(value));
}
