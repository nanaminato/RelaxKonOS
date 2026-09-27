using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>Runs only a small allow-list of Docker Compose operations from server-owned files.</summary>
public sealed class DockerComposeService : IDockerComposeService
{
    private const int MaximumComposeBytes = 1024 * 1024;
    private readonly string _dataDirectory;

    public DockerComposeService(IHostEnvironment environment, IOptions<DockerComposeOptions> options)
    {
        _dataDirectory = DockerComposePaths.ResolveDataDirectory(environment, options.Value.DataDirectory);
    }

    /// <summary>
    /// Parses the definition with the Docker CLI itself. The result is the contract an operator approves,
    /// so it is produced by the same parser that will later apply it — never by a second, disagreeing
    /// YAML reader in this process or on a phone.
    /// </summary>
    public async Task<DockerStackPreviewDto> PreviewAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default)
    {
        Validate(definition);
        var directory = Path.Combine(_dataDirectory, "preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var composePath = Path.Combine(directory, "compose.yaml");
        try
        {
            await File.WriteAllTextAsync(composePath, definition.ComposeYaml, cancellationToken);
            var result = await RunAsync(["compose", "--project-name", definition.Name, "--file", composePath, "config", "--format", "json"], cancellationToken);
            if (!result.Success) throw new DockerStackException(ToProblemCode(result.Error), 409);
            return ParsePreview(definition, result.Output);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Reads the Compose parser's own JSON. Only the members an operator can act on are kept;
    /// anything unrecognised is ignored rather than guessed at.</summary>
    private static DockerStackPreviewDto ParsePreview(DockerStackDefinitionDto definition, string json)
    {
        var services = new List<DockerStackPreviewServiceDto>();
        var volumes = new List<string>();
        var networks = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("services", out var serviceMap) && serviceMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var service in serviceMap.EnumerateObject())
                {
                    var image = service.Value.ValueKind == JsonValueKind.Object && service.Value.TryGetProperty("image", out var declared)
                        ? declared.GetString() ?? string.Empty : string.Empty;
                    services.Add(new DockerStackPreviewServiceDto(service.Name, image, ReadPorts(service.Value)));
                }
            }
            volumes = ReadNames(root, "volumes");
            networks = ReadNames(root, "networks");
        }
        catch (JsonException)
        {
            throw new DockerStackException(DockerStackProblem.ComposeFailed, 409);
        }
        if (services.Count == 0) throw new DockerStackException(DockerStackProblem.NoServices, 409);
        return new DockerStackPreviewDto(definition.Name, DockerStackValidation.DefinitionVersion(definition.Name, definition.ComposeYaml),
            [.. services.OrderBy(x => x.Service, StringComparer.OrdinalIgnoreCase)], volumes, networks);
    }

    private static IReadOnlyList<string> ReadPorts(JsonElement service)
    {
        if (service.ValueKind != JsonValueKind.Object || !service.TryGetProperty("ports", out var ports) || ports.ValueKind != JsonValueKind.Array)
            return [];
        var declared = new List<string>();
        foreach (var port in ports.EnumerateArray())
        {
            // Long syntax is an object; short syntax arrives as the literal string the operator wrote.
            if (port.ValueKind == JsonValueKind.Object)
            {
                var target = port.TryGetProperty("target", out var targetValue) ? targetValue.ToString() : string.Empty;
                if (string.IsNullOrEmpty(target)) continue;
                var published = port.TryGetProperty("published", out var publishedValue) ? publishedValue.ToString() : string.Empty;
                declared.Add(string.IsNullOrEmpty(published) ? $"{target}" : $"{published}:{target}");
            }
            else if (port.ValueKind == JsonValueKind.String && port.GetString() is { Length: > 0 } literal)
            {
                declared.Add(literal);
            }
        }
        return declared;
    }

    private static List<string> ReadNames(JsonElement root, string property) => root.TryGetProperty(property, out var map) && map.ValueKind == JsonValueKind.Object
        ? [.. map.EnumerateObject().Select(entry => entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("name", out var name)
            ? name.GetString() ?? entry.Name : entry.Name).Order(StringComparer.OrdinalIgnoreCase)]
        : [];

    /// <summary>Writes the source under the project's own directory and applies it.</summary>
    public async Task<DockerStackMutationResult> DeployAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default)
    {
        Validate(definition);
        var directory = Path.Combine(_dataDirectory, definition.Name);
        Directory.CreateDirectory(directory);
        var composePath = Path.Combine(directory, "compose.yaml");
        await File.WriteAllTextAsync(composePath, definition.ComposeYaml, cancellationToken);
        var result = await RunAsync(["compose", "--project-name", definition.Name, "--file", composePath, "up", "--detach", "--remove-orphans"], cancellationToken);
        // Compose errors can echo substituted environment values. Clients receive only a stable problem
        // code plus the bounded progress lines; raw daemon diagnostics stay on the host.
        return new DockerStackMutationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result.Error), ToLines(result.Output));
    }

    /// <summary>
    /// Reads container labels as belonging to a Compose project. This also works for projects
    /// started outside RelaxKonOS and for projects whose containers have all been stopped.
    /// </summary>
    public async Task<IReadOnlyList<DockerStackServiceDto>> ListServicesAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!DockerStackValidation.IsValidProjectName(name)) return [];
        // The label name is quoted for the Go template, not for a shell: this argument goes straight into
        // the process argument list, and a backslash-escaped quote makes Docker fail with "unexpected \\ in
        // operand", which would leave every observation empty and misreport a working project as partial.
        var result = await RunAsync(["ps", "--all", "--filter", $"label=com.docker.compose.project={name}", "--format", "{{.Label \"com.docker.compose.service\"}}\\t{{.Names}}\\t{{.Image}}\\t{{.State}}\\t{{.Status}}"], cancellationToken);
        if (!result.Success) return [];
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Select(row => new DockerStackServiceDto(Value(row, 0), Value(row, 1), Value(row, 2), Value(row, 3), Value(row, 4)))
            .OrderBy(service => service.Service, StringComparer.OrdinalIgnoreCase)
            .ThenBy(service => service.Container, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Applies a safe lifecycle action to every container labelled for the project.</summary>
    public async Task<DockerStackMutationResult> ApplyActionAsync(string name, DockerStackOperationKind action, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!DockerStackValidation.IsValidProjectName(name))
            return new DockerStackMutationResult(false, DockerStackProblem.InvalidName, []);

        if (action == DockerStackOperationKind.Delete)
        {
            if (!confirmed) return new DockerStackMutationResult(false, DockerStackProblem.ConfirmationRequired, []);
            var stack = (await ListAsync(cancellationToken)).FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (stack is null) return new DockerStackMutationResult(false, DockerStackProblem.NotFound, []);
            var composePath = FirstConfigFile(stack.ConfigFiles);
            if (composePath is null || !File.Exists(composePath)) return await DeleteByLabelsAsync(name, cancellationToken);
            var down = await RunAsync(["compose", "--project-name", stack.Name, "--file", composePath, "down", "--remove-orphans"], cancellationToken);
            return new DockerStackMutationResult(down.Success, down.Success ? string.Empty : ToProblemCode(down.Error), ToLines(down.Output));
        }

        var verb = action switch
        {
            DockerStackOperationKind.Start => "start",
            DockerStackOperationKind.Stop => "stop",
            DockerStackOperationKind.Restart => "restart",
            _ => null,
        };
        if (verb is null) return new DockerStackMutationResult(false, DockerStackProblem.ValidationFailed, []);

        var containers = await RunAsync(["ps", "--all", "--quiet", "--filter", $"label=com.docker.compose.project={name}"], cancellationToken);
        if (!containers.Success) return new DockerStackMutationResult(false, ToProblemCode(containers.Error), []);
        var ids = containers.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return new DockerStackMutationResult(false, DockerStackProblem.NoServices, []);

        var arguments = new List<string> { verb };
        arguments.AddRange(ids);
        var result = await RunAsync(arguments, cancellationToken);
        return new DockerStackMutationResult(result.Success, result.Success ? string.Empty : ToProblemCode(result.Error), ToLines(result.Output));
    }

    /// <summary>
    /// A Compose project has no independent Engine record: its containers and project networks
    /// are the record.  This fallback removes those labelled resources when the original
    /// Compose source has been deleted or moved, while deliberately retaining named volumes.
    /// </summary>
    private async Task<DockerStackMutationResult> DeleteByLabelsAsync(string name, CancellationToken cancellationToken)
    {
        var containers = await RunAsync(["ps", "--all", "--quiet", "--filter", $"label=com.docker.compose.project={name}"], cancellationToken);
        if (!containers.Success) return new DockerStackMutationResult(false, ToProblemCode(containers.Error), []);
        var containerIds = containers.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var output = new List<string>();
        if (containerIds.Length > 0)
        {
            var remove = await RunAsync(["rm", "--force", .. containerIds], cancellationToken);
            if (!remove.Success) return new DockerStackMutationResult(false, ToProblemCode(remove.Error), []);
            output.AddRange(ToLines(remove.Output));
        }

        var networks = await RunAsync(["network", "ls", "--quiet", "--filter", $"label=com.docker.compose.project={name}"], cancellationToken);
        if (!networks.Success) return new DockerStackMutationResult(false, ToProblemCode(networks.Error), []);
        var networkIds = networks.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (networkIds.Length > 0)
        {
            var remove = await RunAsync(["network", "rm", .. networkIds], cancellationToken);
            if (!remove.Success) return new DockerStackMutationResult(false, ToProblemCode(remove.Error), []);
            output.AddRange(ToLines(remove.Output));
        }
        return new DockerStackMutationResult(true, string.Empty, output);
    }

    public async Task<IReadOnlyList<DockerStackDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["compose", "ls", "--all", "--format", "json"], cancellationToken);
        var stacks = new Dictionary<string, DockerStackDto>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (result.Success)
            {
                using var document = JsonDocument.Parse(result.Output);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        var name = Read(item, "Name");
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            var files = Read(item, "ConfigFiles");
                            stacks[name] = new DockerStackDto(name, Read(item, "Status"), files, ConfigDirectory(files));
                        }
                    }
                }
            }
        }
        catch (JsonException) { /* Container labels below still recover stopped projects. */ }

        // docker compose ls has varied between Compose releases. Labels are the Engine source
        // of truth, so merge them as a fallback rather than letting a stopped project vanish.
        var labels = await RunAsync(["ps", "--all", "--format", "{{.Label \"com.docker.compose.project\"}}\t{{.Label \"com.docker.compose.project.config_files\"}}"], cancellationToken);
        if (labels.Success)
        {
            foreach (var row in labels.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => line.Split('\t', 2)))
            {
                var name = Value(row, 0);
                if (!DockerStackValidation.IsValidProjectName(name) || stacks.ContainsKey(name)) continue;
                var files = Value(row, 1);
                stacks[name] = new DockerStackDto(name, "stopped", files, ConfigDirectory(files));
            }
        }

        return stacks.Values.OrderBy(stack => stack.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<DockerStackDefinitionDto?> GetDefinitionAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!DockerStackValidation.IsValidProjectName(name)) return null;
        var stack = (await ListAsync(cancellationToken)).FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var composePath = FirstConfigFile(stack?.ConfigFiles);
        if (composePath is null || !File.Exists(composePath)) return null;
        try
        {
            var yaml = await File.ReadAllTextAsync(composePath, cancellationToken);
            return yaml.Length <= MaximumComposeBytes ? new DockerStackDefinitionDto(stack!.Name, yaml) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void Validate(DockerStackDefinitionDto definition)
    {
        if (!DockerStackValidation.IsValidProjectName(definition.Name))
            throw new DockerStackException(DockerStackProblem.InvalidName, 400);
        if (string.IsNullOrWhiteSpace(definition.ComposeYaml) || System.Text.Encoding.UTF8.GetByteCount(definition.ComposeYaml) > MaximumComposeBytes)
            throw new DockerStackException(DockerStackProblem.InvalidCompose, 400);
        if (!DockerComposeSubsetValidation.IsSupported(definition.ComposeYaml, out var problem))
            throw new DockerStackException(problem, 400);
    }

    private async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            // The build layer is deliberately NOT applied on this path. A stack is started with
            // `compose up`, which accepts no --build-arg at all (only `compose build` does, and this
            // service never runs it), and Compose does not forward the client environment into the
            // build it triggers for a service with a `build:` section. Such a service therefore has
            // to be built through the Manager's image build first, or declare its own build.args in
            // the Compose file.
            // See docs/applications/RelaxKonOS.DockerManager.md §3.5.
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return new CommandResult(false, string.Empty, "start_failed");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token); var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new CommandResult(process.ExitCode == 0, await outputTask, await errorTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new CommandResult(false, string.Empty, "timeout"); }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException) { return new CommandResult(false, string.Empty, "not_found"); }
    }

    private static IReadOnlyList<string> ToLines(string message) => message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line => line.Length <= 512 ? line : line[..512]).Take(20).ToArray();
    private static string Value(IReadOnlyList<string> row, int index) => index < row.Count ? row[index] : string.Empty;
    private static string ConfigDirectory(string configFiles) => Path.GetDirectoryName(FirstConfigFile(configFiles) ?? string.Empty) ?? string.Empty;
    private static string? FirstConfigFile(string? configFiles) => string.IsNullOrWhiteSpace(configFiles)
        ? null
        : configFiles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    private static string Read(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    private static string ToProblemCode(string error) => error.Contains("not_found", StringComparison.OrdinalIgnoreCase) ? "docker.not_installed" : error.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ? "docker.permission_denied" : "docker.compose_failed";
    private sealed record CommandResult(bool Success, string Output, string Error);
}
