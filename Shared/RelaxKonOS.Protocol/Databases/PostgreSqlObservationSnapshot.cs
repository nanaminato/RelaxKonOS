using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Protocol.Databases;

/// <summary>Private broker inputs for the isolated PostgreSQL state endpoint.</summary>
public sealed record PostgreSqlObservationSnapshot([property: JsonRequired] PostgreSqlState State,
    InstallationOperationDto? PendingBackup, InstallationOperationDto? CurrentSlot, InstallationOperationDto? PreviousBackup);
