using System.Globalization;
using System.Text.Json;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>Server-owned topology. Environment entries describe injection targets without carrying credentials.</summary>
public sealed record ManagedComposePlan(Guid InstanceId, string WebService, int HostPort, IReadOnlyList<ManagedComposeService> Services)
{
    public string Project => DockerStackValidation.ApplicationComposeProjectPrefix + InstanceId.ToString("N");
    private const string Network = "private";
    private const string PreviewValue = "relaxkonos-preview";

    public DockerStackDefinitionDto RenderPreview() => Render((_, _) => PreviewValue);

    public DockerStackDefinitionDto RenderDeployment(out Dictionary<string, string> processEnvironment,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> environment)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var service in Services)
        {
            if (!EnvironmentFor(environment, service.Id).Keys.ToHashSet(StringComparer.Ordinal).SetEquals(service.EnvironmentNames))
                Refuse();
        }
        if (environment.Keys.Except(Services.Select(service => service.Id), StringComparer.Ordinal).Any()) Refuse();
        var definition = Render((service, name) =>
        {
            var value = EnvironmentFor(environment, service)[name];
            if (value is null || value.Contains('\0')) Refuse();
            var variable = "RK_MANAGED_" + variables.Count.ToString(CultureInfo.InvariantCulture);
            variables.Add(variable, value!);
            return "${" + variable + "?missing managed input}";
        });
        processEnvironment = variables;
        return definition;
    }

    private DockerStackDefinitionDto Render(Func<string, string, string> environmentValue)
    {
        if (InstanceId == Guid.Empty) Refuse();
        var volumes = Services.SelectMany(service => service.Volumes).ToDictionary(volume => volume.Name,
            volume => new { name = VolumeName(volume.Name), labels = OwnerLabels() }, StringComparer.Ordinal);
        var services = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var service in Services)
        {
            var value = new Dictionary<string, object>
            {
                ["image"] = service.ImageReference,
                ["container_name"] = ContainerName(service.Id),
                ["platform"] = "linux/amd64",
                ["labels"] = OwnerLabels(),
                ["networks"] = new[] { Network },
                ["expose"] = new[] { service.ContainerPort.ToString(CultureInfo.InvariantCulture) },
                ["cpus"] = 1,
                ["mem_limit"] = 512L * 1024 * 1024,
                ["pids_limit"] = 512,
            };
            if (service.Id == WebService)
                value["ports"] = new[] { new { host_ip = "127.0.0.1", published = HostPort.ToString(CultureInfo.InvariantCulture), target = service.ContainerPort, protocol = "tcp" } };
            if (service.DependsOn.Count != 0) value["depends_on"] = service.DependsOn;
            if (service.Volumes.Count != 0)
                value["volumes"] = service.Volumes.Select(volume => new { type = "volume", source = volume.Name, target = volume.Path, read_only = volume.ReadOnly }).ToArray();
            if (service.EnvironmentNames.Count != 0)
                value["environment"] = service.EnvironmentNames.ToDictionary(name => name, name => environmentValue(service.Id, name), StringComparer.Ordinal);
            services.Add(service.Id, value);
        }
        return new(Project, JsonSerializer.Serialize(new
        {
            services,
            volumes,
            networks = new Dictionary<string, object> { [Network] = new { name = Project + "_private", driver = "bridge", labels = OwnerLabels() } },
        }));
    }

    public void RequireResolvedTopology(JsonElement root) => RequireResolvedTopology(root, null);

    public void RequireResolvedTopology(JsonElement root, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? values)
    {
        if (Text(root, "name") != Project) Refuse();
        var serviceMap = Map(root, "services");
        var volumeMap = Map(root, "volumes");
        var networkMap = Map(root, "networks");
        if (serviceMap.Count != Services.Count || networkMap.Count != 1 || !networkMap.TryGetValue(Network, out var network)
            || Text(network, "name") != Project + "_private" || Text(network, "driver") != "bridge" || !HasOwner(network)
            || HasValue(network, "external") || HasValue(network, "driver_opts"))
            Refuse();
        var volumes = Services.SelectMany(service => service.Volumes).ToArray();
        if (volumeMap.Count != volumes.Length || volumes.Any(volume => !volumeMap.TryGetValue(volume.Name, out var resolved)
            || Text(resolved, "name") != VolumeName(volume.Name) || !HasOwner(resolved)
            || HasValue(resolved, "external") || HasValue(resolved, "driver_opts"))) Refuse();

        foreach (var service in Services)
        {
            if (!serviceMap.TryGetValue(service.Id, out var resolved) || Text(resolved, "image") != service.ImageReference
                || Text(resolved, "container_name") != ContainerName(service.Id)
                || Text(resolved, "platform") != "linux/amd64" || !HasOwner(resolved)
                || !Map(resolved, "networks").Keys.SequenceEqual([Network])
                || !Array(resolved, "expose").Select(value => value.ToString()).SequenceEqual([service.ContainerPort.ToString(CultureInfo.InvariantCulture)]))
                Refuse();
            var dependencies = Map(resolved, "depends_on");
            if (!dependencies.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(service.DependsOn)
                || dependencies.Values.Any(value => Text(value, "condition") != "service_started" || Text(value, "required") != "True"))
                Refuse();
            var environment = Map(resolved, "environment");
            if (!environment.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(service.EnvironmentNames)
                || environment.Any(variable => variable.Value.GetString() != (values is null ? PreviewValue
                    : EnvironmentFor(values, service.Id)[variable.Key].Replace("$", "$$", StringComparison.Ordinal)))) Refuse();
            var ports = Array(resolved, "ports");
            if (service.Id == WebService
                ? ports.Length != 1 || Text(ports[0], "host_ip") != "127.0.0.1" || Text(ports[0], "protocol") != "tcp"
                    || Text(ports[0], "published") != HostPort.ToString(CultureInfo.InvariantCulture)
                    || Text(ports[0], "target") != service.ContainerPort.ToString(CultureInfo.InvariantCulture)
                : ports.Length != 0) Refuse();
            var mounts = Array(resolved, "volumes");
            if (mounts.Length != service.Volumes.Count || service.Volumes.Any(volume => !mounts.Any(mount =>
                Text(mount, "type") == "volume" && Text(mount, "source") == volume.Name && Text(mount, "target") == volume.Path
                    && (Text(mount, "read_only") == "True") == volume.ReadOnly))) Refuse();
            if (Text(resolved, "cpus") != "1" || Text(resolved, "mem_limit") != "536870912" || Text(resolved, "pids_limit") != "512"
                || new[] { "build", "privileged", "network_mode", "pid", "ipc", "devices", "cap_add", "command", "entrypoint" }
                    .Any(property => HasValue(resolved, property))) Refuse();
        }
    }

    public string VolumeName(string key) => Project + "_" + key;
    public string NetworkName => Project + "_private";
    public string ContainerName(string service) => Project + "-" + service + "-1";

    private static IReadOnlyDictionary<string, string> EnvironmentFor(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> environment, string service) =>
        environment.TryGetValue(service, out var values) ? values : EmptyEnvironment;

    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment = new Dictionary<string, string>();
    private Dictionary<string, string> OwnerLabels() => new(StringComparer.Ordinal)
    {
        ["relaxkonos.managed"] = "true",
        [DockerStackValidation.ApplicationOwnerLabel] = DockerStackValidation.ApplicationOwnerValue,
        [DockerStackValidation.ApplicationIdLabel] = InstanceId.ToString("D"),
    };

    private bool HasOwner(JsonElement element)
    {
        var labels = Map(element, "labels");
        return OwnerLabels().All(label => labels.TryGetValue(label.Key, out var value) && value.GetString() == label.Value);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ToString() : null;
    private static bool HasValue(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
    private static Dictionary<string, JsonElement> Map(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal) : [];
    private static JsonElement[] Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()] : [];
    private static void Refuse() => throw new DockerStackException(DockerStackProblem.DefinitionChanged);
}

public sealed record ManagedComposeService(string Id, string ImageReference, int ContainerPort,
    IReadOnlyList<ManagedComposeVolume> Volumes, IReadOnlyList<string> DependsOn, IReadOnlyList<string> EnvironmentNames);

public sealed record ManagedComposeVolume(string Name, string Path, bool ReadOnly);
