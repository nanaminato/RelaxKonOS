using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal static class PrivilegedOperationReplayPolicy
{
    // These verbs share a durable operation identity. The protected domain ledger owns replay safety.
    internal static bool UsesDurableReceipt(PrivilegedOperationKind operation) => operation is
        PrivilegedOperationKind.ComponentPackageQuery or PrivilegedOperationKind.ComponentPackageInstall
        or PrivilegedOperationKind.ComponentPackageChange or PrivilegedOperationKind.ComponentPackageRecover or
        PrivilegedOperationKind.TailscaleInstallerQuery or PrivilegedOperationKind.TailscaleInstallerExecute or PrivilegedOperationKind.TailscaleInstallerRecover or
        PrivilegedOperationKind.TailscaleControlExecute or PrivilegedOperationKind.TailscaleControlReceipt
        or PrivilegedOperationKind.TailscaleRecoveryRead or PrivilegedOperationKind.TailscaleRecoveryClose
        or PrivilegedOperationKind.TailscaleRecoveryReceipt or PrivilegedOperationKind.TailscaleLoginBegin
        or PrivilegedOperationKind.TailscaleLoginReceipt or PrivilegedOperationKind.TailscaleLoginChallenge
        or PrivilegedOperationKind.TailscaleLoginRecoveryRead or PrivilegedOperationKind.TailscaleLoginClose
        or PrivilegedOperationKind.TailscaleLoginRecoveryReceipt
        // Receipt observations reuse the publication ID before and after the mutation.
        or PrivilegedOperationKind.MihomoRuntimeQuery or PrivilegedOperationKind.ProxyServerPublicationReceipt
        or PrivilegedOperationKind.MihomoRuntimeTunStop
        or PrivilegedOperationKind.MihomoRuntimeTunStart or PrivilegedOperationKind.MihomoRuntimeTunRestart
        or PrivilegedOperationKind.ProxyServerRecoverPublication
        or PrivilegedOperationKind.NginxTrackedReload or PrivilegedOperationKind.NginxTrackedLifecycle
        or PrivilegedOperationKind.NginxReloadReceipt
        or PrivilegedOperationKind.NginxRetireReloadReceipt
        or PrivilegedOperationKind.LinuxSystemProxyApplyTracked or PrivilegedOperationKind.LinuxSystemProxyReceipt
        or PrivilegedOperationKind.SquidApplyConfiguration or PrivilegedOperationKind.SquidConfigurationReceipt
        or PrivilegedOperationKind.SquidTrackedLifecycle or PrivilegedOperationKind.SquidLifecycleReceipt;
}
