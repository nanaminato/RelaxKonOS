using System.Text.Json.Serialization;
using RelaxKonOS.ModuleHost.Protocol;

namespace RelaxKonOS.ModulePackaging;

/// <summary>
/// One declared capability category with typed resource scope. A module declares what it needs; it never
/// declares that it is already authorized. Unknown categories, unknown fields and out-of-range resources
/// are rejected when the manifest is read, not deferred to execution.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ServiceManageCapability), ModuleCapabilityPolicy.ServiceManage)]
[JsonDerivedType(typeof(PackageManageCapability), ModuleCapabilityPolicy.PackageManage)]
[JsonDerivedType(typeof(FileManageCapability), ModuleCapabilityPolicy.FileManage)]
[JsonDerivedType(typeof(DataManageCapability), ModuleCapabilityPolicy.DataManage)]
[JsonDerivedType(typeof(AccountManageCapability), ModuleCapabilityPolicy.AccountManage)]
[JsonDerivedType(typeof(NetworkManageCapability), ModuleCapabilityPolicy.NetworkManage)]
[JsonDerivedType(typeof(SecretReferenceCapability), ModuleCapabilityPolicy.SecretReference)]
[JsonDerivedType(typeof(SettingManageCapability), ModuleCapabilityPolicy.SettingManage)]
[JsonDerivedType(typeof(ApplicationManageCapability), ModuleCapabilityPolicy.ApplicationManage)]
public abstract record ModulePackageCapability
{
    /// <summary>Host platforms this declaration applies to: linux, windows, macos.</summary>
    public required string[] Platforms { get; init; }

    [JsonIgnore]
    public string Kind => this switch
    {
        ServiceManageCapability => ModuleCapabilityPolicy.ServiceManage,
        PackageManageCapability => ModuleCapabilityPolicy.PackageManage,
        FileManageCapability => ModuleCapabilityPolicy.FileManage,
        DataManageCapability => ModuleCapabilityPolicy.DataManage,
        AccountManageCapability => ModuleCapabilityPolicy.AccountManage,
        NetworkManageCapability => ModuleCapabilityPolicy.NetworkManage,
        SecretReferenceCapability => ModuleCapabilityPolicy.SecretReference,
        SettingManageCapability => ModuleCapabilityPolicy.SettingManage,
        ApplicationManageCapability => ModuleCapabilityPolicy.ApplicationManage,
        _ => throw new InvalidOperationException("Unknown capability category."),
    };
}

/// <summary>Closed set of service actions. The Helper still maps each action to a typed operation.</summary>
public sealed record ModuleServiceScope(string ServiceId, string[] Actions);

public sealed record ServiceManageCapability : ModulePackageCapability
{
    public required ModuleServiceScope[] Services { get; init; }
}

public sealed record ModuleVersionInterval(string Minimum, string MaximumExclusive);

public sealed record PackageManageCapability : ModulePackageCapability
{
    /// <summary>Host package manager adapter identity: apt, windows-msi.</summary>
    public required string Manager { get; init; }

    /// <summary>Reference to a source the Host approved independently. No URLs, keys or digests here.</summary>
    public required string SourceRef { get; init; }

    public required string[] Packages { get; init; }
    public required ModuleVersionInterval Versions { get; init; }
    public required string[] Actions { get; init; }
}

public sealed record FileManageCapability : ModulePackageCapability
{
    /// <summary>Absolute confined root. Actions never address a path outside it.</summary>
    public required string Root { get; init; }

    public required string[] Actions { get; init; }
    public required long MaximumBytes { get; init; }
}

public sealed record DataManageCapability : ModulePackageCapability
{
    public required string Root { get; init; }
    public required string[] Actions { get; init; }
}

public sealed record AccountManageCapability : ModulePackageCapability
{
    public required string[] Accounts { get; init; }
    public required string[] Groups { get; init; }
    public required string[] Actions { get; init; }
}

public sealed record ModulePortInterval(int Minimum, int MaximumExclusive);

public sealed record NetworkManageCapability : ModulePackageCapability
{
    public required string[] Interfaces { get; init; }
    public required ModulePortInterval[] Ports { get; init; }
    public required string[] Actions { get; init; }
}

public sealed record ModuleSecretScope(string Purpose, string Target, string[] Permissions);

public sealed record SecretReferenceCapability : ModulePackageCapability
{
    public required ModuleSecretScope[] Secrets { get; init; }
}

public sealed record SettingManageCapability : ModulePackageCapability
{
    /// <summary>Host-defined setting keys only; a module cannot invent one at execution time.</summary>
    public required string[] Keys { get; init; }
    public required string[] RegistryRoots { get; init; }
    public required string[] ScheduledTasks { get; init; }
    public required string[] Actions { get; init; }
}

public sealed record ApplicationManageCapability : ModulePackageCapability
{
    /// <summary>Host application-management adapters: mysql-admin.</summary>
    public required string[] Protocols { get; init; }
    public required string[] Instances { get; init; }
    public required string[] Actions { get; init; }
}

/// <summary>
/// Frozen category catalog, action sets and the current Host execution matrix. A declared category that
/// the Host has no adapter for is accepted as a declaration but never executes.
/// </summary>
public static class ModuleCapabilityPolicy
{
    public const string ServiceManage = ModuleCapabilityCatalog.ServiceManage;
    public const string PackageManage = ModuleCapabilityCatalog.PackageManage;
    public const string FileManage = ModuleCapabilityCatalog.FileManage;
    public const string DataManage = ModuleCapabilityCatalog.DataManage;
    public const string AccountManage = ModuleCapabilityCatalog.AccountManage;
    public const string NetworkManage = ModuleCapabilityCatalog.NetworkManage;
    public const string SecretReference = ModuleCapabilityCatalog.SecretReference;
    public const string SettingManage = ModuleCapabilityCatalog.SettingManage;
    public const string ApplicationManage = ModuleCapabilityCatalog.ApplicationManage;

    internal static readonly string[] ServiceActions = ModuleCapabilityCatalog.Actions(ModuleCapabilityCatalog.ServiceManage);
    internal static readonly string[] PackageActions = ModuleCapabilityCatalog.Actions(ModuleCapabilityCatalog.PackageManage);
    internal static readonly string[] FileActions = ModuleCapabilityCatalog.Actions(ModuleCapabilityCatalog.FileManage);
    internal static readonly string[] DataActions = ModuleCapabilityCatalog.Actions(ModuleCapabilityCatalog.DataManage);
    internal static readonly string[] AccountActions = ModuleCapabilityCatalog.Actions(ModuleCapabilityCatalog.AccountManage);
    internal static readonly string[] NetworkActions = ModuleCapabilityCatalog.Actions(ModuleCapabilityCatalog.NetworkManage);
    internal static readonly string[] SecretPermissions = ModuleCapabilityCatalog.Actions(ModuleCapabilityCatalog.SecretReference);
    internal static readonly string[] SettingActions = ModuleCapabilityCatalog.Actions(ModuleCapabilityCatalog.SettingManage);
    internal static readonly string[] ApplicationActions = ModuleCapabilityCatalog.Actions(ModuleCapabilityCatalog.ApplicationManage);
    internal static readonly string[] Platforms = ["linux", "windows", "macos"];

    public static string[] Kinds => ModuleCapabilityCatalog.Kinds;

    /// <summary>The only category the current Host can execute. Everything else fails closed.</summary>
    public static bool IsExecutable(string kind) => ModuleCapabilityCatalog.IsExecutable(kind);

    public static string[] Actions(string kind) => ModuleCapabilityCatalog.Actions(kind);

    /// <summary>Bind the declared resource scope to one concrete request. No wildcards, no re-resolution.</summary>
    public static bool Allows(ModulePackageCapability capability, string action, string target) => capability switch
    {
        ServiceManageCapability service => service.Services.Any(item => item.ServiceId == target
            && item.Actions.Contains(action, StringComparer.Ordinal)),
        PackageManageCapability package => package.Packages.Contains(target, StringComparer.Ordinal)
            && package.Actions.Contains(action, StringComparer.Ordinal),
        FileManageCapability file => file.Actions.Contains(action, StringComparer.Ordinal)
            && RootContains(file.Root, target),
        DataManageCapability data => data.Actions.Contains(action, StringComparer.Ordinal)
            && RootContains(data.Root, target),
        AccountManageCapability account => account.Actions.Contains(action, StringComparer.Ordinal)
            && (account.Accounts.Contains(target, StringComparer.Ordinal) || account.Groups.Contains(target, StringComparer.Ordinal)),
        NetworkManageCapability network => network.Actions.Contains(action, StringComparer.Ordinal)
            && network.Interfaces.Contains(target, StringComparer.Ordinal),
        SecretReferenceCapability secret => secret.Secrets.Any(item => item.Target == target
            && item.Permissions.Contains(action, StringComparer.Ordinal)),
        SettingManageCapability setting => setting.Actions.Contains(action, StringComparer.Ordinal)
            && (setting.Keys.Contains(target, StringComparer.Ordinal)
                || setting.ScheduledTasks.Contains(target, StringComparer.Ordinal)
                || setting.RegistryRoots.Contains(target, StringComparer.Ordinal)),
        ApplicationManageCapability application => application.Instances.Contains(target, StringComparer.Ordinal)
            && application.Actions.Contains(action, StringComparer.Ordinal),
        _ => false,
    };

    /// <summary>Stable display projection. Resource text never carries a secret value.</summary>
    public static string[] Describe(ModulePackageCapability capability) => capability switch
    {
        ServiceManageCapability service => service.Services.Select(item =>
            $"service:{item.ServiceId}:{string.Join(',', item.Actions)}").ToArray(),
        PackageManageCapability package => [$"package:{package.Manager}:{package.SourceRef}:"
            + $"{string.Join(',', package.Packages)}:{package.Versions.Minimum}..{package.Versions.MaximumExclusive}"
            + $":{string.Join(',', package.Actions)}"],
        FileManageCapability file => [$"file:{file.Root}:{string.Join(',', file.Actions)}:{file.MaximumBytes}"],
        DataManageCapability data => [$"data:{data.Root}:{string.Join(',', data.Actions)}"],
        AccountManageCapability account => [$"account:{string.Join(',', account.Accounts)}:"
            + $"{string.Join(',', account.Groups)}:{string.Join(',', account.Actions)}"],
        NetworkManageCapability network => [$"network:{string.Join(',', network.Interfaces)}:"
            + $"{string.Join(',', network.Ports.Select(port => port.Minimum + ".." + port.MaximumExclusive))}"
            + $":{string.Join(',', network.Actions)}"],
        SecretReferenceCapability secret => secret.Secrets.Select(item =>
            $"secret:{item.Purpose}:{item.Target}:{string.Join(',', item.Permissions)}").ToArray(),
        SettingManageCapability setting => [$"setting:{string.Join(',', setting.Keys)}:"
            + $"{string.Join(',', setting.RegistryRoots)}:{string.Join(',', setting.ScheduledTasks)}"],
        ApplicationManageCapability application => [$"app:{string.Join(',', application.Protocols)}:"
            + $"{string.Join(',', application.Instances)}:{string.Join(',', application.Actions)}"],
        _ => [],
    };

    private static bool RootContains(string root, string target)
    {
        var normalizedRoot = root.Replace('\\', '/').TrimEnd('/');
        var normalizedTarget = target.Replace('\\', '/');
        return normalizedTarget.Length > normalizedRoot.Length
            && normalizedTarget.StartsWith(normalizedRoot + "/", StringComparison.Ordinal)
            && !normalizedTarget.Split('/').Any(segment => segment is "." or "..");
    }
}
