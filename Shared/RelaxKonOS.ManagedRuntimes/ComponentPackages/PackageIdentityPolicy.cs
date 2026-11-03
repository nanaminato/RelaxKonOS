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

    /// <summary>
    /// Checks a recovery proof read back from disk.
    /// </summary>
    /// <remarks>
    /// Registration is the one action with no original operation to recover, so its
    /// <c>TargetOperationId</c> is <see cref="Guid.Empty"/> by construction (the writer is
    /// <c>WriteRegistrationRecovery</c>). Every other action must point at the operation it recovers.
    /// Requiring a non-empty target for registration made the proof unreadable, which turned a
    /// successful registration into "存在未完成的恢复操作" and blocked the component until the entries
    /// were cleaned by hand.
    /// </remarks>
    public static bool ValidRecoveryReceipt(ComponentRecoveryReceipt? receipt, Guid operationId) =>
        receipt is not null && receipt.OperationId == operationId && Enum.IsDefined(receipt.Action)
        && (receipt.Action == ComponentRecoveryAction.RegisterPackage || receipt.TargetOperationId != Guid.Empty);

    public static bool OnlyConfigurationChanges(string? output) => output is not null && output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .All(line => line.Length > 13 && line[9] == ' ' && line[10] == 'c' && line[11] == ' '
            && line[12] == '/' && line[..9].All(c => c is '.' or '?' or '5'));
}
