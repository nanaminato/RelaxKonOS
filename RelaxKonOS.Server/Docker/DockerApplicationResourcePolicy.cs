using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.ApplicationDeployments;

namespace RelaxKonOS.Server.Docker;

/// <summary>Protects the namespaces allocated by application deployment from direct Docker Manager writes.</summary>
internal static class DockerApplicationResourcePolicy
{
    public const string ManagedResource = "docker.application_resource_managed";
    public const string ComposeProjectPrefix = "relaxkonos-ca-";

    public static bool IsReservedName(string? name) => name is not null
        && (name.StartsWith(ApplicationDeploymentValidation.ResourcePrefix, StringComparison.Ordinal)
            || name.StartsWith(ComposeProjectPrefix, StringComparison.Ordinal));

    public static void RequireUnmanagedProject(string name)
    {
        if (IsReservedName(name)) throw new DockerStackException(ManagedResource);
    }

    public static async Task<DockerOperationResult> DeleteVolumeAsync(IDockerEngineService engine, string name, bool confirmed, CancellationToken ct)
    {
        if (!confirmed) return new(false, "docker.confirmation_required");
        if (IsReservedName(name)) return new(false, ManagedResource);
        var volume = await engine.GetVolumeAsync(name, ct);
        if (volume is null) return new(false, "docker.operation_failed");
        if (volume.Labels.TryGetValue(ApplicationDeploymentValidation.OwnerLabel, out var owner)
            && owner == ApplicationDeploymentValidation.OwnerValue)
            return new(false, ManagedResource);
        return await engine.DeleteVolumeAsync(volume.Name, confirmed, ct);
    }
}
