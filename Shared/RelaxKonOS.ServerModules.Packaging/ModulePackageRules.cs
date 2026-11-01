using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace RelaxKonOS.ModulePackaging;

internal static partial class ModulePackageRules
{
    internal static void Require([DoesNotReturnIf(false)] bool condition, string code)
    { if (!condition) throw new ModulePackageException("module-package-" + code); }
    [GeneratedRegex("^[a-z][a-z0-9.-]{0,95}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
    [GeneratedRegex("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
    internal static bool Id(string? value) => value is not null && IdentifierPattern().IsMatch(value);
    internal static bool Digest(string? value) => value is not null && DigestPattern().IsMatch(value);
    internal static Version Version(string? value)
    {
        Require(value is not null && System.Version.TryParse(value, out var parsed)
            && parsed.Build >= 0 && parsed.Revision == -1 && parsed.ToString(3) == value, "version-invalid");
        return System.Version.Parse(value!);
    }
    internal static bool Path(string? value) => value is { Length: > 0 and <= 240 }
        && value.Split('/').All(part => part.Length is > 0 and <= 100 && part != "." && part != ".."
            && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        && !value.EndsWith('.') && !value.Split('/').Any(p => p.EndsWith('.'))
        && !value.Split('/').Any(p => Reserved(p.Split('.')[0]));
    private static bool Reserved(string value) => value.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
        or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
        or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    internal static void Manifest(ModulePackageManifest m)
    {
        Require(m.SchemaVersion == 3 && m.PackageKind == "server-module" && Id(m.ModuleId) && Id(m.ReleaseId)
            && m.ModuleId is not ("core" or "relaxkonos.core") && Path(m.ModuleId)
            && Id(m.PublisherId) && m.Purpose is "official" or "development" or "third-party"
            && m.Rid is "linux-x64" or "linux-arm64" or "win-x64" or "win-arm64" or "osx-x64" or "osx-arm64",
            "manifest-invalid");
        Require(m.Runtime is "in-process" or "module-host" && (m.Runtime != "in-process" || m.Purpose == "official"), "runtime-invalid");
        Require(m.Purpose == "official" || !m.ModuleId.StartsWith("relaxkonos.", StringComparison.Ordinal), "module-id-reserved");
        Require(m.Runtime != "module-host" || m.Capabilities is { Length: 0 }, "module-host-capability-forbidden");
        RequirePrivileges(m);
        Require(m.Ui is not null && m.Ui.Name is { Length: > 0 and <= 128 }
            && m.Ui.Summary is { Length: <= 512 } && m.Ui.Description is { Length: <= 8192 }
            && m.Ui.License is { Length: > 0 and <= 128 }
            && (m.Ui.Homepage is null || m.Ui.Homepage.Length <= 2048 && Uri.TryCreate(m.Ui.Homepage, UriKind.Absolute, out var homepage)
                && homepage.Scheme == "https" && homepage.UserInfo.Length == 0), "ui-invalid");
        _ = Version(m.Version);
        Require(Version(m.HostApiMinimum) < Version(m.HostApiMaximumExclusive), "host-api-range-invalid");
        Require(Id(m.BuildId) && m.SourceCommit is { Length: 40 or 64 } && m.SourceCommit.All(c => char.IsAsciiHexDigitLower(c)), "build-identity-invalid");
        Require(m.EntryType is { Length: > 0 and <= 256 } && m.EntryType.Split('.').All(p => p.Length > 0
            && (char.IsAsciiLetter(p[0]) || p[0] == '_') && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')), "entry-type-invalid");
        Require(m.Dependencies is { Length: <= 128 } && m.Capabilities is { Length: <= 128 }
            && m.Files is { Length: > 0 and <= 4096 }, "manifest-limits");
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in m.Dependencies!)
            Require(dependency is not null && Id(dependency.ModuleId) && dependency.ModuleId != m.ModuleId
                && dependencies.Add(dependency.ModuleId) && Version(dependency.MinimumVersion) < Version(dependency.MaximumVersionExclusive), "dependency-invalid");
        Require(m.Capabilities!.All(Id) && m.Capabilities.Distinct(StringComparer.Ordinal).Count() == m.Capabilities.Length, "capability-invalid");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in m.Files!)
        {
            Require(file is not null && Path(file.Path) && file.Path.StartsWith("server/" + m.Rid + "/", StringComparison.Ordinal)
                && paths.Add(file.Path) && Digest(file.Sha256) && file.Length is >= 0 and <= 268435456, "file-invalid");
            total += file!.Length;
        }
        Require(total <= 1073741824 && Path(m.EntryAssembly) && m.EntryAssembly.EndsWith(".dll", StringComparison.Ordinal)
            && m.Files.Any(f => f.Path == m.EntryAssembly), "entry-assembly-invalid");
        // A file cannot also be a directory, including on case-insensitive hosts.
        foreach (var path in paths)
        {
            var slash = path.LastIndexOf('/');
            while (slash > 0)
            { Require(!paths.Contains(path[..slash]), "file-directory-collision"); slash = path.LastIndexOf('/', slash - 1); }
        }
    }
    private static void RequirePrivileges(ModulePackageManifest m)
    {
        if (m.Runtime != "module-host")
        {
            Require(m.Privileges is { Length: 0 }, "privilege-invalid");
            return;
        }
        Require(m.Privileges is { Length: <= 64 }, "privilege-invalid");
        var platform = m.Rid.Split('-')[0] switch { "linux" => "linux", "win" => "windows", _ => "macos" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in m.Privileges)
        {
            Require(capability is not null && Capability(capability, platform), "privilege-invalid");
            Require(seen.Add(JsonSerializer.Serialize(capability, ModulePackageJson.Options)), "privilege-invalid");
        }
    }
    private static bool Capability(ModulePackageCapability capability, string platform)
    {
        if (capability.Platforms is not { Length: > 0 and <= 3 } || capability.Platforms.Any(value => value is not ("linux" or "windows" or "macos"))
            || capability.Platforms.Distinct(StringComparer.Ordinal).Count() != capability.Platforms.Length
            || !capability.Platforms.Contains(platform, StringComparer.Ordinal)) return false;
        return capability switch
        {
            ServiceManageCapability service => service.Services is { Length: > 0 and <= 64 }
                && service.Services.All(scope => scope is not null && Id(scope.ServiceId) && Actions(scope.Actions, ModuleCapabilityPolicy.ServiceActions))
                && service.Services.Select(scope => scope.ServiceId).Distinct(StringComparer.Ordinal).Count() == service.Services.Length,
            PackageManageCapability package => package.Manager is "apt" or "windows-msi" && Id(package.SourceRef)
                && packages(package.Packages) && Actions(package.Actions, ModuleCapabilityPolicy.PackageActions)
                && Version(package.Versions.Minimum) < Version(package.Versions.MaximumExclusive),
            FileManageCapability file => Rooted(file.Root) && Actions(file.Actions, ModuleCapabilityPolicy.FileActions)
                && file.MaximumBytes is > 0 and <= 1073741824,
            DataManageCapability data => Rooted(data.Root) && Actions(data.Actions, ModuleCapabilityPolicy.DataActions),
            AccountManageCapability account => account.Accounts is { Length: <= 64 } && account.Groups is { Length: <= 64 }
                && account.Accounts.All(Id) && account.Groups.All(Id) && account.Accounts.Length + account.Groups.Length is > 0
                && Unique(account.Accounts) && Unique(account.Groups) && Actions(account.Actions, ModuleCapabilityPolicy.AccountActions),
            NetworkManageCapability network => network.Interfaces is { Length: > 0 and <= 64 } && network.Interfaces.All(Id)
                && Unique(network.Interfaces) && network.Ports is { Length: > 0 and <= 64 }
                && network.Ports.All(port => port is not null && port.Minimum is >= 1 and <= 65535
                    && port.MaximumExclusive is > 1 and <= 65536 && port.Minimum < port.MaximumExclusive)
                && network.Ports.Select(port => port.Minimum + ":" + port.MaximumExclusive).Distinct(StringComparer.Ordinal).Count() == network.Ports.Length
                && Actions(network.Actions, ModuleCapabilityPolicy.NetworkActions),
            SecretReferenceCapability secret => secret.Secrets is { Length: > 0 and <= 64 }
                && secret.Secrets.All(scope => scope is not null && Id(scope.Purpose) && Id(scope.Target)
                    && Actions(scope.Permissions, ModuleCapabilityPolicy.SecretPermissions))
                && secret.Secrets.Select(scope => scope.Purpose + ":" + scope.Target).Distinct(StringComparer.Ordinal).Count() == secret.Secrets.Length,
            SettingManageCapability setting => setting.Keys is { Length: <= 64 } && setting.RegistryRoots is { Length: <= 64 }
                && setting.ScheduledTasks is { Length: <= 64 } && setting.Keys.All(Id) && setting.ScheduledTasks.All(Id)
                && setting.RegistryRoots.All(RegistryRoot) && Unique(setting.Keys) && Unique(setting.ScheduledTasks) && Unique(setting.RegistryRoots)
                && setting.Keys.Length + setting.RegistryRoots.Length + setting.ScheduledTasks.Length is > 0
                && Actions(setting.Actions, ModuleCapabilityPolicy.SettingActions),
            ApplicationManageCapability application => application.Protocols is { Length: > 0 and <= 8 } && application.Protocols.All(Id)
                && Unique(application.Protocols) && application.Instances is { Length: > 0 and <= 64 } && application.Instances.All(Id)
                && Unique(application.Instances) && Actions(application.Actions, ModuleCapabilityPolicy.ApplicationActions),
            _ => false,
        };
        static bool packages(string[]? values) => values is { Length: > 0 and <= 64 } && values.All(Id) && Unique(values);
    }
    private static bool Unique(string[]? values) => values is not null
        && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static bool Actions(string[]? actions, string[] allowed) =>
        actions is { Length: > 0 and <= 16 } && Unique(actions) && actions.All(value => allowed.Contains(value, StringComparer.Ordinal));
    private static bool Rooted(string? value)
    {
        if (value is not { Length: >= 2 and <= 512 } || value.Any(char.IsControl)) return false;
        var normalized = value.Replace('\\', '/');
        var absolute = normalized[0] == '/'
            || normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/';
        if (!absolute) return false;
        var body = normalized[0] == '/' ? normalized[1..] : normalized[3..];
        if (body.Length == 0) return false;
        return body.Split('/').All(part => part.Length is > 0 and <= 100 && part is not ("." or "..")
            && !part.EndsWith('.') && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' '));
    }
    private static bool RegistryRoot(string? value)
    {
        if (value is not { Length: >= 6 and <= 512 } || value.Any(char.IsControl)) return false;
        var parts = value.Split('\\');
        return parts[0] is "HKLM" or "HKCU" or "HKCR" or "HKU" or "HKCC" && parts.Length > 1
            && parts.Skip(1).All(part => part.Length is > 0 and <= 100 && part is not ("." or "..")
                && part.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-'));
    }
}
