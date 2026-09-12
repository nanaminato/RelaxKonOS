using System.Globalization;

namespace RelaxKonOS.Client.Localization;

/// <summary>
/// A UI string that re-resolves against the current display language every time it is read.
///
/// A view model must not cache the <em>resolved</em> text of a resource, because the language can
/// change while a window stays open. Storing a <see cref="LocalizedStatus"/> keeps the stable
/// resource key (plus any format arguments) and resolves it on demand, so a property backed by one
/// automatically follows a language switch. Literal text produced by the application
/// (for example an exception message) is preserved as-is through the implicit conversion from
/// <see cref="string"/>.
///
/// Equality is intentionally left to reference semantics: two holders for the same key are still
/// treated as different values, so a binding framework that compares old and new values always
/// re-renders the property after a language change instead of skipping an "unchanged" value.
/// </summary>
public readonly struct LocalizedStatus
{
    private readonly string? _key;
    private readonly object?[]? _arguments;
    private readonly string? _literal;

    private LocalizedStatus(string? key, object?[]? arguments, string? literal)
    {
        _key = key;
        _arguments = arguments;
        _literal = literal;
    }

    /// <summary>A resource key resolved live, with an optional English source fallback.</summary>
    public static LocalizedStatus Key(string key, string? englishFallback = null, params object?[] arguments) =>
        new(key, arguments.Length == 0 ? null : arguments, englishFallback);

    /// <summary>A resource key with format arguments, resolved live.</summary>
    public static LocalizedStatus Format(string key, params object?[] arguments) => new(key, arguments, null);

    /// <summary>Literal, non-localized text such as a server message.</summary>
    public static LocalizedStatus Literal(string? value) => new(null, null, value ?? string.Empty);

    /// <summary>
    /// Combines several values into one that re-resolves as a group. Use this instead of joining
    /// already-resolved strings when the result is stored, so a later language switch still applies.
    /// </summary>
    public static LocalizedStatus Join(string separator, IEnumerable<LocalizedStatus> values) =>
        new(null, [separator, values.ToArray()], null);

    /// <summary>True when the value carries no text.</summary>
    public bool IsEmpty => _key is null && string.IsNullOrEmpty(_literal);

    /// <summary>Resolves the text for the current display language.</summary>
    public string Resolve()
    {
        if (_key is null)
        {
            // Join carries a separator followed by the pieces; anything else is plain literal text.
            if (_arguments is [string separator, LocalizedStatus[] pieces])
                return string.Join(separator, pieces.Select(piece => piece.Resolve()));
            return _literal ?? string.Empty;
        }

        // _literal carries the English source fallback supplied by the caller; when it is absent the
        // key itself is used, which surfaces an obvious marker rather than silently leaving a gap.
        var template = LocalizedText.Get(_key, _literal ?? _key);
        return _arguments is { Length: > 0 }
            ? string.Format(CultureInfo.CurrentCulture, template, _arguments)
            : template;
    }

    /// <summary>A raw string is treated as literal text that must not be re-localized.</summary>
    public static implicit operator LocalizedStatus(string? value) => Literal(value);

    /// <summary>Reading the value resolves it for the current display language.</summary>
    public static implicit operator string(LocalizedStatus value) => value.Resolve();

    public override string ToString() => Resolve();
}
