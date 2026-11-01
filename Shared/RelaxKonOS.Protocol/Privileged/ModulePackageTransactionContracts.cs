namespace RelaxKonOS.Protocol.Privileged;

/// <summary>Package verbs a module may request once its declaration and the Host policy both allow it.</summary>
public enum ModulePackageTransactionAction
{
    Install,
    Update,
    Repair,
    Remove,
}

/// <summary>One exact artifact apt reported for the same transaction, including its published digest.</summary>
public sealed record ModulePackageArtifact(string FileName, string Uri, long Bytes, string Sha256);

/// <summary>
/// The exact package identity the Host froze from signed repository metadata. A module never supplies
/// this record: the Host resolves it, the user confirms it, and the Helper re-resolves it before running.
/// SHA-256 is required; a repository that publishes only MD5 is not usable for a module transaction.
/// </summary>
public sealed record ModulePackageResolution(
    string Package,
    string Version,
    string Architecture,
    string RepositoryId,
    string BaseUri,
    string Suite,
    string Area,
    string PoolUri,
    string Sha256,
    long Bytes,
    IReadOnlyList<ModulePackageArtifact> Dependencies);

/// <summary>Read-only question: which exact artifact would the Host-approved source publish for this package?</summary>
public sealed record ModulePackageQuery(string SourceRef, string Package);

/// <summary>
/// One closed package transaction. There is no argv, no shell, no environment and no repository URL:
/// the source is a reference the Helper resolves through its own protected policy.
/// </summary>
public sealed record ModulePackageTransaction(
    ModulePackageTransactionAction Action,
    string SourceRef,
    ModulePackageResolution Target,
    bool RemoveConfiguration = false);

/// <summary>
/// User-facing projection of one frozen package identity. It is display-only: the Broker and Helper
/// never parse these strings, and no secret value, credential or local path is ever included.
/// </summary>
public static class ModulePackageDescription
{
    public static string[] Describe(ModulePackageResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return
        [
            $"package: {resolution.Package} {resolution.Version} {resolution.Architecture}",
            $"repository: {resolution.BaseUri.TrimEnd('/')}/{resolution.Suite}/{resolution.Area}",
            $"artifact: {FileName(resolution.PoolUri)} sha256:{resolution.Sha256} ({resolution.Bytes} bytes)",
            $"dependencies: {resolution.Dependencies.Count}",
        ];
    }

    public static string[] Warnings(ModulePackageTransactionAction action, bool removeConfiguration) => action switch
    {
        ModulePackageTransactionAction.Remove => removeConfiguration
            ? ["Removing the package also purges its configuration and data files.", "Dependent services may stop or fail."]
            : ["Configuration and data files are kept by default.", "Dependent services may stop or fail."],
        ModulePackageTransactionAction.Repair =>
            ["Reinstalling runs the package maintainer scripts again.", "Installed files are replaced; configuration files may be preserved or replaced by the package policy."],
        _ => ["Maintainer scripts run with host privileges.", "Dependent services may restart during the transaction.", "A package transaction has no general rollback."],
    };

    private static string FileName(string uri) => uri[(uri.LastIndexOf('/') + 1)..];
}
