namespace RemoteOS.Core.VirtualSystemDrive;

public sealed record ShellDescriptor(int SchemaVersion, string Id, string DisplayName,
    string? PreviewPath = null, IReadOnlyList<string>? Features = null);

public enum RemoteOsShortcutKind
{
    Application,
    RemoteFile,
    RemoteFolder,
    Script,
    Uri,
}

/// <summary>Persisted desktop link. Target interpretation belongs exclusively to the Host router.</summary>
public sealed record RemoteOsShortcut(int SchemaVersion, string Id, string DisplayName,
    RemoteOsShortcutKind Kind, string Target, ApplicationDescriptorIcon? Icon = null);

/// <summary>Pure validation; shortcut payloads never carry local paths, command lines, or grants.</summary>
public static class RemoteOsShortcutValidator
{
    public const int CurrentSchemaVersion = 1;

    public static DescriptorValidationResult Validate(RemoteOsShortcut? shortcut)
    {
        if (shortcut is null) return DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.ShortcutInvalid);
        if (shortcut.SchemaVersion != CurrentSchemaVersion || !Guid.TryParse(shortcut.Id, out _)
            || string.IsNullOrWhiteSpace(shortcut.DisplayName) || string.IsNullOrWhiteSpace(shortcut.Target))
            return DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.ShortcutInvalid);

        return shortcut.Kind switch
        {
            RemoteOsShortcutKind.Application when ApplicationDescriptorValidator.IsValidAppId(shortcut.Target)
                => DescriptorValidationResult.Valid,
            RemoteOsShortcutKind.Script when ApplicationDescriptorValidator.IsSafeRelativePath(shortcut.Target)
                && shortcut.Target.StartsWith("Scripts/", StringComparison.Ordinal)
                => DescriptorValidationResult.Valid,
            RemoteOsShortcutKind.Uri when Uri.TryCreate(shortcut.Target, UriKind.Absolute, out var uri)
                && uri.Scheme.Equals("remoteos", StringComparison.OrdinalIgnoreCase)
                => DescriptorValidationResult.Valid,
            RemoteOsShortcutKind.RemoteFile or RemoteOsShortcutKind.RemoteFolder when IsRemotePath(shortcut.Target)
                => DescriptorValidationResult.Valid,
            _ => DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.ShortcutInvalid),
        };
    }

    private static bool IsRemotePath(string path) => !path.Contains('\0') && !path.Contains('\\')
        && !path.Contains("://", StringComparison.Ordinal) && !path.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
}
