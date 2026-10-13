using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.Client.Apps.Registry;

/// <summary>Materializes one level on expansion; unopened branches retain their original JSON.</summary>
public sealed partial class RegistryJsonNode : ObservableObject
{
    private readonly JsonNode? _value;
    private bool _loaded;
    public string Name { get; }
    public string Type { get; }
    public bool IsContainer => _value is JsonObject or JsonArray;
    public bool IsLeaf => !IsContainer;
    public ObservableCollection<RegistryJsonNode> Children { get; } = [];
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _valueText;

    public RegistryJsonNode(string name, JsonNode? value)
    {
        Name = name;
        _value = value;
        Type = value?.GetValueKind().ToString() ?? "Null";
        _valueText = value is JsonObject obj ? $"{{ {obj.Count} }}"
            : value is JsonArray array ? $"[ {array.Count} ]"
            : value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value?.ToJsonString() ?? "null";
        if (IsContainer) Children.Add(new RegistryJsonNode("…", null));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _loaded || !IsContainer) return;
        Children.Clear();
        if (_value is JsonObject obj)
            foreach (var property in obj) Children.Add(new RegistryJsonNode(property.Key, property.Value));
        else if (_value is JsonArray array)
            for (var index = 0; index < array.Count; index++) Children.Add(new RegistryJsonNode($"[{index}]", array[index]));
        _loaded = true;
    }

    public JsonNode? BuildValue()
    {
        if (IsContainer)
        {
            if (!_loaded) return _value?.DeepClone();
            if (_value is JsonObject)
            {
                var result = new JsonObject();
                foreach (var child in Children) result.Add(child.Name, child.BuildValue());
                return result;
            }
            return new JsonArray(Children.Select(child => child.BuildValue()).ToArray());
        }
        if (_value?.GetValueKind() == JsonValueKind.String) return JsonValue.Create(ValueText);
        var parsed = JsonNode.Parse(ValueText);
        var actual = parsed?.GetValueKind() ?? JsonValueKind.Null;
        var expected = _value?.GetValueKind() ?? JsonValueKind.Null;
        if (actual != expected && !(actual is JsonValueKind.True or JsonValueKind.False && expected is JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException($"{Name}: expected {Type}.");
        return parsed;
    }
}
