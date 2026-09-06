using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using RemoteOS.Core.Applications;

namespace RemoteOS.Core.VirtualSystemDrive;

/// <summary>Origin declared by a disk descriptor. It is a claim, never a trust decision.</summary>
public enum ApplicationDescriptorKind
{
    [JsonStringEnumMemberName("builtin")]
    BuiltIn,
    [JsonStringEnumMemberName("package")]
    Package,
}

public sealed record ApplicationDescriptorIcon(string? Path = null, string? Glyph = null);

/// <summary>Activation data. Built-in and package fields are mutually exclusive.</summary>
public sealed record ApplicationDescriptorActivation(
    string? BuiltInKey = null,
    string? EntryAssembly = null,
    string? EntryType = null);

/// <summary>
/// Persisted, non-executable application metadata. It cannot grant permissions, select a Host
/// service, or establish that an application is built in.
/// </summary>
public sealed record ApplicationDescriptor(
    int SchemaVersion,
    string Id,
    ApplicationDescriptorKind Kind,
    string DisplayName,
    string Version,
    ApplicationDescriptorActivation Activation,
    string? Description = null,
    ApplicationDescriptorIcon? Icon = null,
    IReadOnlyList<string>? RequestedPermissions = null,
    IReadOnlyList<string>? SupportedFileExtensions = null,
    IReadOnlyList<string>? SupportedUriSchemes = null,
    string? InstancePolicy = null,
    IReadOnlyList<string>? ClientPlatforms = null,
    int PermissionModelVersion = 2);

public sealed record DescriptorValidationResult(bool IsValid, string? ProblemCode = null)
{
    public static DescriptorValidationResult Valid { get; } = new(true);
    public static DescriptorValidationResult Invalid(string problemCode) => new(false, problemCode);
}

/// <summary>Pure validation for metadata after it has been read by a Host-owned storage service.</summary>
public static partial class ApplicationDescriptorValidator
{
    public const int CurrentSchemaVersion = 1;

    public static DescriptorValidationResult Validate(ApplicationDescriptor? descriptor)
    {
        if (descriptor is null)
            return DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.JsonInvalid);
        if (descriptor.SchemaVersion != CurrentSchemaVersion)
            return DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.SchemaUnsupported);
        if (!IsValidAppId(descriptor.Id))
            return DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.AppIdInvalid);
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName) || string.IsNullOrWhiteSpace(descriptor.Version)
            || descriptor.PermissionModelVersion != 2 || descriptor.Activation is null)
            return DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.PackageLayoutInvalid);

        return descriptor.Kind switch
        {
            ApplicationDescriptorKind.BuiltIn => ValidateBuiltIn(descriptor),
            ApplicationDescriptorKind.Package => ValidatePackage(descriptor),
            _ => DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.PackageLayoutInvalid),
        };
    }

    public static bool IsValidAppId(string? id) => !string.IsNullOrWhiteSpace(id)
        && AppIdPattern().IsMatch(id)
        && id.Equals(id.ToLowerInvariant(), StringComparison.Ordinal);

    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\'))
            return false;
        var segments = path.Split('/', StringSplitOptions.None);
        return segments.Length > 0 && segments.All(segment => !string.IsNullOrWhiteSpace(segment)
            && segment is not "." and not ".." && !segment.Contains(':'));
    }

    private static DescriptorValidationResult ValidateBuiltIn(ApplicationDescriptor descriptor)
    {
        if (!descriptor.Id.StartsWith("remoteos.", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(descriptor.Activation.BuiltInKey)
            || descriptor.Activation.EntryAssembly is not null || descriptor.Activation.EntryType is not null)
            return DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.BuiltInMismatch);
        return DescriptorValidationResult.Valid;
    }

    private static DescriptorValidationResult ValidatePackage(ApplicationDescriptor descriptor)
    {
        if (descriptor.Id.StartsWith("remoteos.", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(descriptor.Activation.BuiltInKey)
            || !IsSafeRelativePath(descriptor.Activation.EntryAssembly)
            || !descriptor.Activation.EntryAssembly!.StartsWith("lib/", StringComparison.Ordinal)
            || !descriptor.Activation.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(descriptor.Icon?.Path) && !IsSafeRelativePath(descriptor.Icon.Path))
            || string.IsNullOrWhiteSpace(descriptor.Activation.EntryType))
            return DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.PackageLayoutInvalid);
        return DescriptorValidationResult.Valid;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{2,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdPattern();
}
