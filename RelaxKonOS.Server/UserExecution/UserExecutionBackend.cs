namespace RelaxKonOS.Server.UserExecution;

/// <summary>
/// Which process performs an ordinary (non-elevated) file, Git or terminal operation for the
/// authenticated user. This is a deployment decision, exactly like <c>Server:Mode</c>: it is read
/// from configuration once and then used for every decision.
/// </summary>
public enum UserExecutionBackend
{
    /// <summary>
    /// The installed Helper. On Windows the LocalSystem Helper serves the
    /// <c>&lt;pipeName&gt;-user</c> pipe and impersonates the target account through a one-shot S4U
    /// token; on Linux the Server starts a root-owned Helper per request, which irreversibly drops
    /// to the target UID. This is the only backend that can act as a <em>different</em> account, and
    /// the only one the installer ever writes.
    /// </summary>
    Helper,

    /// <summary>
    /// In-process, performed by the Server's own OS identity. Permitted only while the resolved
    /// effective identity is that same identity, so access control is still decided by the target
    /// account (the Server process <em>is</em> that account). Intended for local debugging, where
    /// installing the platform service is not worthwhile; see
    /// <c>docs/development/RelaxKonOS.LocalDebugging.md</c>.
    /// </summary>
    LocalIdentity,

    /// <summary>
    /// Explicitly closed. Every request fails closed with
    /// <c>UserExecutionProblemCode.HelperUnavailable</c>; nothing runs, which is what the Server
    /// account must never silently stand in for.
    /// </summary>
    Disabled,
}

/// <summary>
/// The <see cref="UserExecutionBackend"/> resolved once at startup, published as a service so the
/// terminal factory and the Git domain share the single validated decision instead of re-reading
/// configuration. The enum is wrapped because a value type cannot itself be a service instance.
/// </summary>
public sealed record UserExecutionBackendSelection(UserExecutionBackend Backend);

/// <summary>
/// Resolves <see cref="UserExecutionBackend"/> from <c>PrivilegedHelper:UserExecutionBackend</c>.
/// </summary>
public static class UserExecutionBackendResolver
{
    public const string ConfigurationKey = "PrivilegedHelper:UserExecutionBackend";

    /// <summary>
    /// Reads and validates the configured backend. An unknown value is a configuration error rather
    /// than a silent fallback, and the debugging backend is refused outright in Production: a
    /// misconfigured deployment must fail at startup, not quietly run without the effective-user
    /// boundary.
    /// </summary>
    public static UserExecutionBackend Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[ConfigurationKey]?.Trim().ToLowerInvariant();
        var backend = configured switch
        {
            null or "" or "helper" => UserExecutionBackend.Helper,
            "local-identity" => UserExecutionBackend.LocalIdentity,
            "disabled" => UserExecutionBackend.Disabled,
            _ => throw new InvalidOperationException(
                $"{ConfigurationKey} must be 'helper', 'local-identity' or 'disabled'."),
        };
        if (backend != UserExecutionBackend.LocalIdentity) return backend;
        if (environment.IsProduction())
            throw new InvalidOperationException(
                $"{ConfigurationKey}=local-identity executes ordinary user operations as the Server's own "
                + "account. It exists for local debugging and must not be enabled in Production.");
        // A root Server matches no eligible account, so every request would be refused anyway; failing
        // at startup says why instead of leaving a confusing per-request denial. It also keeps the
        // debugging backend from ever being a way to run the whole Server privileged.
        if (ServerProcessIdentity.IsPrivileged())
            throw new InvalidOperationException(
                $"{ConfigurationKey}=local-identity must not run as root. Start the Server as the account you "
                + "are debugging with.");
        return backend;
    }
}
