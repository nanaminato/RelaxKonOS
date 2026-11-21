using System.Globalization;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.App.ApplicationDeployments.ViewModels;

/// <summary>
/// One row of the application list. It exposes the definition together with the observed state the
/// server reconciled against real containers, so the list never presents intent as fact.
/// </summary>
public sealed class ApplicationRowViewModel(ApplicationDto application, Func<string, string> localize)
{
    public ApplicationDto Model { get; } = application;

    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public string SourceText => localize(DeploymentText.Enum(DeploymentText.SourcePrefix, Model.SourceKind));
    public string WorkloadText => localize(DeploymentText.Enum(DeploymentText.WorkloadPrefix, Model.WorkloadKind));
    public string DesiredText => localize(DeploymentText.Enum(DeploymentText.DesiredPrefix, Model.DesiredState));
    public string ActualText => localize(DeploymentText.Enum(DeploymentText.ActualPrefix, Model.ActualState));
    public string ReadinessText => localize(DeploymentText.Enum(DeploymentText.ReadinessPrefix, Model.ReadinessLevel));

    public string RevisionText => Model.CurrentRevisionNumber is { } number
        ? "#" + number.ToString(CultureInfo.CurrentCulture)
        : localize(DeploymentText.Prefix + ".never_deployed");

    /// <summary>
    /// The observed state itself, not its text. The list dot, the header badge and the status
    /// animation all derive their colour and glyph from this through one shared table, so the two
    /// sides of the workspace can never describe the same instance differently.
    /// </summary>
    public ApplicationActualState ActualState => Model.ActualState;

    /// <summary>True while the instance is starting or stopping, which is what the spinning glyph marks.</summary>
    public bool IsTransitioning => DeploymentStatusVisual.IsTransitioning(Model.ActualState);

    /// <summary>
    /// One line of what the instance is: what it is doing, what it is, and which revision is live.
    /// Ordered so the state is the first thing read, because that is the question the list exists to
    /// answer.
    /// </summary>
    public string SummaryText => $"{ActualText} · {SourceText} · {RevisionText}";

    public string DomainText => string.IsNullOrWhiteSpace(Model.Domain)
        ? localize(DeploymentText.Prefix + ".not_configured")
        : Model.Domain;

    public bool HasDrift => !string.IsNullOrWhiteSpace(Model.DriftProblemCode);
    public string DriftText => DeploymentText.Problem(Model.DriftProblemCode, localize);

    /// <summary>A failed or missing instance is highlighted; the rest stay neutral.</summary>
    public bool IsAttentionRequired => Model.ActualState is ApplicationActualState.Failed
        or ApplicationActualState.Missing or ApplicationActualState.Unknown || HasDrift;

    public string UpdatedText => Model.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
