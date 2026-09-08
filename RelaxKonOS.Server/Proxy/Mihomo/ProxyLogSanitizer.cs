using System.Text.RegularExpressions;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Defence in depth for controller, service, and diagnostic logs. Never use it to make secret DTOs safe.</summary>
public static partial class ProxyLogSanitizer
{
    private const string Redacted = "[REDACTED]";

    public static string Sanitize(string? value, int maximumLength)
    {
        var text = value ?? string.Empty;
        text = Authorization().Replace(text, "$1" + Redacted);
        text = SecretField().Replace(text, "$1" + Redacted);
        text = UrlUserInfo().Replace(text, "${1}" + Redacted + "@");
        text = ControlCharacters().Replace(text, " ");
        return text.Length <= maximumLength ? text : text[..maximumLength] + "…";
    }

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*(?:bearer\\s+)?)\\S+")]
    private static partial Regex Authorization();
    [GeneratedRegex("(?i)((?:secret|token|password|uuid|private[-_ ]?key)\\s*[:=]\\s*)[^\\s,;]+")]
    private static partial Regex SecretField();
    [GeneratedRegex("(?i)([a-z][a-z0-9+.-]*://[^/\\s:@]+:)[^@/\\s]+@")]
    private static partial Regex UrlUserInfo();
    [GeneratedRegex("[\\x00-\\x1f\\x7f]+")]
    private static partial Regex ControlCharacters();
}
