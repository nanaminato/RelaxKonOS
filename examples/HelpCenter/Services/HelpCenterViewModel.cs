using System.ComponentModel;

namespace RelaxKonOS.Examples.HelpCenter.Services;

public sealed class HelpCenterViewModel : INotifyPropertyChanged
{
    private readonly HelpContentCatalog _catalog;
    private LocalizedHelpContent _content;
    private HelpDocument? _currentDocument;
    private string _status = string.Empty;

    public HelpCenterViewModel(HelpContentCatalog catalog, string systemLanguage)
    {
        _catalog = catalog;
        _content = catalog.ResolveLanguage(systemLanguage);
        _currentDocument = _content.Documents.Values.FirstOrDefault();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<HelpLanguage> Languages => _catalog.Languages;
    public IReadOnlyList<HelpTreeNode> Tree => _content.Tree;
    public HelpLanguage SelectedLanguage => new(_content.Code, _content.DisplayName);
    public HelpDocument? CurrentDocument => _currentDocument;
    public string Status => _status;

    public void SelectLanguage(string requestedLanguage)
    {
        var previousRoute = _currentDocument?.Route;
        _content = _catalog.ResolveLanguage(requestedLanguage);
        _currentDocument = previousRoute is not null && _content.Routes.TryGetValue(previousRoute, out var translated)
            ? translated
            : _content.Documents.Values.FirstOrDefault();
        _status = string.Empty;
        Notify(nameof(Tree));
        Notify(nameof(SelectedLanguage));
        Notify(nameof(CurrentDocument));
        Notify(nameof(Status));
    }

    public void Open(HelpDocument document)
    {
        _currentDocument = document;
        _status = string.Empty;
        Notify(nameof(CurrentDocument));
        Notify(nameof(Status));
    }

    public void Navigate(Uri uri)
    {
        var query = ParseQuery(uri.Query);
        if (query.TryGetValue("lang", out var language) || query.TryGetValue("language", out language))
            SelectLanguage(language);

        var route = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (_content.Routes.TryGetValue(route, out var document))
        {
            Open(document);
            return;
        }

        _status = $"The requested guide is unavailable: {route}";
        Notify(nameof(Status));
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.IndexOf('=');
            if (split < 1) continue;
            values[Uri.UnescapeDataString(part[..split])] = Uri.UnescapeDataString(part[(split + 1)..]);
        }
        return values;
    }

    private void Notify(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
