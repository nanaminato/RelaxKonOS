using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Databases;

[JsonConverter(typeof(JsonStringEnumConverter<MySqlAction>))]
public enum MySqlAction { Install, Initialize, Start, Stop, Verify, CreateDatabase, CreateAccount, SetGrant, ChangePassword, RecoverAdministrator }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MySqlRequest(bool Confirmed, MySqlAction? Action = null, long Revision = 0, Guid? RecoverOperationId = null, MySqlAccountCommand? Account = null, Guid? AdministratorInstanceId = null)
{
    [JsonIgnore]
    public bool Valid => Confirmed && (RecoverOperationId is { } id
        ? id != Guid.Empty && Action is null && Revision == 0 && Account is null && AdministratorInstanceId is null
        : Action is { } action && Enum.IsDefined(action) && Revision > 0 && (action == MySqlAction.RecoverAdministrator
            ? Account is null && AdministratorInstanceId is { } target && target != Guid.Empty
            : AdministratorInstanceId is null && (action >= MySqlAction.CreateDatabase ? Account?.Valid(action) == true : Account is null)));
}

public sealed record MySqlState(bool Supported, long Revision, bool Installed, Guid? InstanceId,
    bool Running, bool Verified, Guid? PendingOperationId, Guid? CommittedOperationId, string? ProblemCode, IReadOnlyList<string>? Databases = null, IReadOnlyList<MySqlAccount>? Accounts = null,
    MySqlAccountCommand? PendingAccount = null, bool PendingAdministratorRecovery = false);

public static class MySqlRoutes
{
    public const string Credentials = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/databases/mysql/credentials";
    public const string State = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/databases/mysql";
}

[JsonConverter(typeof(JsonStringEnumConverter<MySqlGrant>))]
public enum MySqlGrant { ReadOnly, ReadWrite }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MySqlAccountCommand(Guid InstanceId, string Database, string? User = null, string? Host = null,
    MySqlGrant? Grant = null, Guid? CredentialReference = null)
{
    public bool Valid(MySqlAction action) => InstanceId != Guid.Empty && Name(Database) &&
        (action == MySqlAction.CreateDatabase
            ? User is null && Host is null && Grant is null && CredentialReference is null
            : Name(User) && Host == "localhost" && (action == MySqlAction.ChangePassword ? Grant is null :
                Grant is { } grant && Enum.IsDefined(grant)) &&
                (action == MySqlAction.SetGrant ? CredentialReference is null : CredentialReference is { } id && id != Guid.Empty));

    // Restrict database names so GRANT schema patterns cannot contain SQL wildcards.
    public static bool Name(string? value) => value is { Length: >= 1 and <= 32 } &&
        value[0] is >= 'a' and <= 'z' && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9') &&
        value is not ("root" or "mysql" or "sys" or "information_schema" or "performance_schema");

    public static bool Password(string? value) => value is { Length: >= 16 and <= 128 } &&
        value.All(c => c is >= '!' and <= '~' && c is not ('\'' or '"' or '\\'));
}

public sealed record MySqlAccount(string Database, string User, string Host, MySqlGrant Grant,
    Guid CredentialReference, long CredentialRevision, Guid OwnershipId)
{
    public string Identity => User + "@" + Host;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MySqlSecrets(string Password, string? PreviousPassword = null)
{
    public override string ToString() => "MySqlSecrets [redacted]";
}
