namespace RelaxKonOS.App.CodeEditor;

/// <summary>Resource names for the editor labels shared by the standalone and Git editors.</summary>
public static class CodeEditorLocalization
{
    public static IEnumerable<string> ResourceNames => typeof(CodeEditorLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("CodeEditor.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(CodeEditorLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing editor localization resource '{resourceName}'.");
}
