namespace RelaxKonOS.ModuleHost.Protocol;

/// <summary>
/// Frozen capability catalog shared by the manifest validator, the Broker, the desktop client and tests.
/// Categories are generic system capabilities; they are never per-application identifiers.
/// </summary>
public static class ModuleCapabilityCatalog
{
    public const string ServiceManage = "service.manage";
    public const string PackageManage = "package.manage";
    public const string FileManage = "file.manage";
    public const string DataManage = "data.manage";
    public const string AccountManage = "account.manage";
    public const string NetworkManage = "network.manage";
    public const string SecretReference = "secret.reference";
    public const string SettingManage = "setting.manage";
    public const string ApplicationManage = "app.manage";

    public static string[] Kinds { get; } =
    [
        ServiceManage, PackageManage, FileManage, DataManage, AccountManage,
        NetworkManage, SecretReference, SettingManage, ApplicationManage,
    ];

    /// <summary>Closed action set per category. An action outside its category is never planned.</summary>
    public static string[] Actions(string kind) => kind switch
    {
        ServiceManage => ["start", "stop", "restart"],
        PackageManage => ["install", "update", "repair", "remove"],
        FileManage => ["write", "delete", "set-mode", "set-owner"],
        DataManage => ["preserve", "backup", "restore", "delete"],
        AccountManage => ["create", "delete", "grant"],
        NetworkManage => ["listen", "firewall-allow", "firewall-deny", "tun"],
        SecretReference => ["read", "write", "rotate"],
        SettingManage => ["read", "write"],
        ApplicationManage => ["query", "configure", "rotate-credential", "recover-credential"],
        _ => [],
    };

    /// <summary>Categories with a delivered Host adapter. package.manage still requires a Helper that can
    /// resolve the declared source and package; file.manage and data.manage still require a Helper whose
    /// root-owned resource policy approves the declared root. Each adapter can answer "unsupported" at
    /// plan time rather than degrade to a command.</summary>
    public static bool IsExecutable(string kind) =>
        kind is ServiceManage or PackageManage or FileManage or DataManage or ApplicationManage;

    public static bool IsCategory(string? kind) => kind is not null && Kinds.Contains(kind, StringComparer.Ordinal);

    public static bool IsAction(string kind, string? action) =>
        action is not null && Actions(kind).Contains(action, StringComparer.Ordinal);

    public static bool IsPlatform(string? value) => value is "linux" or "windows" or "macos";
}
