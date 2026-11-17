using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Protocol.Databases;

[JsonConverter(typeof(JsonStringEnumConverter<PostgreSqlAction>))]
public enum PostgreSqlAction { Initialize, Start, Stop, Verify }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PostgreSqlCommand(PostgreSqlAction Action, Guid InstanceId, long ExpectedRevision, int Port = 5432);

public sealed record PostgreSqlState(bool Supported, bool Installed, bool Managed, string? Version,
    long Revision, Guid? InstanceId, int Port, bool Running, bool ConnectionVerified,
    Guid? PendingOperationId, Guid? CommittedOperationId, string? ProblemCode,
    ComponentPackageTarget? Candidate = null, IReadOnlyList<PostgreSqlDatabase>? Databases = null,
    IReadOnlyList<PostgreSqlAccount>? Accounts = null, PostgreSqlAccountCommand? PendingAccount = null,
    PostgreSqlAdminRecoveryCommand? PendingAdminRecovery = null, PostgreSqlRemoteCommand? RemoteAccess = null, PostgreSqlRemoteCommand? PendingRemoteAccess = null, PostgreSqlMaintenanceCommand? PendingMaintenance = null,
    IReadOnlyList<PostgreSqlBackup>? Backups = null, bool PackageRemoved = false, bool DataDeleted = false, PostgreSqlBackupSchedule? BackupSchedule = null, RelaxKonOS.Protocol.Installations.InstallationOperationDto? ScheduledOperation = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PostgreSqlInstallationRequest(bool Confirmed,
    ComponentExecuteRequest? FrozenPackage = null, PostgreSqlCommand? Command = null, Guid? RecoverOperationId = null, PostgreSqlAccountCommand? AccountCommand = null,
    PostgreSqlAdminRecoveryCommand? AdminRecovery = null, PostgreSqlRemoteCommand? RemoteAccess = null, PostgreSqlMaintenanceCommand? Maintenance = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PostgreSqlAdminRecoveryCommand(Guid InstanceId, long ExpectedRevision);

public static class PostgreSqlRoutes
{
    public const string MigrationPlan = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/databases/postgresql/migration-plan";
    public const string MaintenancePlan = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/databases/postgresql/maintenance-plan";
    public const string Credentials = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/databases/postgresql/credentials";
    public const string State = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/databases/postgresql";
}

[JsonConverter(typeof(JsonStringEnumConverter<PostgreSqlAccountAction>))]
public enum PostgreSqlAccountAction { CreateDatabase, CreateAccount, SetGrant, ChangePassword }
[JsonConverter(typeof(JsonStringEnumConverter<PostgreSqlPermission>))]
public enum PostgreSqlPermission { ReadOnly, ReadWrite, Migration }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PostgreSqlAccountCommand(PostgreSqlAccountAction Action, Guid InstanceId, long ExpectedRevision,
    string Database, string? Account = null, PostgreSqlPermission? Permission = null, Guid? CredentialReference = null);
public sealed record PostgreSqlDatabase(string Name, uint Oid, string Owner, uint OwnerOid);
public sealed record PostgreSqlAccount(string Database, string Name, uint Oid, PostgreSqlPermission Permission,
    Guid CredentialReference, long CredentialRevision, bool ApplicationsNeedUpdate);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PostgreSqlCredentialRequest(Guid Reference, Guid InstanceId, string Password)
{ public override string ToString() => "PostgreSqlCredentialRequest [redacted]"; }
public sealed record PostgreSqlCredentialReference(Guid Reference);
public sealed record PostgreSqlSecrets(string Password, string? PreviousPassword = null)
{ public override string ToString() => "PostgreSqlSecrets [redacted]"; }
public static class PostgreSqlAccountValidation
{
    public static bool Name(string? value) => value is { Length: >= 1 and <= 40 }
        && value[0] is >= 'a' and <= 'z' && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
        && !value.StartsWith("pg_", StringComparison.Ordinal) && !value.StartsWith("rk_", StringComparison.Ordinal)
        && value is not ("postgres" or "template0" or "template1" or "public");
    // ASCII avoids ambiguous SASLprep normalization; passwords are never SQL literals or argv values.
    public static bool Password(string? value) => value is { Length: >= 16 and <= 128 } && value.All(c => c is >= '!' and <= '~');
    public static bool Valid(PostgreSqlAccountCommand c) => Enum.IsDefined(c.Action) && c.InstanceId != Guid.Empty && c.ExpectedRevision > 0 && Name(c.Database)
        && (c.Action == PostgreSqlAccountAction.CreateDatabase ? c.Account is null && c.Permission is null && c.CredentialReference is null
            : Name(c.Account) && (c.Action == PostgreSqlAccountAction.ChangePassword ? c.Permission is null : c.Permission is { } p && Enum.IsDefined(p))
                && (c.Action == PostgreSqlAccountAction.SetGrant ? c.CredentialReference is null : c.CredentialReference is { } id && id != Guid.Empty));
}
