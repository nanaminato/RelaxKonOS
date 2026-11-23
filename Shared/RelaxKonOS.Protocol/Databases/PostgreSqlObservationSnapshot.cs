using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Protocol.Databases;

/// <summary>Helper state and the bounded backup ledger candidates for projecting PostgreSQL state.</summary>
public sealed record PostgreSqlObservationSnapshot([property: JsonRequired] PostgreSqlState State,
    InstallationOperationDto? PendingBackup, InstallationOperationDto? CurrentSlot, InstallationOperationDto? PreviousBackup)
{
    public PostgreSqlState ToPublicState() =>
        State with { ScheduledOperation = PendingBackup ?? CurrentSlot ?? PreviousBackup };
}
