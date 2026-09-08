namespace RelaxKonOS.Protocol.Workspace;

/// <summary>Canonical text encodings offered by the built-in editors and stored in workspace preferences.</summary>
public static class TextEncodingPreferences
{
    public const string Default = "UTF-8";

    public static IReadOnlyList<string> Supported { get; } =
    [
        "UTF-8", "UTF-8 BOM", "UTF-16 LE", "UTF-16 BE", "UTF-32 LE", "UTF-32 BE",
        "ASCII", "ISO-8859-1", "Windows-1252", "GB18030", "GBK", "Big5", "Shift JIS", "EUC-KR",
    ];

    public static bool IsSupported(string? encoding) =>
        !string.IsNullOrWhiteSpace(encoding)
        && Supported.Contains(encoding, StringComparer.Ordinal);
}
