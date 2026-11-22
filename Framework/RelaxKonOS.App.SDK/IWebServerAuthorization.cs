using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.AppSDK;

/// <summary>Runs an instance-scoped operation after the host has authorized it.</summary>
public interface IWebServerAuthorization
{
    Task<T> ExecuteAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> operation,
        CancellationToken cancellationToken = default);
}
