using System.Diagnostics.CodeAnalysis;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Value rules for the Docker proxy setting. The Linux Helper re-validates everything it receives,
/// so these rules exist to reject bad input early with a stable problem code rather than to be the
/// only line of defence.
/// </summary>
internal static class DockerProxyValidation
{
    internal const int MaximumProxyValueLength = 512;
    internal const int MaximumBypassLength = 1024;

    /// <summary>A proxy URL is an absolute HTTP(S) URL with a host and no query or fragment.</summary>
    internal static bool IsValidProxyUrl(string value) => value.Length is > 0 and <= MaximumProxyValueLength
        && value.All(IsSafeCharacter)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && !string.IsNullOrWhiteSpace(uri.Host)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    /// <summary>A bypass list is a comma-separated set of host, domain, or CIDR tokens.</summary>
    internal static bool IsValidBypassList(string value)
    {
        if (value.Length > MaximumBypassLength) return false;
        if (value.Length == 0) return true;
        return value.Split(',').All(token => token.Length is > 0 and <= 255
            && token.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_' or ':' or '*' or '/' or '[' or ']'));
    }

    /// <summary>
    /// Rejects quotes, backslashes, control characters, and non-ASCII. A value that reaches the
    /// host would otherwise be able to close the systemd <c>Environment=</c> quoting or add a
    /// second directive.
    /// </summary>
    internal static bool IsSafeCharacter(char character) => character is >= '!' and <= '~' && character is not ('"' or '\\');

    internal static bool TryNormalize(string? raw, [NotNullWhen(true)] out string value)
    {
        value = raw?.Trim() ?? string.Empty;
        return true;
    }

    /// <summary>
    /// Hides the user information of a proxy URL. The operator who owns the setting reads the full
    /// value back in the Docker Manager, because a masked echo would be written back as the mask on
    /// the next save. This exists for the audiences that are not the operator: log lines, audit
    /// records, problem details, and layer diagnostics.
    /// </summary>
    internal static string MaskProxy(string value)
    {
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return value;
        var authorityStart = schemeEnd + 3;
        var authorityEnd = value.IndexOf('/', authorityStart);
        if (authorityEnd < 0) authorityEnd = value.Length;
        var authority = value.AsSpan(authorityStart, authorityEnd - authorityStart);
        var at = authority.LastIndexOf('@');
        return at < 0 ? value : string.Concat(value.AsSpan(0, authorityStart), "***", value.AsSpan(authorityStart + at));
    }
}
