using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Protocol.Privileged;

/// <summary>Local authenticated Helper transport only. Never expose raw values through HTTP or audit logs.</summary>
public sealed record PrivilegedEnvironmentValue(string Name, string Value, EnvironmentValueKind Kind);
public sealed record PrivilegedEnvironmentState(SettingsTarget Target, string Revision, string Provider,
    IReadOnlyList<PrivilegedEnvironmentValue> Values, bool NotificationDelivered = false);
