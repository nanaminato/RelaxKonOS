using System.Globalization;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.UI.SshFiles;

internal readonly struct SshStatus
{
    private readonly string? _key;
    private readonly object?[]? _arguments;
    private readonly string? _literal;

    private SshStatus(string? key, object?[]? arguments, string? literal)
    {
        _key = key;
        _arguments = arguments;
        _literal = literal;
    }

    /// <summary>A resource key resolved live, with an optional English source fallback.</summary>
    public static SshStatus Key(string key, string? englishFallback = null, params object?[] arguments) =>
        new(key, arguments.Length == 0 ? null : arguments, englishFallback);

    /// <summary>Literal, non-localized text such as a server message.</summary>
    public static SshStatus Literal(string? value) => new(null, null, value ?? string.Empty);

    /// <summary>
    /// Combines several values into one that re-resolves as a group. Use this instead of joining
    /// already-resolved strings when the result is stored, so a later language switch still applies.
    /// </summary>
    public static SshStatus Join(string separator, IEnumerable<SshStatus> values) =>
        new(null, [separator, values.ToArray()], null);

    /// <summary>Resolves the text for the current display language.</summary>
    public string Resolve(ISystemLanguage language)
    {
        if (_key is null)
        {
            // Join carries a separator followed by the pieces; anything else is plain literal text.
            if (_arguments is [string separator, SshStatus[] pieces])
                return string.Join(separator, pieces.Select(piece => piece.Resolve(language)));
            return _literal ?? string.Empty;
        }

        // _literal carries the English source fallback supplied by the caller; when it is absent the
        // key itself is used, which surfaces an obvious marker rather than silently leaving a gap.
        var template = SshFilesLocalization.Get(language, _key, _literal);
        return _arguments is { Length: > 0 }
            ? string.Format(CultureInfo.CurrentCulture, template, _arguments)
            : template;
    }

    /// <summary>A raw string is treated as literal text that must not be re-localized.</summary>
    public static implicit operator SshStatus(string? value) => Literal(value);
}
