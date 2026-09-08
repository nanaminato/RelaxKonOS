using System.Text.RegularExpressions;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Reads only the ordered names from the managed YAML so controller map ordering never changes the proxy-group order in the UI.</summary>
internal static partial class MihomoProxyGroupOrder
{
    public static async Task<IReadOnlyDictionary<string, int>> ReadAsync(IProxyPlatformPaths paths, CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        if (!File.Exists(path)) return Empty;
        try
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            var withinProxyGroups = false;
            var needsName = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var isTopLevel = !char.IsWhiteSpace(line[0]);
                if (isTopLevel)
                {
                    withinProxyGroups = trimmed.StartsWith("proxy-groups:", StringComparison.OrdinalIgnoreCase);
                    needsName = false;
                    continue;
                }
                if (!withinProxyGroups) continue;
                if (trimmed.StartsWith('-'))
                {
                    needsName = true;
                    AddName(trimmed[1..], result, ref needsName);
                }
                else if (needsName) AddName(trimmed, result, ref needsName);
            }
            return result;
        }
        catch (IOException) { return Empty; }
        catch (UnauthorizedAccessException) { return Empty; }
    }

    private static void AddName(string value, IDictionary<string, int> result, ref bool needsName)
    {
        var match = NamePattern().Match(value);
        if (!match.Success) return;
        var name = match.Groups.Cast<Group>().Skip(1).FirstOrDefault(group => group.Success)?.Value.Trim() ?? string.Empty;
        if (name.Length == 0) return;
        if (!result.ContainsKey(name)) result.Add(name, result.Count);
        needsName = false;
    }

    // A group often uses flow mapping syntax, e.g. "- { name: 节点选择, type: select }".
    // Stop an unquoted scalar at the next flow-mapping delimiter instead of including the
    // remaining properties in the group name.
    [GeneratedRegex(@"(?:^|\s)name\s*:\s*(?:""([^""]*)""|'([^']*)'|([^,}\s#][^,}#]*?)(?=\s*[,}]|\s*$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    private static readonly IReadOnlyDictionary<string, int> Empty = new Dictionary<string, int>(StringComparer.Ordinal);
}
