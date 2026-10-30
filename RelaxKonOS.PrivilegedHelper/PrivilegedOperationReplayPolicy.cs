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
        or PrivilegedOperationKind.TailscaleLoginRecoveryReceipt;
}
