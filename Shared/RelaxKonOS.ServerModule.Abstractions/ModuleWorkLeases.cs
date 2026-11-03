namespace RelaxKonOS.Server.Residency;

public interface IModuleWorkLeaseSource
{
    IModuleWorkLease Acquire();
}
public interface IModuleWorkLease : IDisposable
{
    CancellationToken Stopping { get; }
    void RequireRecovery();
}
