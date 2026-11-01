using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Privileged;

/// <summary>
/// One Host-approved database instance and the two root-owned credential files the Helper may use.
/// The application account is deliberately distinct from the administrative account so a failed
/// rotation can still be rolled back with the unchanged administrative credential.
/// </summary>
public sealed record ModuleApplicationInstance(string Instance, string Protocol, string Socket,
    string AdminUser, string AdminSecret, string Account, string AccountHost, string AccountSecret, string[] Actions);

/// <summary>Root-owned application-management policy. Read on every request; never cached.</summary>
public sealed record ModuleApplicationPolicy(int SchemaVersion, ModuleApplicationInstance[] Instances)
{
    private const string SecretRoot = "/etc/relaxkonos/secrets/";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16,
    };

    public static bool TryLoad(string path, out ModuleApplicationPolicy? policy, out string problem)
    {
        policy = null;
        problem = "module-application-policy-unavailable";
        try
        {
            if (!OperatingSystem.IsLinux() || !File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 65536 || info.LinkTarget is not null) return false;
            var (owner, _) = RelaxKonOS.PrivilegedHelper.LinuxFileOwnership.Read(path);
            if (owner != 0) return false;
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return false;
            return TryParse(File.ReadAllBytes(path), out policy, out problem);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    public static bool TryParse(ReadOnlySpan<byte> json, out ModuleApplicationPolicy? policy, out string problem)
    {
        policy = null;
        problem = "module-application-policy-invalid";
        try
        {
            var parsed = JsonSerializer.Deserialize<ModuleApplicationPolicy>(json, Options);
            if (parsed is null || parsed.SchemaVersion != 1 || !parsed.Valid()) return false;
            policy = parsed;
            problem = "";
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return false;
        }
    }

    public bool TryFind(string? instance, ModuleApplicationAction action, out ModuleApplicationInstance? entry)
    {
        var name = action switch
        {
            ModuleApplicationAction.Query => "query",
            ModuleApplicationAction.RotateCredential => "rotate-credential",
            _ => "",
        };
        entry = Instances.FirstOrDefault(item => item.Instance == instance
            && item.Actions.Contains(name, StringComparer.Ordinal));
        return entry is not null;
    }

    private bool Valid() => Instances is { Length: > 0 and <= 64 }
        && Instances.All(item => item is not null
            && ModuleApplicationIdentity.Identifier(item.Instance)
            && item.Protocol == "mysql-admin"
            && ModuleResourceIdentity.CanonicalPath(item.Socket)
            && ModuleApplicationIdentity.MySQLAccount(item.AdminUser)
            && SecretPath(item.AdminSecret)
            && ModuleApplicationIdentity.MySQLAccount(item.Account)
            && ModuleApplicationIdentity.MySQLHost(item.AccountHost)
            && SecretPath(item.AccountSecret)
            && item.Account != item.AdminUser
            && item.Actions is { Length: > 0 and <= 8 }
            && item.Actions.Distinct(StringComparer.Ordinal).Count() == item.Actions.Length
            && item.Actions.All(action => action is "query" or "rotate-credential"))
        && Instances.Select(item => item.Instance).Distinct(StringComparer.Ordinal).Count() == Instances.Length;

    private static bool SecretPath(string? value) => ModuleResourceIdentity.CanonicalPath(value)
        && value.StartsWith(SecretRoot, StringComparison.Ordinal)
        && value.Length > SecretRoot.Length;
}

/// <summary>Root-owned credential file. Its content never leaves the Helper.</summary>
public sealed record ModuleApplicationSecret(int SchemaVersion, string Revision, string User, string Password)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
    };

    public static bool TryLoad(string path, string expectedUser, out ModuleApplicationSecret? secret, out string problem)
    {
        secret = null;
        problem = "module-application-secret-unavailable";
        try
        {
            if (!OperatingSystem.IsLinux() || !File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 4096 || info.LinkTarget is not null) return false;
            var (owner, _) = RelaxKonOS.PrivilegedHelper.LinuxFileOwnership.Read(path);
            if (owner != 0) return false;
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) return false;
            if (!TryParse(File.ReadAllBytes(path), expectedUser, out secret, out problem)) return false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    public static bool TryParse(ReadOnlySpan<byte> json, string expectedUser, out ModuleApplicationSecret? secret,
        out string problem)
    {
        secret = null;
        problem = "module-application-secret-invalid";
        try
        {
            var parsed = JsonSerializer.Deserialize<ModuleApplicationSecret>(json, Options);
            if (parsed is null || parsed.SchemaVersion != 1 || !ModuleApplicationIdentity.Revision(parsed.Revision)
                || !ModuleApplicationIdentity.MySQLAccount(parsed.User) || parsed.User != expectedUser
                || parsed.Password is not { Length: > 0 and <= 256 } || parsed.Password.Any(char.IsControl))
                return false;
            secret = parsed;
            problem = "";
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return false;
        }
    }
}
