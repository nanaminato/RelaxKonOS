using System.Text.Json;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// The four first-stage templates. Each one generates a base-image selection, a build definition,
/// and a start definition only. No template ever calls the host's Java, Maven, Gradle, .NET SDK, or
/// Python: every tool comes from inside the container image, and the Server's own runtime
/// dependencies are unrelated to this boundary.
/// </summary>
internal static class ApplicationTemplateCatalog
{
    public static IReadOnlyList<IApplicationTemplate> All { get; } =
    [
        new ImageTemplate(),
        new JavaJarTemplate(),
        new DotNetPublishTemplate(),
        new PythonProjectTemplate(),
    ];

    public static IApplicationTemplate Require(ApplicationSourceKind kind) =>
        All.FirstOrDefault(x => x.Kind == kind)
        ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.NotSupported, 400);

    public static ApplicationDeploymentTemplateDto[] DescribeAll(ApplicationDeploymentOptions options) =>
        [.. All.Select(template => template.Describe(options))];

    internal static string[] Arguments(IReadOnlyList<string>? arguments)
    {
        if (arguments is null) return [];
        if (arguments.Count > 64) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        foreach (var argument in arguments)
            if (argument.Length > 4096 || argument.Any(char.IsControl))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        return [.. arguments];
    }

    internal static string BaseImage(string? requested, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        RequirePinnedImage(candidate);
        return candidate;
    }

    /// <summary>Rejects both an invalid reference and a floating <c>latest</c> tag.</summary>
    internal static void RequirePinnedImage(string imageReference)
    {
        if (!ApplicationDeploymentValidation.IsValidImageReference(imageReference)
            || !ApplicationDeploymentValidation.IsPinnedImageReference(imageReference))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ImageReferenceInvalid, 400);
    }

    /// <summary>
    /// A publish archive may wrap its content in one top-level directory. That single directory is
    /// the publish root; anything else keeps the extraction root.
    /// </summary>
    internal static string PublishRoot(string contextDirectory)
    {
        var directories = Directory.GetDirectories(contextDirectory);
        var files = Directory.GetFiles(contextDirectory);
        return directories.Length == 1 && files.Length == 0 ? directories[0] : contextDirectory;
    }
}

internal abstract class ApplicationTemplateBase : IApplicationTemplate
{
    public abstract ApplicationSourceKind Kind { get; }
    public abstract string TemplateVersion { get; }
    protected abstract string DisplayName { get; }
    protected virtual bool RequiresArchive => false;
    protected virtual bool RequiresImage => false;
    protected virtual bool SupportsSelfContained => false;
    protected virtual string? DefaultBaseImage(ApplicationDeploymentOptions options) => null;
    protected virtual int DefaultContainerPort => 8080;

    public ApplicationDeploymentTemplateDto Describe(ApplicationDeploymentOptions options) => new(
        Kind,
        TemplateVersion,
        DisplayName,
        DefaultBaseImage(options),
        [.. ApplicationDeploymentValidation.SupportedPlatforms],
        RequiresArchive,
        RequiresImage,
        SupportsSelfContained,
        DefaultContainerPort);

    public virtual DeploymentPlan Validate(DeploymentSourceInputDto source, ApplicationRecord definition, ApplicationDeploymentOptions options, string inputReference)
    {
        if (RequiresImage && string.IsNullOrWhiteSpace(source.ImageReference))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ImageReferenceInvalid, 400);
        if (RequiresArchive && string.IsNullOrWhiteSpace(source.ArchiveReferenceId))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.FileReferenceUnavailable, 400);
        if (!RequiresImage && !string.IsNullOrWhiteSpace(source.ImageReference))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        if (!RequiresArchive && !string.IsNullOrWhiteSpace(source.ArchiveReferenceId))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        if (!SupportsSelfContained && source.SelfContained)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);

        var image = RequiresImage
            ? source.ImageReference!.Trim()
            : ApplicationDeploymentValidation.BuiltImageReference(definition.Name, inputReference);
        if (RequiresImage) ApplicationTemplateCatalog.RequirePinnedImage(image);

        // An operator-supplied base image is validated here and resolved by the template, so a
        // template default is never confused with an explicit override.
        string? requestedBaseImage = null;
        if (!string.IsNullOrWhiteSpace(source.BaseImage))
        {
            requestedBaseImage = source.BaseImage.Trim();
            ApplicationTemplateCatalog.RequirePinnedImage(requestedBaseImage);
        }

        return new(Kind, TemplateVersion, image, requestedBaseImage, source.ArchiveReferenceId?.Trim(),
            source.RuntimeVersion?.Trim(), source.ProgramEntry?.Trim(), ApplicationTemplateCatalog.Arguments(source.Arguments), source.SelfContained);
    }

    public virtual Task<(string EntryPoint, string[] Arguments)> PrepareBuildContextAsync(
        DeploymentPlan plan, string contextDirectory, ApplicationRecord definition, ApplicationDeploymentOptions options, ApplicationArchiveExecution execution, CancellationToken cancellationToken)
        => throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.NotSupported, 400);
}

/// <summary>Deploys an existing image reference after pulling it and binding its observed identity.</summary>
internal sealed class ImageTemplate : ApplicationTemplateBase
{
    public override ApplicationSourceKind Kind => ApplicationSourceKind.Image;
    public override string TemplateVersion => "1.0";
    protected override string DisplayName => "Image";
    protected override bool RequiresImage => true;

    public override DeploymentPlan Validate(DeploymentSourceInputDto source, ApplicationRecord definition, ApplicationDeploymentOptions options, string inputReference)
    {
        // The image template deliberately keeps the image's own entry point. It accepts command
        // arguments, but no unrelated runtime/base-image fields that would otherwise be silently ignored.
        if (!string.IsNullOrWhiteSpace(source.BaseImage) || !string.IsNullOrWhiteSpace(source.RuntimeVersion)
            || !string.IsNullOrWhiteSpace(source.ProgramEntry))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        return base.Validate(source, definition, options, inputReference);
    }
}

internal static class ContainerImageFacts
{
    /// <summary>Maps a Docker Engine architecture name onto the platform identifier used by templates.</summary>
    public static string? NormalizeArchitecture(string? architecture) => architecture?.Trim().ToLowerInvariant() switch
    {
        "x86_64" or "amd64" => "amd64",
        "aarch64" or "arm64" or "arm64/v8" => "arm64",
        "armv7l" or "armhf" or "arm/v7" => "arm",
        _ => null,
    };

    public static bool IsSupportedPlatform(string? operatingSystem, string? architecture)
        => string.Equals(operatingSystem, "linux", StringComparison.OrdinalIgnoreCase)
        && NormalizeArchitecture(architecture) is { } normalized
        && ApplicationDeploymentValidation.SupportedPlatforms.Contains($"linux/{normalized}", StringComparer.Ordinal);

    public static string? Describe(string? operatingSystem, string? architecture)
        => string.IsNullOrWhiteSpace(operatingSystem) ? null : $"{operatingSystem.ToLowerInvariant()}/{NormalizeArchitecture(architecture) ?? architecture!.ToLowerInvariant()}";
}

/// <summary>
/// Deploys an executable JAR on a JRE base image. A plain JAR without a main class, a custom
/// classpath, and a WAR are explicitly outside the first-stage template scope.
/// </summary>
internal sealed class JavaJarTemplate : ApplicationTemplateBase
{
    public const string RuntimeDirectory = "/app";
    public const string JarFileName = "app.jar";

    public override ApplicationSourceKind Kind => ApplicationSourceKind.JavaJar;
    public override string TemplateVersion => "1.0";
    protected override string DisplayName => "Java JAR";
    protected override bool RequiresArchive => true;
    protected override string? DefaultBaseImage(ApplicationDeploymentOptions options) => options.JavaBaseImage;

    public override DeploymentPlan Validate(DeploymentSourceInputDto source, ApplicationRecord definition, ApplicationDeploymentOptions options, string inputReference)
    {
        if (!string.IsNullOrWhiteSpace(source.ProgramEntry))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.EntryPointInvalid, 400);
        var plan = base.Validate(source, definition, options, inputReference);
        if (!string.IsNullOrWhiteSpace(plan.RuntimeVersion) && !IsRuntimeLine(plan.RuntimeVersion))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.RuntimeMismatch, 400);
        var fallback = string.IsNullOrWhiteSpace(plan.RuntimeVersion)
            ? options.JavaBaseImage
            : $"eclipse-temurin:{plan.RuntimeVersion}-jre";
        var baseImage = ApplicationTemplateCatalog.BaseImage(source.BaseImage, fallback);
        if (!string.IsNullOrWhiteSpace(plan.RuntimeVersion) && !baseImage.Contains($":{plan.RuntimeVersion}", StringComparison.Ordinal))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.RuntimeMismatch, 400);
        return plan with { BaseImage = baseImage };
    }

    public override async Task<(string EntryPoint, string[] Arguments)> PrepareBuildContextAsync(
        DeploymentPlan plan, string contextDirectory, ApplicationRecord definition, ApplicationDeploymentOptions options, ApplicationArchiveExecution execution, CancellationToken cancellationToken)
    {
        await execution.WriteDockerfileAsync(contextDirectory, Kind, plan.BaseImage ?? options.JavaBaseImage,
            definition.ContainerPort, JarFileName, false, cancellationToken);
        return ($"java -jar {RuntimeDirectory}/{JarFileName}", plan.Arguments);
    }

    private static bool IsRuntimeLine(string value) => value.Length is >= 1 and <= 16
        && value.Split('.').All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
}

/// <summary>
/// Deploys a .NET publish directory. Deployment type, target framework, target runtime, and the
/// Web/Worker role are all validated against the actual publish output rather than trusted from
/// the request.
/// </summary>
internal sealed class DotNetPublishTemplate : ApplicationTemplateBase
{
    public const string RuntimeDirectory = "/app";

    public override ApplicationSourceKind Kind => ApplicationSourceKind.DotNetPublish;
    public override string TemplateVersion => "1.0";
    protected override string DisplayName => ".NET publish";
    protected override bool RequiresArchive => true;
    protected override bool SupportsSelfContained => true;
    protected override string? DefaultBaseImage(ApplicationDeploymentOptions options) => options.DotNetWebBaseImage;

    public override async Task<(string EntryPoint, string[] Arguments)> PrepareBuildContextAsync(
        DeploymentPlan plan, string contextDirectory, ApplicationRecord definition, ApplicationDeploymentOptions options, ApplicationArchiveExecution execution, CancellationToken cancellationToken)
    {
        var root = ApplicationTemplateCatalog.PublishRoot(contextDirectory);
        var runtimeConfigurations = Directory.GetFiles(root, "*.runtimeconfig.json", SearchOption.TopDirectoryOnly);
        if (runtimeConfigurations.Length != 1)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveContentInvalid, 400);

        var assemblyName = Path.GetFileName(runtimeConfigurations[0])[..^".runtimeconfig.json".Length];
        if (assemblyName.Length == 0) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveContentInvalid, 400);

        var runtimeConfig = await ReadJsonAsync(runtimeConfigurations[0], cancellationToken)
            ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveContentInvalid, 400);
        if (!runtimeConfig.TryGetProperty("runtimeOptions", out var runtimeOptions) || runtimeOptions.ValueKind != JsonValueKind.Object)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveContentInvalid, 400);

        // 1. Target framework must be one the selected base image can serve.
        var targetFramework = ReadString(runtimeOptions, "tfm");
        var imageVersion = BaseImageVersion(targetFramework)
            ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.RuntimeMismatch, 400);
        if (!string.IsNullOrWhiteSpace(plan.RuntimeVersion) && !string.Equals(plan.RuntimeVersion, imageVersion, StringComparison.Ordinal))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.RuntimeMismatch, 400);

        // 2. Framework-dependent and self-contained publishes use different base images.
        var includedFrameworks = runtimeOptions.TryGetProperty("includedFrameworks", out var included) && included.ValueKind == JsonValueKind.Array;
        if (plan.SelfContained != includedFrameworks)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.RuntimeMismatch, 400);

        // 3. The Web/Worker role must match the actual publish, and it selects the base image.
        var isWeb = runtimeOptions.TryGetProperty("frameworks", out var frameworks) && frameworks.ValueKind == JsonValueKind.Array
            && frameworks.EnumerateArray().Any(framework => ReadString(framework, "name") == "Microsoft.AspNetCore.App");
        if (isWeb != (definition.WorkloadKind == ApplicationWorkloadKind.Web))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.RuntimeMismatch, 400);

        // 4. A RID-specific publish must target a Linux runtime the engine can execute.
        var target = await ReadRuntimeTargetAsync(root, cancellationToken);
        if (target is not null && !target.StartsWith("linux-", StringComparison.OrdinalIgnoreCase))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ImagePlatformMismatch, 400);

        var baseImage = ResolveBaseImage(plan, options, imageVersion, isWeb, plan.SelfContained);
        if (plan.SelfContained && !File.Exists(Path.Combine(root, assemblyName)))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveContentInvalid, 400);
        if (!plan.SelfContained && !File.Exists(Path.Combine(root, assemblyName + ".dll")))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ArchiveContentInvalid, 400);

        await execution.WriteDockerfileAsync(contextDirectory, ApplicationSourceKind.DotNetPublish, baseImage, definition.ContainerPort, assemblyName, plan.SelfContained, cancellationToken);
        return (plan.SelfContained ? $"./{assemblyName}" : $"dotnet {assemblyName}.dll", plan.Arguments);
    }

    private static string ResolveBaseImage(DeploymentPlan plan, ApplicationDeploymentOptions options, string imageVersion, bool isWeb, bool selfContained)
    {
        if (plan.BaseImage is { Length: > 0 } requested) return requested;
        var template = selfContained
            ? options.DotNetSelfContainedBaseImage
            : isWeb ? options.DotNetWebBaseImage : options.DotNetWorkerBaseImage;
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

/// <summary>
/// Deploys a Python project whose dependencies are declared for build-time installation. The
/// container start never installs anything, so a restart cannot reach a package index.
/// </summary>
internal sealed class PythonProjectTemplate : ApplicationTemplateBase
{
    public const string RuntimeDirectory = "/app";

    public override ApplicationSourceKind Kind => ApplicationSourceKind.PythonProject;
    public override string TemplateVersion => "1.0";
    protected override string DisplayName => "Python project";
    protected override bool RequiresArchive => true;
    protected override string? DefaultBaseImage(ApplicationDeploymentOptions options) => options.PythonBaseImage;

    public override DeploymentPlan Validate(DeploymentSourceInputDto source, ApplicationRecord definition, ApplicationDeploymentOptions options, string inputReference)
    {
        if (string.IsNullOrWhiteSpace(source.ProgramEntry) || !IsValidModule(source.ProgramEntry))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.EntryPointInvalid, 400);
        var plan = base.Validate(source, definition, options, inputReference);
        if (!string.IsNullOrWhiteSpace(plan.RuntimeVersion) && !IsRuntimeLine(plan.RuntimeVersion))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.RuntimeMismatch, 400);
        var fallback = string.IsNullOrWhiteSpace(plan.RuntimeVersion)
            ? options.PythonBaseImage
            : $"python:{plan.RuntimeVersion}-slim";
        var baseImage = ApplicationTemplateCatalog.BaseImage(source.BaseImage, fallback);
        if (!string.IsNullOrWhiteSpace(plan.RuntimeVersion) && !baseImage.Contains($":{plan.RuntimeVersion}", StringComparison.Ordinal))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.RuntimeMismatch, 400);
        return plan with { BaseImage = baseImage };
    }

    public override async Task<(string EntryPoint, string[] Arguments)> PrepareBuildContextAsync(
        DeploymentPlan plan, string contextDirectory, ApplicationRecord definition, ApplicationDeploymentOptions options, ApplicationArchiveExecution execution, CancellationToken cancellationToken)
    {
        var module = plan.ProgramEntry!;
        await execution.WriteDockerfileAsync(contextDirectory, Kind, plan.BaseImage ?? options.PythonBaseImage,
            definition.ContainerPort, module, false, cancellationToken);
        return ($"python -m {module}", plan.Arguments);
    }

    private static bool IsValidModule(string value) => value.Length is >= 1 and <= 128
        && value.Split('.').All(segment => segment.Length >= 1
            && (char.IsAsciiLetter(segment[0]) || segment[0] == '_')
            && segment.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));

    private static bool IsRuntimeLine(string value) => value.Length is >= 1 and <= 16
        && value.Split('.').All(part => part.Length > 0 && part.All(char.IsAsciiDigit));

}
