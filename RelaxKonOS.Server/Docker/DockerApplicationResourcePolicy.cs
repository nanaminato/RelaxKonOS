using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.ApplicationDeployments;

namespace RelaxKonOS.Server.Docker;

/// <summary>Protects the namespaces allocated by application deployment from direct Docker Manager writes.</summary>
internal static class DockerApplicationResourcePolicy
{
    public const string ManagedResource = DockerStackProblem.ApplicationResourceManaged;

    public static bool IsReservedName(string? name) => DockerStackValidation.IsApplicationResourceName(name);

    public static void RequireUnmanagedProject(string name)
    {
        if (IsReservedName(name)) throw new DockerStackException(ManagedResource);
    }

    public static bool IsManagedContainer(DockerContainerDetailsDto container) => IsManaged(container.Name, container.Labels);

    public static async Task<DockerOperationResult> ChangeContainerAsync(IDockerEngineService engine, string id,
        string? newName, Func<string, Task<DockerOperationResult>> change, CancellationToken ct)
    {
        if (IsReservedName(id) || IsReservedName(newName)) return new(false, ManagedResource);
        var container = await engine.GetContainerAsync(id, ct);
        if (container is null || string.IsNullOrEmpty(container.Id) || string.IsNullOrEmpty(container.Name))
            return new(false, "docker.operation_failed");
        if (IsManagedContainer(container)) return new(false, ManagedResource);
        return await change(container.Id);
    }

    public static async Task<DockerOperationResult> CreateContainerAsync(IDockerEngineService engine,
        DockerContainerCreateRequest request, CancellationToken ct)
    {
        if (IsReservedName(request.Name) || IsReservedName(request.Network) || HasReservedLabel(request.Labels))
            return new(false, ManagedResource);
        if (!string.IsNullOrWhiteSpace(request.Network) && request.Network is not ("default" or "bridge" or "host" or "none"))
        {
            var network = await engine.GetNetworkAsync(request.Network, ct);
            if (network is null || string.IsNullOrEmpty(network.Id) || string.IsNullOrEmpty(network.Name))
                return new(false, "docker.operation_failed");
            if (IsManaged(network.Name, network.Labels)) return new(false, ManagedResource);
            request = request with { Network = network.Id };
        }

        IReadOnlyList<DockerVolumeDto>? volumes = null;
        foreach (var mount in request.Mounts ?? [])
        {
            if (string.IsNullOrEmpty(mount)) return new(false, "docker.validation_failed");
            // A single absolute path requests an anonymous volume. Any explicit source must
            // be a Docker volume name. Host paths can reach the Docker socket or daemon data.
            var separator = mount.IndexOf(':');
            if (separator < 0 && mount.StartsWith('/')) continue;
            if (separator <= 0) return new(false, "docker.validation_failed");
            var source = mount[..separator];
            if (IsReservedName(source)) return new(false, ManagedResource);
            if (source.Length < 2 || !char.IsAsciiLetterOrDigit(source[0])
                || source.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
                return new(false, "docker.validation_failed");
            volumes ??= await engine.ListVolumesAsync(ct);
            if (volumes.Any(volume => volume.Name.Equals(source, StringComparison.OrdinalIgnoreCase)))
            {
                var existing = await engine.GetVolumeAsync(source, ct);
                if (existing is null) return new(false, "docker.operation_failed");
                if (IsManaged(existing.Name, existing.Labels)) return new(false, ManagedResource);
            }
        }

        return await engine.CreateContainerAsync(request, ct);
    }

    public static Task<DockerOperationResult> CreateNetworkAsync(IDockerEngineService engine,
        DockerNetworkCreateRequest request, CancellationToken ct) => IsReservedName(request.Name)
        ? Task.FromResult(new DockerOperationResult(false, ManagedResource))
        : engine.CreateNetworkAsync(request, ct);

    public static Task<DockerOperationResult> CreateVolumeAsync(IDockerEngineService engine,
        DockerVolumeCreateRequest request, CancellationToken ct) => IsReservedName(request.Name) || HasReservedLabel(request.Labels)
        ? Task.FromResult(new DockerOperationResult(false, ManagedResource))
        : engine.CreateVolumeAsync(request, ct);

    public static async Task<DockerOperationResult> DeleteNetworkAsync(IDockerEngineService engine, string id, bool confirmed, CancellationToken ct)
    {
        if (!confirmed) return new(false, "docker.confirmation_required");
        if (IsReservedName(id)) return new(false, ManagedResource);
        var network = await engine.GetNetworkAsync(id, ct);
        if (network is null || string.IsNullOrEmpty(network.Id) || string.IsNullOrEmpty(network.Name))
            return new(false, "docker.operation_failed");
        if (IsManaged(network.Name, network.Labels)) return new(false, ManagedResource);
        return await engine.DeleteNetworkAsync(network.Id, confirmed, ct);
    }

    public static async Task<DockerOperationResult> DeleteVolumeAsync(IDockerEngineService engine, string name, bool confirmed, CancellationToken ct)
    {
        if (!confirmed) return new(false, "docker.confirmation_required");
        if (IsReservedName(name)) return new(false, ManagedResource);
        var volume = await engine.GetVolumeAsync(name, ct);
        if (volume is null) return new(false, "docker.operation_failed");
        if (IsManaged(volume.Name, volume.Labels)) return new(false, ManagedResource);
        return await engine.DeleteVolumeAsync(volume.Name, confirmed, ct);
    }

    private static bool IsManaged(string name, IReadOnlyDictionary<string, string> labels) =>
        IsReservedName(name)
        || labels.TryGetValue(ApplicationDeploymentValidation.OwnerLabel, out var owner) && owner == ApplicationDeploymentValidation.OwnerValue
        || labels.TryGetValue(DockerStackValidation.ComposeProjectLabel, out var project) && IsReservedName(project);

    private static bool HasReservedLabel(IReadOnlyList<string>? labels) => labels?.Any(label =>
    {
        if (label is null) return false;
        var separator = label.IndexOf('=');
        return separator < 0 ? DockerStackValidation.IsReservedApplicationLabel(label, null)
            : DockerStackValidation.IsReservedApplicationLabel(label[..separator], label[(separator + 1)..]);
    }) == true;
}
