namespace RelaxKonOS.Client.Apps.CodeEditor;

internal static class CodeEditorFileTypes
{
    public static IReadOnlyList<string> SupportedExtensions { get; } =
    [
        ".cs", ".csx", ".fs", ".fsx", ".vb", ".c", ".h", ".cpp", ".cxx", ".cc", ".hpp", ".hh", ".hxx",
        ".m", ".mm", ".java", ".kt", ".kts", ".scala", ".sc", ".groovy", ".gradle", ".go", ".rs", ".swift",
        ".dart", ".py", ".pyw", ".rb", ".php", ".phar", ".pl", ".pm", ".r", ".lua", ".jl", ".nim", ".zig",
        ".ex", ".exs", ".erl", ".hrl", ".clj", ".cljs", ".cljc", ".hs", ".lhs", ".elm", ".ml", ".mli",
        ".pas", ".pp", ".d", ".f", ".f90", ".f95", ".adb", ".ads", ".asm", ".s", ".sol", ".v", ".sv", ".svh",
        ".sql", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".astro", ".razor", ".cshtml", ".vbhtml",
        ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less", ".svg", ".sh", ".bash", ".zsh", ".fish", ".command",
        ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".dockerfile", ".cmake", ".make", ".csproj", ".fsproj", ".vbproj",
        ".sln", ".slnx", ".props", ".targets", ".xaml", ".axaml", ".json", ".jsonc", ".json5", ".yaml", ".yml", ".toml",
        ".ini", ".cfg", ".conf", ".config", ".properties", ".xml", ".xsd", ".xsl", ".xslt", ".md", ".markdown", ".mdx", ".rst",
        ".adoc", ".asciidoc", ".txt", ".log", ".diff", ".patch", ".gitconfig", ".editorconfig",
    ];

    public static IReadOnlyList<string> SupportedFileNames { get; } =
    [".gitignore", ".gitattributes", ".gitmodules", ".dockerignore", ".npmignore", ".editorconfig", ".env", ".env.local", ".env.development", ".env.production", "Dockerfile", "Makefile", "README", "LICENSE", "CHANGELOG", "CONTRIBUTING"];
}
