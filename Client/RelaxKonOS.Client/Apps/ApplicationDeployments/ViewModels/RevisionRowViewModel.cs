using System.Globalization;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.ViewModels;

/// <summary>
/// One published revision. It is immutable, so a row is a pure projection of what the server recorded
/// when the revision was published — including the image identity a rollback would return to.
/// </summary>
public sealed class RevisionRowViewModel(ApplicationRevisionDto revision, Guid? currentRevisionId)
{
    public ApplicationRevisionDto Model { get; } = revision;

    public Guid Id => Model.Id;
    public string NumberText => "#" + Model.Number.ToString(CultureInfo.CurrentCulture);

    public string ImageText
    {
        get
        {
            // The nullable property does not narrow across the null check, so it is read once.
            var imageId = Model.ImageId;
            return string.IsNullOrWhiteSpace(imageId) ? Model.ImageReference : $"{Model.ImageReference} ({Shorten(imageId)})";
        }
    }

    public string PlatformText => string.IsNullOrWhiteSpace(Model.Platform) ? "—" : Model.Platform;
    public string TemplateText => $"{LocalizedText.Get(DeploymentText.Enum(DeploymentText.SourcePrefix, Model.SourceKind))} · {Model.TemplateVersion}";
    public string CreatedText => Model.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string EntryText => Model.Arguments.Count == 0
        ? Model.EntryPoint
        : $"{Model.EntryPoint} {string.Join(' ', Model.Arguments)}";

    public bool IsCurrent => Model.Id == currentRevisionId;
    public bool HasProblem => !string.IsNullOrWhiteSpace(Model.ProblemCode);
    public LocalizedStatus ProblemText => DeploymentText.Problem(Model.ProblemCode);

    /// <summary>Image ids are long digests; the full value stays available in the tooltip.</summary>
    public string ImageTooltip => Model.ImageId ?? Model.ImageReference;

    public string ConfigurationText => Model.Configuration.Count == 0
        ? "—"
        : string.Join(", ", Model.Configuration.Select(entry => entry.IsSecret
            ? $"{entry.Name} (v{entry.SecretVersion?.ToString(CultureInfo.CurrentCulture) ?? "?"})"
            : entry.Name));

    public string VolumesText => Model.Volumes.Count == 0
        ? "—"
        : string.Join(", ", Model.Volumes.Select(volume => $"{volume.Name}:{volume.ContainerPath}"));

    private static string Shorten(string value) => value.Length <= 19 ? value : value[..19] + "…";
}
