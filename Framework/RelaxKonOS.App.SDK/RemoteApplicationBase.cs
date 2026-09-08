using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.AppSDK;

/// <summary>Convenience base class for <see cref="IRemoteApplication"/> implementations.</summary>
public abstract class RemoteApplicationBase : IRemoteApplication
{
    public abstract ApplicationManifest Manifest { get; }

    public virtual void Activate(AppContext context) { }
}
