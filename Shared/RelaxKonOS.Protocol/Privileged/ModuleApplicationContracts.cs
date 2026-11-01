namespace RelaxKonOS.Protocol.Privileged;

/// <summary>Closed application-management verbs. Credential recovery waits for its own service-stop,
/// restricted-start and compensation design and is not executable here.</summary>
public enum ModuleApplicationAction
{
    Query,
    RotateCredential,
}

/// <summary>
/// Frozen, non-secret view of one Host-configured database instance. It names the account and its
/// secret revision, never a password. The Helper produces it; a module cannot supply any of it.
/// </summary>
public sealed record ModuleApplicationState(
    string Instance,
    string Protocol,
    string Account,
    string AccountHost,
    bool AccountExists,
    string SecretRevision,
    string ServerVersion,
    string PolicyRevision);

/// <summary>Read-only protocol question about one Host-approved application instance.</summary>
public sealed record ModuleApplicationQuery(string Instance, ModuleApplicationAction Action);

/// <summary>
/// One confirmed application action. The Helper reads the actual credential from its own root-owned
/// store; no secret value or secret path crosses this contract.
/// </summary>
public sealed record ModuleApplicationTransaction(
    ModuleApplicationAction Action,
    string Instance,
    ModuleApplicationState Expected);

/// <summary>Shape rules shared by the Host, the Helper and tests. They describe values, not authority.</summary>
public static class ModuleApplicationIdentity
{
    public static bool Identifier(string? value) => value is { Length: > 0 and <= 64 }
        && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');

    public static bool MySQLAccount(string? value) => value is { Length: > 0 and <= 64 }
        && value[0] != '-' && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static bool MySQLHost(string? value) => value is { Length: > 0 and <= 255 }
        && value[0] != '-' && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '%' or ':');

    public static bool Revision(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigitLower);

    public static bool ServerVersion(string? value) => value is { Length: > 0 and <= 128 } && !value.Any(char.IsControl);

    public static bool Usable(ModuleApplicationState? state) => state is not null
        && Identifier(state.Instance) && state.Protocol == "mysql-admin"
        && MySQLAccount(state.Account) && MySQLHost(state.AccountHost)
        && state.AccountExists && Revision(state.SecretRevision) && ServerVersion(state.ServerVersion)
        && state.PolicyRevision is { Length: 64 } && state.PolicyRevision.All(char.IsAsciiHexDigitLower);
}
