using RelaxKonOS.Protocol.ManagedRuntimes;
namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

public static class PackageIdentityPolicy
{
    public static bool Valid(ComponentInstalledPackage? identity) => identity is not null
        && HostPackageDefinitions.KnownPackage(identity.PackageName)
        && identity.Version is { Length: > 0 and <= 128 } && identity.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '~' or ':' or '-')
        && identity.Architecture is { Length: > 0 and <= 32 } && identity.Architecture.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && identity.ExecutableSha256 is { Length: 64 } && identity.ExecutableSha256.All(Uri.IsHexDigit);
    public static bool SamePackage(ComponentInstalledPackage authorized, ComponentInstalledPackage observed) =>
        Valid(authorized) && Valid(observed) && authorized.PackageName == observed.PackageName && authorized.Architecture == observed.Architecture;
    public static (bool Managed, bool ExternalChange, bool RecoveryRequired) Assess(ComponentInstalledPackage? authorized,
        ComponentInstalledPackage? verified, bool pending)
    {
        var managed = authorized is not null && verified is not null && SamePackage(authorized, verified);
        return (managed, managed && authorized != verified, pending || authorized is not null && !managed);
    }
    public static bool ProvesOriginalCommit(ComponentInstalledPackage? recorded, ComponentInstalledPackage? verified) =>
        Valid(recorded) && Valid(verified) && recorded == verified;
    public static bool OnlyConfigurationChanges(string? output) => output is not null && output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .All(line => line.Length > 13 && line[9] == ' ' && line[10] == 'c' && line[11] == ' '
            && line[12] == '/' && line[..9].All(c => c is '.' or '?' or '5'));
}
