using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>Runs only a small allow-list of Docker Compose operations from server-owned files.</summary>
public sealed class DockerComposeExecutor(string dataDirectory) : IDockerComposeService
{
    private const int MaximumComposeBytes = 1024 * 1024;
    private const string ResolvedFile = "compose.resolved.yaml";
    private const string SourceFile = "compose.source.yaml";
    /// <summary>
    /// Parses the definition with the Docker CLI itself. The result is the contract an operator approves,
    /// so it is produced by the same parser that will later apply it — never by a second, disagreeing
    /// YAML reader in this process or on a phone.
    /// </summary>
    public async Task<DockerStackPreviewDto> PreviewAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default)
    {
        Validate(definition);
        return await PreviewConfigurationAsync(definition, DockerComposeSubsetValidation.RequireUnmanagedConfiguration, cancellationToken);
    }

    public Task<DockerStackPreviewDto> PreviewManagedAsync(ManagedComposePlan plan, CancellationToken cancellationToken = default) =>
        PreviewConfigurationAsync(plan.RenderPreview(), plan.RequireResolvedTopology, cancellationToken);

    public async Task<DockerStackMutationResult> DeployManagedAsync(ManagedComposePlan plan,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> environment, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
            throw new DockerStackException(DockerStackProblem.EngineUnavailable, 503);
        var definition = plan.RenderDeployment(out var variables, environment);
        var directory = Path.Combine(dataDirectory, "managed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var path = Path.Combine(directory, "compose.json");
            await File.WriteAllTextAsync(path, definition.ComposeYaml, cancellationToken);
            string[] prefix = ["compose", "--env-file", "/dev/null", "--project-name", definition.Name, "--file", path];
            var config = await RunManagedAsync([.. prefix, "config", "--format", "json"], cancellationToken, variables);
            if (!config.Success) return new(false, DockerStackProblem.ComposeFailed, []);
            using (var resolved = JsonDocument.Parse(config.Output))
                plan.RequireResolvedTopology(resolved.RootElement, environment);
            await RequireManagedResourcesAsync(plan, cancellationToken);
            var result = await RunManagedAsync([.. prefix, "up", "--detach", "--no-build", "--pull", "never"], cancellationToken, variables);
            await RequireManagedResourcesAsync(plan, cancellationToken);
            return new(result.Success, result.Success ? string.Empty : DockerStackProblem.ComposeFailed, []);
        }
        catch (JsonException) { throw new DockerStackException(DockerStackProblem.ComposeFailed); }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new DockerComposeCleanupException(); }
        }
    }

    private async Task RequireManagedResourcesAsync(ManagedComposePlan plan, CancellationToken ct)
    {
        const string projectLabel = "com.docker.compose.project";
        const string serviceLabel = "com.docker.compose.service";
        var expected = plan.Services.ToDictionary(service => plan.ContainerName(service.Id), service => service.Id, StringComparer.Ordinal);
        var rows = await ReadManagedRowsAsync(["ps", "--all", "--format",
            "{{.Names}}\t{{.Label \"com.docker.compose.project\"}}"], ct);
        foreach (var row in rows)
        {
            var fields = row.Split('\t');
            if (fields.Length != 2) throw new DockerStackException(DockerStackProblem.EngineUnavailable, 503);
            if (fields[1] != plan.Project && !expected.ContainsKey(fields[0])) continue;
            if (!expected.TryGetValue(fields[0], out var service))
                throw new DockerStackException(DockerStackProblem.ApplicationResourceManaged);
            await RequireLabelsAsync("container", fields[0], "{{json .Config.Labels}}", serviceLabel, service);
        }

        await RequireNamedResourceAsync("network", plan.NetworkName, "private");
        foreach (var volume in plan.Services.SelectMany(service => service.Volumes))
            await RequireNamedResourceAsync("volume", plan.VolumeName(volume.Name), volume.Name);

        async Task RequireNamedResourceAsync(string kind, string name, string key)
        {
            var names = await ReadManagedRowsAsync([kind, "ls", "--filter", "name=" + name, "--format", "{{.Name}}"], ct);
            if (names.Count(candidate => candidate == name) > 1)
                throw new DockerStackException(DockerStackProblem.ApplicationResourceManaged);
            if (names.Contains(name, StringComparer.Ordinal))
                await RequireLabelsAsync(kind, name, "{{json .Labels}}", "com.docker.compose." + kind, key);
        }

        async Task RequireLabelsAsync(string kind, string name, string format, string kindLabel, string key)
        {
            var result = await RunManagedAsync([kind, "inspect", "--format", format, name], ct);
            if (!result.Success) throw new DockerStackException(DockerStackProblem.EngineUnavailable, 503);
            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(result.Output);
            if (labels is null || labels.GetValueOrDefault("relaxkonos.managed") != "true"
                || labels.GetValueOrDefault(DockerStackValidation.ApplicationOwnerLabel) != DockerStackValidation.ApplicationOwnerValue
                || labels.GetValueOrDefault(DockerStackValidation.ApplicationIdLabel) != plan.InstanceId.ToString("D")
                || labels.GetValueOrDefault(projectLabel) != plan.Project || labels.GetValueOrDefault(kindLabel) != key)
                throw new DockerStackException(DockerStackProblem.ApplicationResourceManaged);
        }
    }

    private async Task<string[]> ReadManagedRowsAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await RunManagedAsync(args, ct);
        if (!result.Success) throw new DockerStackException(DockerStackProblem.EngineUnavailable, 503);
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task<DockerStackPreviewDto> PreviewConfigurationAsync(DockerStackDefinitionDto definition,
        Action<JsonElement> requireConfiguration, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(dataDirectory, "preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var composePath = Path.Combine(directory, "compose.yaml");
        try
        {
            await File.WriteAllTextAsync(composePath, definition.ComposeYaml, cancellationToken);
            var result = await RunAsync(["compose", "--project-name", definition.Name, "--file", composePath, "config", "--format", "json"], cancellationToken);
            if (!result.Success) throw new DockerStackException(ToProblemCode(result.Error), 409);
            return ParsePreview(definition, result.Output, requireConfiguration);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new DockerComposeCleanupException(); }
        }
    }

    /// <summary>Reads the Compose parser's own JSON. Only the members an operator can act on are kept;
    /// anything unrecognised is ignored rather than guessed at.</summary>
    private static DockerStackPreviewDto ParsePreview(DockerStackDefinitionDto definition, string json, Action<JsonElement> requireConfiguration)
    {
        var services = new List<DockerStackPreviewServiceDto>();
        var volumes = new List<string>();
        var networks = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            requireConfiguration(root);
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
        var directory = Path.Combine(dataDirectory, definition.Name);
        Directory.CreateDirectory(directory);
        var pending = Path.Combine(directory, $"compose.pending-{Guid.NewGuid():N}.yaml");
        var composePath = Path.Combine(directory, ResolvedFile);
        try
        {
            await File.WriteAllTextAsync(pending, definition.ComposeYaml, cancellationToken);
            var config = await RunAsync(["compose", "--project-name", definition.Name, "--file", pending, "config", "--format", "json"], cancellationToken);
            if (!config.Success) return new DockerStackMutationResult(false, ToProblemCode(config.Error), []);

            using var resolved = JsonDocument.Parse(config.Output);
            DockerComposeSubsetValidation.RequireUnmanagedConfiguration(resolved.RootElement);
            await File.WriteAllTextAsync(pending, config.Output, cancellationToken);
            // The parser escapes Compose interpolation in its own output. A different CLI
            // may emit a document that reparses differently, so prove stability before up.
            var checkedConfig = await RunAsync(["compose", "--project-name", definition.Name, "--file", pending, "config", "--format", "json"], cancellationToken);
            if (!checkedConfig.Success) return new DockerStackMutationResult(false, ToProblemCode(checkedConfig.Error), []);
            using var roundTrip = JsonDocument.Parse(checkedConfig.Output);
            if (!JsonElement.DeepEquals(resolved.RootElement, roundTrip.RootElement))
                throw new DockerStackException(DockerStackProblem.DefinitionChanged);
            File.Move(pending, composePath, overwrite: true);
            await File.WriteAllTextAsync(Path.Combine(directory, SourceFile), definition.ComposeYaml, cancellationToken);
        }
        catch (JsonException) { throw new DockerStackException(DockerStackProblem.ComposeFailed); }
        finally
        {
            try { File.Delete(pending); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new DockerComposeCleanupException(); }
        }
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
        string[] args = ["ps", "--all", "--filter", $"label=com.docker.compose.project={name}", "--format", "{{.Label \"com.docker.compose.service\"}}\\t{{.Names}}\\t{{.Image}}\\t{{.State}}\\t{{.Status}}"];
        var result = name.StartsWith(DockerStackValidation.ApplicationComposeProjectPrefix, StringComparison.Ordinal)
            ? await RunManagedAsync(args, cancellationToken)
            : await RunAsync(args, cancellationToken);
        if (!result.Success) throw new DockerStackException(DockerStackProblem.EngineUnavailable, 503);
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
            var resolvedPath = Path.Combine(dataDirectory, name, ResolvedFile);
            var original = Path.Combine(dataDirectory, name, SourceFile);
            if (Path.GetFullPath(composePath).Equals(Path.GetFullPath(resolvedPath), StringComparison.Ordinal) && File.Exists(original))
                composePath = original;
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

    private Task<CommandResult> RunManagedAsync(IReadOnlyList<string> arguments, CancellationToken ct,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        var host = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (string.IsNullOrEmpty(host)) host = "unix:///var/run/docker.sock";
        if (!host.StartsWith("unix:///", StringComparison.Ordinal) || host.Length == "unix:///".Length)
            throw new DockerStackException(DockerStackProblem.EngineUnavailable, 503);
        return RunAsync(["--host", host, .. arguments], ct, managed: true, variables);
    }

    private async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        bool managed = false, IReadOnlyDictionary<string, string>? variables = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            if (managed)
            {
                var environment = process.StartInfo.Environment;
                foreach (var name in environment.Keys.Where(name => name.StartsWith("COMPOSE_", StringComparison.Ordinal)
                    || name is "DOCKER_CONTEXT" or "DOCKER_HOST" or "DOCKER_TLS" or "DOCKER_TLS_VERIFY" or "DOCKER_CERT_PATH").ToArray())
                    environment.Remove(name);
                if (variables is not null)
                    foreach (var variable in variables) environment[variable.Key] = variable.Value;
            }
            if (!process.Start()) return new CommandResult(false, string.Empty, "start_failed");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var completion = Task.WhenAll(process.WaitForExitAsync(), outputTask, errorTask);
            try
            {
                await completion.WaitAsync(timeout.Token);
                return new CommandResult(process.ExitCode == 0, await outputTask, await errorTask);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await completion.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception)
                {
                    throw new DockerComposeCleanupException();
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new CommandResult(false, string.Empty, "timeout");
            }
        }
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
