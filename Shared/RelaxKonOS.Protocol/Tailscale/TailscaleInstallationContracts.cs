using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Protocol.Tailscale;

public sealed record TailscaleInstallerSnapshot(ComponentStateView View, Guid? VerifiedOperationId = null,
    ComponentDownloadRelease? InstalledRelease = null);
