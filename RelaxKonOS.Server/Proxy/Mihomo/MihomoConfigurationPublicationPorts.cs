using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Application configuration publication with the existing publication gate held by the caller.</summary>
public interface IMihomoApplicationConfigurationPublisher
{
    Task<MihomoPublicationResult> PublishUnderGateAsync(Guid operationId, long expectedPublicationRevision,
        RuntimeConfigurationPlan plan, MihomoPublishedRuntime candidate, CancellationToken ct,
        MihomoSettingsPublication? settings = null, MihomoProfilePublication? profile = null);
}

/// <summary>Recovery of a previously accepted configuration publication under its original identity.</summary>
public interface IMihomoConfigurationPublicationRecovery
{
    // The Core hosted scan owns this call and drains it with its recovery scope.
    Task<MihomoPublicationResult> RecoverAsync(Guid operationId, CancellationToken ct);
    Task<MihomoPublicationResult> RecoverUnderGateAsync(MihomoPublicationState state, CancellationToken ct);
}
