using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>
/// Helper-owned durable management authorization. <paramref name="OperationId"/> is the first explicit
/// registration or installation that granted management and never changes; <paramref name="Identity"/>
/// is the most recently authorized verified identity. An external package-manager update changes the
/// observed identity but never rewrites this record, so it can be reported as a verified change and is
/// never mistaken for the original operation's success receipt.
/// </summary>
public sealed record ComponentPackageRegistration(Guid OperationId, string ComponentId, ComponentInstalledPackage Identity)
{
    public bool Valid(string componentId, string packageName) =>
        OperationId != Guid.Empty && ComponentId == componentId
        && Identity.PackageName == packageName && PackageIdentityPolicy.Valid(Identity);

    /// <summary>Explicit "manage this package" confirmation. It records the identity and changes nothing on the host.</summary>
    public static ComponentPackageRegistration Authorize(Guid operationId, string componentId, ComponentInstalledPackage identity)
    {
        if (operationId == Guid.Empty || HostPackageDefinitions.Find(componentId) is not { } definition
            || identity.PackageName != definition.PackageName || !PackageIdentityPolicy.Valid(identity))
            throw new InvalidOperationException("Invalid package authorization.");
        return new(operationId, componentId, identity);
    }

    /// <summary>
    /// Re-confirms the same package and architecture after the package was verified again. The first
    /// authorization operation is preserved; only the recorded identity advances.
    /// </summary>
    public ComponentPackageRegistration Acknowledge(ComponentInstalledPackage identity)
    {
        if (!PackageIdentityPolicy.SamePackage(Identity, identity) || !PackageIdentityPolicy.Valid(identity))
            throw new InvalidOperationException("Acknowledged identity is not the authorized package.");
        return this with { Identity = identity };
    }
}
