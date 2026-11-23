using System.Text.Json;
using System.IO.Compression;
using System.Text;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

internal static class ApplicationPayload
{
    private const string RequirementsFileName = "requirements.txt";

    internal static Task<string?> PrepareAsync(string contextDirectory, ApplicationSourceKind kind, string entry, CancellationToken cancellationToken)
        => kind switch
        {
            ApplicationSourceKind.JavaJar => PrepareJavaAsync(contextDirectory, entry, cancellationToken),
            ApplicationSourceKind.PythonProject => PreparePythonAsync(contextDirectory, entry, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string PublishRoot(string contextDirectory)
    {
        var directories = Directory.GetDirectories(contextDirectory);
        return directories.Length == 1 && !Directory.EnumerateFiles(contextDirectory).Any()
            ? directories[0] : contextDirectory;
    }

    private static async Task<string?> PrepareJavaAsync(string contextDirectory, string entry, CancellationToken cancellationToken)
    {
        var root = PublishRoot(contextDirectory);
        var jars = Directory.GetFiles(root, "*.jar", SearchOption.AllDirectories);
        if (jars.Length != 1)
            return ApplicationDeploymentProblemCodes.ArchiveContentInvalid;
        if (!await HasMainClassAsync(jars[0], cancellationToken))
            return ApplicationDeploymentProblemCodes.EntryPointInvalid;

        var staged = Path.Combine(contextDirectory, entry);
        if (!string.Equals(jars[0], staged, StringComparison.Ordinal))
        {
            File.Copy(jars[0], staged, overwrite: true);
            File.Delete(jars[0]);
        }
        return null;
    }

    private static async Task<string?> PreparePythonAsync(string contextDirectory, string entry, CancellationToken cancellationToken)
    {
        var root = PublishRoot(contextDirectory);
        var requirements = Path.Combine(root, RequirementsFileName);
        if (!File.Exists(requirements))
            return ApplicationDeploymentProblemCodes.ArchiveContentInvalid;
        if (!await HasLockedRequirementsAsync(requirements, cancellationToken))
            return ApplicationDeploymentProblemCodes.ArchiveContentInvalid;

        var segments = entry.Split('.');
        if (!File.Exists(Path.Combine(root, $"{segments[0]}.py")) && !Directory.Exists(Path.Combine(root, segments[0])))
            return ApplicationDeploymentProblemCodes.EntryPointInvalid;
        return null;
    }

    /// <summary>An executable JAR is required because <c>java -jar</c> needs a manifest main class.</summary>
    private static async Task<bool> HasMainClassAsync(string jarPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var file = File.OpenRead(jarPath);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            var manifest = archive.GetEntry("META-INF/MANIFEST.MF");
            if (manifest is null) return false;
            await using var stream = manifest.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                if (line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase) && line["Main-Class:".Length..].Trim().Length > 0)
                    return true;
            return false;
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>The first-stage Python input is deliberately reproducible: every dependency must
    /// identify an exact distribution version, or a direct artifact with an integrity fragment.</summary>
    private static async Task<bool> HasLockedRequirementsAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            return lines.All(line =>
            {
                var value = line.Trim();
                return value.Length == 0 || value.StartsWith('#')
                    || value.Contains("==", StringComparison.Ordinal)
                    || value.Contains(" @ ", StringComparison.Ordinal) && value.Contains("#sha256=", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (IOException) { return false; }
    }

    internal static async Task<(string? EntryPoint, string? Problem)> PrepareDotNetAsync(string contextDirectory,
        string? requestedImage, string? runtimeVersion, bool selfContained, ApplicationWorkloadKind workloadKind, int port,
        string selfContainedImage, string webImage, string workerImage, CancellationToken cancellationToken)
    {
        var root = PublishRoot(contextDirectory);
        var runtimeConfigurations = Directory.GetFiles(root, "*.runtimeconfig.json", SearchOption.TopDirectoryOnly);
        if (runtimeConfigurations.Length != 1)
            return (null, ApplicationDeploymentProblemCodes.ArchiveContentInvalid);

        var assemblyName = Path.GetFileName(runtimeConfigurations[0])[..^".runtimeconfig.json".Length];
        if (assemblyName.Length == 0) return (null, ApplicationDeploymentProblemCodes.ArchiveContentInvalid);

        if (await ReadJsonAsync(runtimeConfigurations[0], cancellationToken) is not { } runtimeConfig) return (null, ApplicationDeploymentProblemCodes.ArchiveContentInvalid);
        if (!runtimeConfig.TryGetProperty("runtimeOptions", out var runtimeOptions) || runtimeOptions.ValueKind != JsonValueKind.Object)
            return (null, ApplicationDeploymentProblemCodes.ArchiveContentInvalid);

        // 1. Target framework must be one the selected base image can serve.
        var targetFramework = ReadString(runtimeOptions, "tfm");
        if (BaseImageVersion(targetFramework) is not { } imageVersion) return (null, ApplicationDeploymentProblemCodes.RuntimeMismatch);
        if (!string.IsNullOrWhiteSpace(runtimeVersion) && !string.Equals(runtimeVersion, imageVersion, StringComparison.Ordinal))
            return (null, ApplicationDeploymentProblemCodes.RuntimeMismatch);

        // 2. Framework-dependent and self-contained publishes use different base images.
        var includedFrameworks = runtimeOptions.TryGetProperty("includedFrameworks", out var included) && included.ValueKind == JsonValueKind.Array;
        if (selfContained != includedFrameworks)
            return (null, ApplicationDeploymentProblemCodes.RuntimeMismatch);

        // 3. The Web/Worker role must match the actual publish, and it selects the base image.
        var isWeb = runtimeOptions.TryGetProperty("frameworks", out var frameworks) && frameworks.ValueKind == JsonValueKind.Array
            && frameworks.EnumerateArray().Any(framework => ReadString(framework, "name") == "Microsoft.AspNetCore.App");
        if (isWeb != (workloadKind == ApplicationWorkloadKind.Web))
            return (null, ApplicationDeploymentProblemCodes.RuntimeMismatch);

        // 4. A RID-specific publish must target a Linux runtime the engine can execute.
        var target = await ReadRuntimeTargetAsync(root, cancellationToken);
        if (target is not null && !target.StartsWith("linux-", StringComparison.OrdinalIgnoreCase))
            return (null, ApplicationDeploymentProblemCodes.ImagePlatformMismatch);

        var baseImage = ResolveBaseImage(requestedImage, selfContainedImage, webImage, workerImage, imageVersion, isWeb, selfContained);
        if (selfContained && !File.Exists(Path.Combine(root, assemblyName)))
            return (null, ApplicationDeploymentProblemCodes.ArchiveContentInvalid);
        if (!selfContained && !File.Exists(Path.Combine(root, assemblyName + ".dll")))
            return (null, ApplicationDeploymentProblemCodes.ArchiveContentInvalid);

        await ApplicationArchiveExtractor.WriteDockerfileAsync(contextDirectory, ApplicationSourceKind.DotNetPublish, baseImage, port, assemblyName, selfContained, cancellationToken);
        return (selfContained ? $"./{assemblyName}" : $"dotnet {assemblyName}.dll", null);
    }

    private static string ResolveBaseImage(string? requestedImage, string selfContainedImage, string webImage, string workerImage, string imageVersion, bool isWeb, bool selfContained)
    {
        if (requestedImage is { Length: > 0 } requested) return requested;
        var template = selfContained
            ? selfContainedImage
            : isWeb ? webImage : workerImage;
        return ReplaceVersionSuffix(template, imageVersion);
    }

    /// <summary>Rewrites the framework-version suffix of a configured base image such as <c>.../aspnet:10.0</c>.</summary>
    private static string ReplaceVersionSuffix(string template, string imageVersion)
    {
        var separator = template.LastIndexOf(':');
        if (separator < 0) return template;
        var tag = template[(separator + 1)..];
        var dash = tag.IndexOf('-');
        return dash < 0 ? $"{template[..(separator + 1)]}{imageVersion}" : $"{template[..(separator + 1)]}{imageVersion}{tag[dash..]}";
    }

    private static string? BaseImageVersion(string? targetFramework)
    {
        if (targetFramework is null || !targetFramework.StartsWith("net", StringComparison.Ordinal)) return null;
        var version = targetFramework["net".Length..];
        var segments = version.Split('.');
        if (segments.Length < 1 || !int.TryParse(segments[0], out var major) || major < 5) return null;
        return segments.Length >= 2 ? $"{major}.{segments[1]}" : $"{major}.0";
    }

    private static async Task<JsonElement?> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private static async Task<string?> ReadRuntimeTargetAsync(string root, CancellationToken cancellationToken)
    {
        var dependencyFiles = Directory.GetFiles(root, "*.deps.json", SearchOption.TopDirectoryOnly);
        if (dependencyFiles.Length != 1) return null;
        var document = await ReadJsonAsync(dependencyFiles[0], cancellationToken);
        if (document is not { } element || !element.TryGetProperty("runtimeTarget", out var runtimeTarget)) return null;
        var name = ReadString(runtimeTarget, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;
        var separator = name.IndexOf('/');
        return separator < 0 ? null : name[(separator + 1)..];
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
