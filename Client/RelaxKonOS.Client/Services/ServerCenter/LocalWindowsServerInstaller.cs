using System.Security.Cryptography.X509Certificates;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

public interface ILocalWindowsDeploymentSession : IAsyncDisposable
{
    Task<ServerDeploymentOperationDto> ExecuteAsync(ServerDeploymentRequest request, Stream? archive,
        Stream? certificate, string? certificatePassword, IProgress<ServerDeploymentTransfer?>? transfer,
        CancellationToken cancellationToken);
}

public interface ILocalWindowsDeploymentSessionFactory
{
    Task<ILocalWindowsDeploymentSession> OpenAsync(ServerInstallMode mode, CancellationToken cancellationToken);
}

/// <summary>Local orchestration; package policy, deployment locks and receipts remain owned by the launcher.</summary>
public sealed class LocalWindowsServerInstaller(
    ILocalWindowsDeploymentSessionFactory sessions,
    IServerCenterReleaseSource releases,
    IServerCenterOperationJournal journal)
{
    public const string JournalHostId = "local-windows";
    public ServerInstallMode Mode { get; set; } = ServerInstallMode.WindowsSystem;
    public Guid? LastOperationId { get; private set; }
    public ServerHostSnapshotDto? LastVerifiedSnapshot { get; private set; }
    public string? LastFirewallStatus { get; private set; }

    public async Task<ServerHostSnapshotDto> StatusAsync(CancellationToken cancellationToken = default)
    {
        LastOperationId = null;
        await using var session = await sessions.OpenAsync(Mode, cancellationToken).ConfigureAwait(false);
        return await ReadStatusAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServerHostSnapshotDto> MaintainAsync(ServerDeploymentKind kind, ServerHostSnapshotDto reviewed,
        string language, bool confirmed, ServerDataRetention retention = ServerDataRetention.Retain,
        bool regenerateSelfSignedCertificate = false, string? selfSignedIdentities = null, bool repairFirewall = false,
        CancellationToken cancellationToken = default)
    {
        LastOperationId = null;
        if (kind is not (ServerDeploymentKind.Repair or ServerDeploymentKind.Rollback or ServerDeploymentKind.Uninstall) ||
            !confirmed || !reviewed.Installed || reviewed.Mode != Mode ||
            !ServerInstallationId.IsValid(reviewed.InstallationId) || !Enum.IsDefined(retention) ||
            retention == ServerDataRetention.Delete && kind != ServerDeploymentKind.Uninstall ||
            kind != ServerDeploymentKind.Repair && (regenerateSelfSignedCertificate || repairFirewall) ||
            regenerateSelfSignedCertificate && string.IsNullOrWhiteSpace(selfSignedIdentities))
            throw new ArgumentException("Review and confirm a valid local lifecycle action first.");
        await using var session = await sessions.OpenAsync(Mode, cancellationToken).ConfigureAwait(false);
        var current = await ReadStatusAsync(session, cancellationToken).ConfigureAwait(false);
        if (!current.Installed || current.Mode != reviewed.Mode || current.InstallationId != reviewed.InstallationId ||
            current.InstallRoot != reviewed.InstallRoot || current.DataRoot != reviewed.DataRoot ||
            current.Version != reviewed.Version || current.PreviousVersion != reviewed.PreviousVersion || current.ListenUrl != reviewed.ListenUrl)
            throw new InvalidDataException("The local installation changed; refresh and review it again.");
        var options = new ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback,
            retention, Mode, ExpectedInstallationId: current.InstallationId,
            Confirmed: true, Language: language, InstallRoot: current.InstallRoot, DataRoot: current.DataRoot,
            CertificateMode: regenerateSelfSignedCertificate ? ServerCertificateMode.SelfSigned : null,
            SelfSignedIdentities: regenerateSelfSignedCertificate ? selfSignedIdentities!.Trim() : null,
            AddFirewallRule: repairFirewall);
        var completed = await ExecuteRecordedAsync(session, new(ServerDeploymentProtocol.Version, Guid.NewGuid(), kind, options), cancellationToken)
            .ConfigureAwait(false);
        LastFirewallStatus = completed.Result?.FirewallStatus;
        var after = await ReadStatusAsync(session, cancellationToken).ConfigureAwait(false);
        if (kind == ServerDeploymentKind.Uninstall ? after.Installed :
            !after.Installed || !after.Healthy || after.InstallationId != current.InstallationId)
            throw new InvalidDataException("Local lifecycle status verification failed.");
        return after;
    }

    private async Task<ServerHostSnapshotDto> ReadStatusAsync(ILocalWindowsDeploymentSession session, CancellationToken ct)
    {
        var receipt = await ExecuteRecordedAsync(session, new(ServerDeploymentProtocol.Version, Guid.NewGuid(),
            ServerDeploymentKind.Status, new(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback,
                Mode: Mode)), ct).ConfigureAwait(false);
        var snapshot = receipt.Snapshot ?? throw new InvalidDataException("Missing local installation status.");
        if (snapshot.Installed && (snapshot.Mode != Mode ||
            !ServerInstallationId.IsValid(snapshot.InstallationId)))
            throw new InvalidDataException("Invalid local installation identity or mode.");
        return snapshot;
    }

    private async Task<ServerDeploymentOperationDto> ExecuteRecordedAsync(ILocalWindowsDeploymentSession session,
        ServerDeploymentRequest request, CancellationToken ct)
    {
        LastOperationId = request.OperationId;
        var now = DateTimeOffset.UtcNow;
        await journal.RecordAsync(new(request.OperationId, JournalHostId, request.Kind, ServerDeploymentState.Queued,
            ServerDeploymentPhase.Queued, 0, null, null, null, now, null, now), ct).ConfigureAwait(false);
        var receipt = await session.ExecuteAsync(request, null, null, null, null, ct).ConfigureAwait(false);
        await journal.RecordAsync(ServerCenterOperationRecord.From(JournalHostId, receipt), ct).ConfigureAwait(false);
        if (receipt.State != ServerDeploymentState.Succeeded) throw new LocalWindowsDeploymentFailedException(receipt);
        return receipt;
    }

    public async Task<string> InstallAsync(ServerInstallationOptions installation, string language,
        IProgress<string>? stage, IProgress<ServerDeploymentTransfer?>? transfer, CancellationToken cancellationToken = default)
    {
        LastOperationId = null;
        if (installation.Mode is not (ServerInstallMode.WindowsSystem or ServerInstallMode.WindowsUser) || installation.Source == ServerPackageSourceKind.RemoteBundle)
            throw new ArgumentException("Local installation requires a Windows mode and a local or HTTPS source.");

        Mode = installation.Mode!.Value;
        if (Mode == ServerInstallMode.WindowsUser && (
            !string.IsNullOrWhiteSpace(installation.InstallRoot) || !string.IsNullOrWhiteSpace(installation.DataRoot)))
            throw new ArgumentException("Personal installation uses fixed per-user directories.");
        // Read the user's files before elevation, including when UAC uses a different administrator account.
        await using var archive = installation.Source == ServerPackageSourceKind.LocalBundle
            ? File.OpenRead(installation.LocalBundlePath ?? throw new ArgumentException("A ZIP is required.")) : null;
        using var preparedCertificate = PrepareCertificate(installation);
        stage?.Report(Mode == ServerInstallMode.WindowsSystem ? "elevating" : "checking");
        await using var session = await sessions.OpenAsync(Mode, cancellationToken).ConfigureAwait(false);

        async Task<ServerDeploymentOperationDto> Execute(ServerDeploymentRequest request, Stream? bundle = null,
            Stream? certificate = null, string? password = null)
        {
            // Index the id before dispatch so an interrupted connection leaves a recoverable operation reference.
            LastOperationId = request.OperationId;
            var now = DateTimeOffset.UtcNow;
            await journal.RecordAsync(new(request.OperationId, JournalHostId, request.Kind, ServerDeploymentState.Queued,
                ServerDeploymentPhase.Queued, 0, null, null, null, now, null, now), cancellationToken).ConfigureAwait(false);
            var receipt = await session.ExecuteAsync(request, bundle, certificate, password, transfer, cancellationToken).ConfigureAwait(false);
            await journal.RecordAsync(ServerCenterOperationRecord.From(JournalHostId, receipt), cancellationToken).ConfigureAwait(false);
            if (receipt.State != ServerDeploymentState.Succeeded) throw new LocalWindowsDeploymentFailedException(receipt);
            return receipt;
        }

        stage?.Report("checking");
        var probeReceipt = await Execute(new(ServerDeploymentProtocol.Version, Guid.NewGuid(), ServerDeploymentKind.Probe,
            new(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, Mode: Mode)));
        var probe = probeReceipt.Probe ?? throw new InvalidDataException("Missing local preflight.");
        if (probe.HostPlatform != HostPlatformKind.Windows || Mode == ServerInstallMode.WindowsSystem && !probe.Elevated ||
            Mode == ServerInstallMode.WindowsUser && probe.Elevated || !probe.OsSupported ||
            probe.RuntimeIdentifier is not (ServerRuntimeIdentifier.WinX64 or ServerRuntimeIdentifier.WinArm64))
            throw new InvalidDataException("Unsupported or unelevated local Windows host.");
        if (probe.ExistingInstalled && (probe.ExistingMode != Mode ||
            !ServerInstallationId.IsValid(probe.ExistingInstallationId)))
            throw new InvalidDataException("The existing installation identity is invalid.");

        var kind = probe.ExistingInstalled ? ServerDeploymentKind.Upgrade : ServerDeploymentKind.Install;
        if (archive is not null && await releases.ResolveLocalBundleAsync(HostPlatformKind.Windows,
            probe.RuntimeIdentifier.Value, Mode, installation.LocalBundlePath!, cancellationToken)
            .ConfigureAwait(false) is null) throw new InvalidDataException("Unavailable local release bundle.");

        var options = new ServerDeploymentOptions(installation.Source, installation.Network,
            Mode: Mode,
            PackageUri: installation.PackageUri, PackageDigest: installation.PackageDigest,
            StagedPackageName: archive is null ? null : "server.zip",
            ExpectedInstallationId: probe.ExistingInstalled ? probe.ExistingInstallationId : null,
            ServerPort: installation.ServerPort, FileAccess: installation.FileAccess,
            CertificateMode: installation.CertificateMode, SelfSignedIdentities: installation.SelfSignedIdentities,
            Confirmed: true, Language: language, ReleaseCatalogBaseUri: installation.ReleaseCatalogBaseUri,
            InstallRoot: installation.InstallRoot, DataRoot: installation.DataRoot, FileRoots: installation.FileRoots,
            AddFirewallRule: installation.Network == ServerNetworkProfile.Lan && installation.AddFirewallRule);
        stage?.Report(kind == ServerDeploymentKind.Upgrade ? "upgrading" : "installing");
        var installed = await Execute(new(ServerDeploymentProtocol.Version, Guid.NewGuid(), kind, options), archive,
            preparedCertificate, installation.CertificatePassword);
        if (installed.Result is not { Healthy: true } result || result.Mode != Mode ||
            !ServerInstallationId.IsValid(result.InstallationId))
            throw new InvalidDataException("The installation receipt has no healthy managed result.");

        stage?.Report("verifying");
        var status = await Execute(new(ServerDeploymentProtocol.Version, Guid.NewGuid(), ServerDeploymentKind.Status,
            new(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, Mode: Mode)));
        var endpoint = VerifiedLocalEndpoint(result.InstallationId!, status.Snapshot);
        LastVerifiedSnapshot = status.Snapshot;
        return endpoint;
    }

    public static string VerifiedLocalEndpoint(string installationId, ServerHostSnapshotDto? snapshot)
    {
        if (snapshot is not { Installed: true, Healthy: true, Mode: ServerInstallMode.WindowsSystem or ServerInstallMode.WindowsUser } ||
            snapshot.InstallationId != installationId || !ServerInstallationId.IsValid(installationId) ||
            !Uri.TryCreate(snapshot.ListenUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !(uri.IsLoopback || uri.Host == "0.0.0.0" || uri.Host == "[::]"))
            throw new InvalidDataException("The local installation status is not healthy or has no valid listening address.");
        return new UriBuilder(uri) { Host = uri.IsLoopback ? uri.Host : "127.0.0.1" }.Uri.GetLeftPart(UriPartial.Authority);
    }

    private static Stream? PrepareCertificate(ServerInstallationOptions installation)
    {
        if (installation.CertificateMode != ServerCertificateMode.Custom) return null;
        if (installation.CertificateFormat == ServerCertificateFormat.Pfx) return File.OpenRead(installation.CertificatePath!);
        var chain = new X509Certificate2Collection();
        try
        {
            chain.ImportFromPemFile(installation.CertificatePath!);
            using var leaf = X509Certificate2.CreateFromPemFile(installation.CertificatePath!, installation.CertificatePrivateKeyPath!);
            foreach (var existing in chain.Cast<X509Certificate2>().Where(c => c.Thumbprint == leaf.Thumbprint).ToArray())
            {
                chain.Remove(existing);
                existing.Dispose();
            }
            chain.Add(leaf);
            return new MemoryStream(chain.Export(X509ContentType.Pkcs12, installation.CertificatePassword)!, writable: false);
        }
        finally { foreach (var certificate in chain) certificate.Dispose(); }
    }
}

public sealed class LocalWindowsDeploymentFailedException(ServerDeploymentOperationDto receipt)
    : Exception("The local deployment launcher reported failure.")
{
    public ServerDeploymentOperationDto Receipt { get; } = receipt;
}
