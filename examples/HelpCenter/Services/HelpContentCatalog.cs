using System.Reflection;
using System.Text.Json;

namespace RelaxKonOS.Examples.HelpCenter.Services;

public sealed class HelpContentCatalog
{
    private readonly IReadOnlyDictionary<string, LocalizedHelpContent> _languages;

    private HelpContentCatalog(IReadOnlyDictionary<string, LocalizedHelpContent> languages) => _languages = languages;

    public IReadOnlyList<HelpLanguage> Languages => _languages.Values
        .Select(content => new HelpLanguage(content.Code, content.DisplayName))
        .OrderBy(language => language.Code, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static HelpContentCatalog Load()
    {
        var assembly = typeof(HelpContentCatalog).Assembly;
        var languages = new Dictionary<string, LocalizedHelpContent>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in new[] { "en", "zh-CN", "ja-JP" })
        {
            var index = ReadResource<HelpIndex>(assembly, $"content.{language}.index.json")
                ?? throw new InvalidOperationException($"Missing help index for {language}.");
            var documents = new Dictionary<string, HelpDocument>(StringComparer.OrdinalIgnoreCase);
            var routes = new Dictionary<string, HelpDocument>(StringComparer.OrdinalIgnoreCase);
            var tree = index.Nodes.Select(node => ToNode(assembly, language, node, documents, routes)).ToArray();
            languages.Add(language, new LocalizedHelpContent(language, index.DisplayName, tree, documents, routes));
        }
        return new HelpContentCatalog(languages);
    }

    public LocalizedHelpContent ResolveLanguage(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && _languages.TryGetValue(requested, out var exact))
            return exact;
        var neutral = requested?.Split('-', 2)[0];
        if (!string.IsNullOrWhiteSpace(neutral))
        {
            var neutralMatch = _languages.Values.FirstOrDefault(content => content.Code.Equals(neutral, StringComparison.OrdinalIgnoreCase));
            if (neutralMatch is not null) return neutralMatch;
        }
        return _languages["en"];
    }

    private static HelpTreeNode ToNode(Assembly assembly, string language, HelpIndexNode source,
        IDictionary<string, HelpDocument> documents, IDictionary<string, HelpDocument> routes)
    {
        HelpDocument? document = null;
        if (!string.IsNullOrWhiteSpace(source.Id) && !string.IsNullOrWhiteSpace(source.Route) && !string.IsNullOrWhiteSpace(source.File))
        {
            var markdown = ReadTextResource(assembly, $"content.{language}.{source.File.Replace('/', '.')}");
            document = new HelpDocument(source.Id, source.Route.Trim('/'), source.Title, markdown);
            documents.Add(document.Id, document);
            routes.Add(document.Route, document);
        }
        return new HelpTreeNode(source.Title, document, source.Children?.Select(child => ToNode(assembly, language, child, documents, routes)).ToArray()
            ?? Array.Empty<HelpTreeNode>());
    }

    private static T? ReadResource<T>(Assembly assembly, string suffix)
    {
        using var stream = OpenResource(assembly, suffix);
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static string ReadTextResource(Assembly assembly, string suffix)
    {
        using var stream = OpenResource(assembly, suffix);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Stream OpenResource(Assembly assembly, string suffix)
    {
        var prefix = $"{assembly.GetName().Name}.";
        var resourceName = $"{prefix}{suffix}";

        // MSBuild converts the hyphen in culture-like directory names (for example,
        // zh-CN) to an underscore in the manifest resource name. Resolve both forms so
        // the content layout can continue to use standard BCP-47 language tags.
        var normalizedResourceName = $"{prefix}{suffix.Replace('-', '_')}";
        var resolvedName = assembly.GetManifestResourceNames().FirstOrDefault(name =>
            name.Equals(resourceName, StringComparison.Ordinal)
            || name.Equals(normalizedResourceName, StringComparison.Ordinal));

        return resolvedName is not null
            ? assembly.GetManifestResourceStream(resolvedName)!
            : throw new InvalidOperationException($"Missing embedded help resource {suffix}.");
    }

    private sealed record HelpIndex(string DisplayName, IReadOnlyList<HelpIndexNode> Nodes);
    private sealed record HelpIndexNode(string Title, string? Id = null, string? Route = null, string? File = null,
        IReadOnlyList<HelpIndexNode>? Children = null);
}

public sealed record HelpLanguage(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record HelpDocument(string Id, string Route, string Title, string Markdown);
public sealed record HelpTreeNode(string Title, HelpDocument? Document, IReadOnlyList<HelpTreeNode> Children);

public sealed class LocalizedHelpContent(
    string code,
    string displayName,
    IReadOnlyList<HelpTreeNode> tree,
    IReadOnlyDictionary<string, HelpDocument> documents,
    IReadOnlyDictionary<string, HelpDocument> routes)
{
    public string Code { get; } = code;
    public string DisplayName { get; } = displayName;
    public IReadOnlyList<HelpTreeNode> Tree { get; } = tree;
    public IReadOnlyDictionary<string, HelpDocument> Documents { get; } = documents;
    public IReadOnlyDictionary<string, HelpDocument> Routes { get; } = routes;
}
