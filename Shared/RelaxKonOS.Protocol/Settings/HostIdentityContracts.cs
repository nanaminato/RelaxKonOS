namespace RelaxKonOS.Protocol.Settings;

/// <summary>
/// Remote host identity snapshot. <c>PendingHostName</c> differs from <c>HostName</c> only on
/// platforms that stage a rename until the next restart; callers must never assume a write is live.
/// <c>MaximumHostNameLength</c> is reported by the platform provider, not inferred by the caller.
/// </summary>
public sealed record HostIdentityState(string HostName, string PendingHostName, int MaximumHostNameLength,
    string Revision, DateTimeOffset ObservedAt, string Provider);
public sealed record HostIdentitySnapshot(HostIdentityState Value, SettingsTarget Target, SettingsCapability Capability,
    SettingsEffectiveState EffectiveState);
public sealed record HostnameChange(string HostName);
public sealed record HostnamePreviewRequest(string ExpectedRevision, string IdempotencyKey, HostnameChange Change);

/// <summary>
/// Host-name syntax is shared by the Server and re-checked by the Helper before any platform write.
/// The maximum length is platform-dependent: Windows requires a NetBIOS-compatible short name,
/// while Linux accepts a longer single label.
/// </summary>
public static class HostIdentityValidation
{
    public const int WindowsMaximumLength = 15;
    public const int LinuxMaximumLength = 63;

    public static int MaximumLength(bool windows) => windows ? WindowsMaximumLength : LinuxMaximumLength;

    /// <summary>Returns a stable problem code, or null when the name is acceptable on the given platform.</summary>
    public static string? Validate(HostnameChange? change, bool windows) => Validate(change, MaximumLength(windows));

    /// <summary>
    /// Validates against the maximum the remote provider reported, so a client never guesses the
    /// target platform's rules from its own operating system.
    /// </summary>
    public static string? Validate(HostnameChange? change, int maximumLength)
    {
        var name = change?.HostName;
        // A host name is a single RFC 952/1123 label: letters, digits and inner hyphens only,
        // never a fully-qualified name, never all digits, and never a control character.
        if (name is not { Length: > 0 } || name.Length > maximumLength) return "settings.identity.invalid_name";
        if (name.Any(char.IsControl)) return "settings.identity.invalid_name";
        if (!char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1])) return "settings.identity.invalid_name";
        if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return "settings.identity.invalid_name";
        if (name.All(char.IsAsciiDigit)) return "settings.identity.invalid_name";
        return null;
    }
}
