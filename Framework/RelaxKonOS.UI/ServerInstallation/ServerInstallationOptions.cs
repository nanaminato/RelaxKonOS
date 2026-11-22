using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.UI.ServerInstallation;

public sealed record ServerInstallationOptions(
    ServerPackageSourceKind Source,
    ServerInstallMode? Mode,
    string? LocalBundlePath,
    string? RemoteBundlePath,
    ServerFileAccessScope FileAccess,
    ServerNetworkProfile Network,
    ServerCertificateMode CertificateMode,
    ServerCertificateFormat CertificateFormat,
    string? CertificatePath,
    string? CertificatePrivateKeyPath,
    string CertificatePassword,
    string SelfSignedIdentities,
    string SudoPassword = "",
    int ServerPort = 5000,
    string? PackageUri = null, string? PackageDigest = null, string? ReleaseCatalogBaseUri = null,
    string? InstallRoot = null, string? DataRoot = null, string? ConfigRoot = null,
    string? StateRoot = null, string? CacheRoot = null,
    IReadOnlyList<string>? FileRoots = null, ServerFileAccessScope? AdministratorFileAccess = null,
    IReadOnlyList<string>? AdministratorFileRoots = null, ServerFileAccessScope? RootFileAccess = null,
    IReadOnlyList<string>? RootFileRoots = null, bool DockerAccess = false, bool AllowUnsupportedSystem = false, bool AddFirewallRule = false, bool GuardianEnabled = true);
