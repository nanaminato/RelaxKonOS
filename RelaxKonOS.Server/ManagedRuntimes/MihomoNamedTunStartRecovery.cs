using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.ManagedRuntimes;

/// <summary>Resumes the original named start only after the queue has verified its pending receipt.</summary>
internal sealed class MihomoNamedTunStartRecovery(MihomoPublicationStore store, IPrivilegedOperationTransport helper,
    IServerModeResolver mode, MihomoRuntimeAuthorizationProvider authorization)
{
    internal async Task<bool> TryRecoverAsync(Guid executionId, Guid publicationId, CancellationToken ct)
    {
        // The queue gate is already held. A live publisher may still need to report progress there.
        if (!await store.Gate.WaitAsync(0, ct)) return false;
        try
        {
            await MihomoRuntimeHelperProtocol.RecoverUnderGateAsync(helper, mode, authorization, Guid.NewGuid(), executionId, CancellationToken.None, publicationId);
            return true;
        }
        finally { store.Gate.Release(); }
    }
}
