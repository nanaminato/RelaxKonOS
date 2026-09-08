namespace RelaxKonOS.Server.WebServer;

/// <summary>
/// Compatibility gate for legacy callers. Server process identity is never elevation authority:
/// privileged work must use a capability-authorized Helper transport even during root/Admin
/// development. Callers not yet migrated therefore fail closed.
/// </summary>
public interface IHostPrivilegeService
{
    bool IsAdministrator { get; }
}

public sealed class HostPrivilegeService : IHostPrivilegeService
{
    public bool IsAdministrator => false;
}
