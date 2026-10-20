using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace RelaxKonOS.ManagedRuntimes;

/// <summary>One bounded YAML document, flattened merges and aliases, scalar map keys, no custom tags or cycles.</summary>
public static class MihomoYaml
{
    private const int MaximumNodes = 100_000;
    public const int MaximumBytes = 4 * 1024 * 1024;

    public static YamlMappingNode Parse(string yaml)
    {
        if (yaml.Length > MaximumBytes || Encoding.UTF8.GetByteCount(yaml) > MaximumBytes || yaml.Contains('\0')) Fail();
        if (string.IsNullOrWhiteSpace(yaml)) return new();
        try
        {
            var parser = new Parser(new StringReader(yaml));
            var anchors = new HashSet<string>(StringComparer.Ordinal);
            var open = new Stack<string?>();
            var events = 0;
            var documents = 0;
            while (parser.MoveNext())
            {
                if (++events > MaximumNodes || parser.Current is DocumentStart && ++documents > 1) Fail();
                if (parser.Current is NodeEvent node)
                {
                    if (!node.Tag.IsEmpty && node.Tag.Value is not ("tag:yaml.org,2002:str" or "tag:yaml.org,2002:int"
                        or "tag:yaml.org,2002:bool" or "tag:yaml.org,2002:float" or "tag:yaml.org,2002:null"
                        or "tag:yaml.org,2002:map" or "tag:yaml.org,2002:seq" or "tag:yaml.org,2002:merge")) Fail();
                    if (node is MappingStart && !node.Tag.IsEmpty && node.Tag.Value != "tag:yaml.org,2002:map"
                        || node is SequenceStart && !node.Tag.IsEmpty && node.Tag.Value != "tag:yaml.org,2002:seq") Fail();
                    if (!node.Anchor.IsEmpty && !anchors.Add(node.Anchor.Value)) Fail();
                }
                if (parser.Current is MappingStart or SequenceStart)
                {
                    var start = (NodeEvent)parser.Current;
                    open.Push(start.Anchor.IsEmpty ? null : start.Anchor.Value);
                    if (open.Count > 64) Fail();
                }
                else if (parser.Current is MappingEnd or SequenceEnd) open.Pop();
                else if (parser.Current is AnchorAlias alias && (!anchors.Contains(alias.Value.Value) || open.Contains(alias.Value.Value))) Fail();
            }
            var stream = new YamlStream(); stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 && documents == 0) return new();
            if (stream.Documents.Count != 1) Fail();
            if (IsNull(stream.Documents[0].RootNode)) return new();
            if (stream.Documents[0].RootNode is not YamlMappingNode) Fail();
            var budget = MaximumNodes;
            var bytes = MaximumBytes;
            var normalized = (YamlMappingNode)Clone(stream.Documents[0].RootNode, new(ReferenceEqualityComparer.Instance), 0, ref budget, ref bytes);
            normalized.Style = MappingStyle.Block;
            return normalized;
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException or InvalidOperationException)
        { throw new RuntimePolicyException("runtime.native_configuration_invalid"); }
    }

    public static string Render(YamlMappingNode root)
    {
        if (root.Children.Count == 0) return "";
        using var writer = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        var yaml = writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        // Only remove document delimiters emitted by our own writer, so Server-owned keys may be appended.
        if (yaml.StartsWith("---\n", StringComparison.Ordinal)) yaml = yaml[4..];
        if (yaml.EndsWith("...\n", StringComparison.Ordinal)) yaml = yaml[..^4];
        if (Encoding.UTF8.GetByteCount(yaml) > MaximumBytes) Fail();
        return yaml;
    }

    public static YamlNode? Get(YamlMappingNode map, string key)
    {
        var found = map.Children.Where(pair => pair.Key is YamlScalarNode scalar && scalar.Value!.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (found.Length > 1) Fail();
        var value = found.Length == 0 ? null : found[0].Value;
        return IsNull(value) ? null : value;
    }

    private static bool IsNull(YamlNode? value) => value is null || value is YamlScalarNode scalar && (scalar.Value is null
        || scalar.Style == ScalarStyle.Plain && (scalar.Value is "" or "~" || scalar.Value.Equals("null", StringComparison.OrdinalIgnoreCase)));

    public static string? Scalar(YamlNode? node)
    {
        if (node is null) return null;
        if (node is not YamlScalarNode scalar) { Fail(); return null; }
        return scalar.Value;
    }

    public static void Remove(YamlMappingNode map, IEnumerable<string> keys)
    {
        var names = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        foreach (var key in map.Children.Keys.Where(key => key is YamlScalarNode scalar && names.Contains(scalar.Value!)).ToArray())
            map.Children.Remove(key);
    }

    private static YamlNode Clone(YamlNode node, HashSet<YamlNode> active, int depth, ref int budget, ref int bytes)
    {
        if (--budget < 0 || depth > 64 || !active.Add(node)) Fail();
        try
        {
            if (node is YamlScalarNode scalar)
            {
                if ((bytes -= Encoding.UTF8.GetByteCount(scalar.Value ?? "")) < 0) Fail();
                return new YamlScalarNode(scalar.Value) { Style = scalar.Style, Tag = scalar.Tag };
            }
            if (node is YamlSequenceNode sequence)
            {
                var result = new YamlSequenceNode { Style = sequence.Style };
                foreach (var child in sequence.Children) result.Add(Clone(child, active, depth + 1, ref budget, ref bytes));
                return result;
            }
            if (node is not YamlMappingNode mapping) { Fail(); return null!; }
            var output = new YamlMappingNode { Style = mapping.Style };
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in mapping.Children.Where(pair => !IsMerge(pair.Key)))
            {
                if (pair.Key is not YamlScalarNode { Value: { Length: > 0 and <= 1024 } name } || !names.Add(name)) Fail();
                if ((bytes -= Encoding.UTF8.GetByteCount(Scalar(pair.Key)!)) < 0) Fail();
                var key = (YamlScalarNode)pair.Key;
                output.Add(new YamlScalarNode(key.Value) { Style = key.Style == ScalarStyle.Plain ? ScalarStyle.Plain : ScalarStyle.DoubleQuoted, Tag = key.Tag },
                    Clone(pair.Value, active, depth + 1, ref budget, ref bytes));
            }
            foreach (var pair in mapping.Children.Where(pair => IsMerge(pair.Key)))
            {
                var merged = Clone(pair.Value, active, depth + 1, ref budget, ref bytes);
                var sources = merged is YamlSequenceNode list ? list.Children : [merged];
                foreach (var source in sources)
                {
                    if (source is not YamlMappingNode values) { Fail(); continue; }
                    foreach (var entry in values.Children)
                        if (names.Add(Scalar(entry.Key)!)) output.Add(entry.Key, entry.Value);
                }
            }
            return output;
        }
        finally { active.Remove(node); }
    }

    private static bool IsMerge(YamlNode key) => key is YamlScalarNode { Value: "<<" } scalar
        && (scalar.Style == ScalarStyle.Plain || !scalar.Tag.IsEmpty && scalar.Tag.Value == "tag:yaml.org,2002:merge");

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail() => throw new RuntimePolicyException("runtime.native_configuration_invalid");
}
