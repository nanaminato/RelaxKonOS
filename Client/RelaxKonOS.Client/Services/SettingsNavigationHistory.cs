namespace RelaxKonOS.Client.Services;

public sealed record SettingsNavigationLocation(string Route, string SearchQuery);

/// <summary>Back restores the actual search context, including searches within the same page.</summary>
public sealed class SettingsNavigationHistory
{
    private readonly Stack<SettingsNavigationLocation> _entries = new();
    public bool CanGoBack => _entries.Count > 0;
    public void Remember(string route, string searchQuery) => _entries.Push(new(route, searchQuery));
    public SettingsNavigationLocation Back() => _entries.Pop();
    public void Clear() => _entries.Clear();
}
