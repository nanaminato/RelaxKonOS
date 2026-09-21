using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RelaxKonOS.Server.Observability;

public interface IObservabilitySanitizer
{
    string ToReference(string? value);
    string SanitizeSummary(string? value, int maximumLength = 1024);
    SanitizedException SanitizeException(Exception exception);
}

public sealed record SanitizedException(string Type, string Summary);

/// <summary>Single boundary for data that did not originate in the event catalog.</summary>
public sealed partial class ObservabilitySanitizer : IObservabilitySanitizer
{
    private readonly byte[] _referenceKey;
    private static readonly HashSet<string> ExceptionTypes = new(StringComparer.Ordinal)
    {
        nameof(ArgumentException), nameof(InvalidOperationException), nameof(UnauthorizedAccessException),
        nameof(IOException), nameof(TimeoutException), nameof(OperationCanceledException), nameof(System.Security.SecurityException)
    };

    public ObservabilitySanitizer(ObservabilityOptions options)
    {
        var material = options.AuditHmacKey;
        _referenceKey = string.IsNullOrWhiteSpace(material) ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(material);
    }

    public string ToReference(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        return "hmac:v1:" + Convert.ToHexString(HMACSHA256.HashData(_referenceKey, Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
    }

    public string SanitizeSummary(string? value, int maximumLength = 1024)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var result = SensitiveAssignment().Replace(value, "$1=[redacted]");
        result = BearerToken().Replace(result, "Bearer [redacted]");
        result = UrlCredentials().Replace(result, "[url-redacted]");
        result = result.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return result.Length <= maximumLength ? result : result[..maximumLength];
    }

    public SanitizedException SanitizeException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var type = ExceptionTypes.Contains(exception.GetType().Name) ? exception.GetType().Name : "UnhandledException";
        // Never call ToString(): stack traces and nested exceptions frequently contain paths and input.
        return new SanitizedException(type, SanitizeSummary(exception.Message));
    }

    [GeneratedRegex("(?i)\\b(password|passwd|token|secret|authorization|cookie|apikey|api_key|privatekey)\\s*[:=]\\s*[^\\s,;]+")]
    private static partial Regex SensitiveAssignment();
    [GeneratedRegex("(?i)bearer\\s+[a-z0-9._~+/-]+=*")]
    private static partial Regex BearerToken();
    [GeneratedRegex("(?i)https?://[^\\s/@]+:[^\\s/@]+@[^\\s]+")]
    private static partial Regex UrlCredentials();
}
