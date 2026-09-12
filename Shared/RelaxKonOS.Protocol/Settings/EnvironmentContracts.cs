using System.Text;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<EnvironmentValueKind>))]
public enum EnvironmentValueKind { String, ExpandString }
[JsonConverter(typeof(JsonStringEnumConverter<EnvironmentMutationKind>))]
public enum EnvironmentMutationKind { Set, Delete }
[JsonConverter(typeof(JsonStringEnumConverter<EnvironmentPathMode>))]
public enum EnvironmentPathMode { Replace, Append }

/// <summary>Delete is an explicit operation; an empty string remains a stored value.</summary>
public sealed record EnvironmentMutation(string Name, EnvironmentMutationKind Operation, string? Value = null,
    EnvironmentValueKind ValueKind = EnvironmentValueKind.String);
public sealed record EnvironmentChangeSet(IReadOnlyList<EnvironmentMutation> Changes, bool ConfirmHighImpact = false);
public sealed record EnvironmentPreviewRequest(SettingsScope Scope, string ExpectedRevision, string IdempotencyKey,
    EnvironmentChangeSet Change);
public sealed record EnvironmentVariable(string Name, string? RawValue, string? ExpandedPreview,
    EnvironmentValueKind ValueKind, SettingsScope Source, bool Sensitive, bool Masked,
    IReadOnlyList<string> Warnings);
public sealed record HostEnvironmentSnapshot(SettingsTarget Target, string Revision, DateTimeOffset ObservedAt,
    SettingsCapability Capability, SettingsEffectiveState EffectiveState, string Provider,
    bool CaseSensitiveNames, string PathSeparator, IReadOnlyList<EnvironmentVariable> Variables);
public sealed record WorkspaceEnvironmentSnapshot(Guid WorkspaceId, string Revision, DateTimeOffset ObservedAt,
    IReadOnlyList<EnvironmentVariable> Variables, EnvironmentPathMode PathMode);
public sealed record WorkspaceEnvironmentUpdate(string ExpectedRevision, EnvironmentChangeSet Change, EnvironmentPathMode PathMode);

/// <summary>Shared validation is repeated at the Helper boundary, before provider access.</summary>
public static class EnvironmentValidation
{
    public const int MaximumChanges = 128;
    public const int MaximumNameLength = 255;
    public const int MaximumValueLength = 32767;
    public const int MaximumChangeBytes = 256 * 1024;
    public const int MaximumExpansionDepth = 16;
    public const int MaximumExpandedLength = 128 * 1024;

    public static string? Validate(EnvironmentChangeSet? change, bool windows)
    {
        if (change?.Changes is not { Count: > 0 and <= MaximumChanges }) return "settings.environment.invalid_batch";
        var names = new HashSet<string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var bytes = 0;
        foreach (var item in change.Changes)
        {
            if (item is null || !IsValidName(item.Name, windows)) return "settings.environment.invalid_name";
            if (!names.Add(item.Name)) return "settings.environment.duplicate_name";
            if (!Enum.IsDefined(item.Operation) || !Enum.IsDefined(item.ValueKind)) return "settings.environment.invalid_operation";
            if (item.Operation == EnvironmentMutationKind.Delete)
            {
                if (item.Value is not null || item.ValueKind != EnvironmentValueKind.String) return "settings.environment.invalid_delete";
            }
            else if (item.Value is null || item.Value.Length > MaximumValueLength || item.Value.Contains('\0') || !ValidUnicode(item.Value))
                return "settings.environment.invalid_value";
            if (!windows && item.ValueKind != EnvironmentValueKind.String) return "settings.environment.value_kind_unsupported";
            bytes += Encoding.UTF8.GetByteCount(item.Name) + Encoding.UTF8.GetByteCount(item.Value ?? "");
            if (bytes > MaximumChangeBytes) return "settings.environment.batch_too_large";
            if (IsHighImpact(item.Name, windows) && !change.ConfirmHighImpact) return "settings.environment.high_impact_confirmation_required";
        }
        return null;
    }

    public static bool IsValidName(string? name, bool windows)
    {
        if (name is not { Length: > 0 and <= MaximumNameLength } || !ValidUnicode(name)
            || name.Any(c => c == '=' || char.IsControl(c))) return false;
        return windows || (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    public static bool IsHighImpact(string name, bool windows)
    {
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return new[] { "PATH", "PATHEXT", "COMSPEC", "SYSTEMROOT", "WINDIR", "DOTNET_ROOT", "DOTNET_STARTUP_HOOKS",
            "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "NODE_OPTIONS", "PYTHONPATH", "PYTHONHOME", "PERL5OPT", "RUBYOPT" }
            .Any(key => name.Equals(key, comparison))
            || new[] { "LD_", "DYLD_", "COR_", "CORECLR_", "DOTNET_" }.Any(prefix => name.StartsWith(prefix, comparison));
    }

    public static bool IsPotentiallySensitive(string name) => new[] { "SECRET", "TOKEN", "PASSWORD", "PASSWD", "CREDENTIAL", "PRIVATE", "KEY", "CONNECTION_STRING" }
        .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static bool ValidUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}
