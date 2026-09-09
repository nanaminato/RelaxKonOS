namespace RelaxKonOS.Client.Services;

public sealed record SettingsSearchEntry(string SettingId, string Route, string Title, string Category,
    string Scope, string Reason, string SearchText);

/// <summary>Immutable local index. Queries never perform network I/O and include unavailable entries.</summary>
public sealed class SettingsSearchIndex(IEnumerable<SettingsSearchEntry> entries)
{
    private readonly SettingsSearchEntry[] _entries = entries.ToArray();

    public IReadOnlyList<SettingsSearchEntry> Search(string query)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return Array.Empty<SettingsSearchEntry>();
        return _entries.Where(entry => terms.All(term => entry.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(entry => entry.Title.StartsWith(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenBy(entry => entry.Category, StringComparer.CurrentCulture)
            .ThenBy(entry => entry.Title, StringComparer.CurrentCulture).ToArray();
    }
}
