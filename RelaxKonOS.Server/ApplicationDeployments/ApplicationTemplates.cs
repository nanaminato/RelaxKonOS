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
    public override ApplicationSourceKind Kind => ApplicationSourceKind.DotNetPublish;
    public override string TemplateVersion => "1.0";
    protected override string DisplayName => ".NET publish";
    protected override bool RequiresArchive => true;
    protected override bool SupportsSelfContained => true;
    protected override string? DefaultBaseImage(ApplicationDeploymentOptions options) => options.DotNetWebBaseImage;
}

/// <summary>
/// Deploys a Python project whose dependencies are declared for build-time installation. The
/// container start never installs anything, so a restart cannot reach a package index.
/// </summary>
internal sealed class PythonProjectTemplate : ApplicationTemplateBase
{
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

    private static bool IsValidModule(string value) => value.Length is >= 1 and <= 128
        && value.Split('.').All(segment => segment.Length >= 1
            && (char.IsAsciiLetter(segment[0]) || segment[0] == '_')
            && segment.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));

    private static bool IsRuntimeLine(string value) => value.Length is >= 1 and <= 16
        && value.Split('.').All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
}
