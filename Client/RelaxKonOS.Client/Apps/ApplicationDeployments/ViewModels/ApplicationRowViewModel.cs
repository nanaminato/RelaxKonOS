using System.Globalization;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.ViewModels;

/// <summary>
/// One row of the application list. It exposes the definition together with the observed state the
/// server reconciled against real containers, so the list never presents intent as fact.
/// </summary>
public sealed class ApplicationRowViewModel(ApplicationDto application)
{
    public ApplicationDto Model { get; } = application;

    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public string SourceText => LocalizedText.Get(DeploymentText.Enum(DeploymentText.SourcePrefix, Model.SourceKind));
    public string WorkloadText => LocalizedText.Get(DeploymentText.Enum(DeploymentText.WorkloadPrefix, Model.WorkloadKind));
    public string DesiredText => LocalizedText.Get(DeploymentText.Enum(DeploymentText.DesiredPrefix, Model.DesiredState));
    public string ActualText => LocalizedText.Get(DeploymentText.Enum(DeploymentText.ActualPrefix, Model.ActualState));
    public string ReadinessText => LocalizedText.Get(DeploymentText.Enum(DeploymentText.ReadinessPrefix, Model.ReadinessLevel));

    public string RevisionText => Model.CurrentRevisionNumber is { } number
        ? "#" + number.ToString(CultureInfo.CurrentCulture)
        : LocalizedText.Get(DeploymentText.Prefix + ".never_deployed");

    /// <summary>The published entry point. A worker has none, and an empty cell is the honest answer.</summary>
    public string EndpointText => Model.Endpoint is { } endpoint
        ? $"{endpoint.BindAddress}:{endpoint.HostPort} → {endpoint.ContainerPort}"
        : "—";

    public string DomainText => string.IsNullOrWhiteSpace(Model.Domain) ? "—" : Model.Domain;

    public bool HasDrift => !string.IsNullOrWhiteSpace(Model.DriftProblemCode);
    public LocalizedStatus DriftText => DeploymentText.Problem(Model.DriftProblemCode);

    /// <summary>A failed or missing instance is highlighted; the rest stay neutral.</summary>
    public bool IsAttentionRequired => Model.ActualState is ApplicationActualState.Failed
        or ApplicationActualState.Missing or ApplicationActualState.Unknown || HasDrift;

    public string UpdatedText => Model.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
