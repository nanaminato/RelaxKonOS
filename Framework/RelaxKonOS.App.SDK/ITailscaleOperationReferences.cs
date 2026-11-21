namespace RelaxKonOS.AppSDK;

/// <summary>Device-local operation IDs. The server remains authoritative for outcomes.</summary>
public interface ITailscaleOperationReferences
{
    IReadOnlyList<Guid> Read(HostSettingsConnection connection);
    void Remember(HostSettingsConnection connection, Guid operationId);
}
