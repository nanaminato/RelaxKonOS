using System.Globalization;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.ViewModels;

/// <summary>
/// One durable operation of an application. <see cref="ProgressText"/> distinguishes verified work
/// from an unknown denominator rather than fabricating a percentage the server never reported.
/// </summary>
public sealed class OperationRowViewModel(DeploymentOperationDto operation)
{
    public DeploymentOperationDto Model { get; } = operation;

    public Guid Id => Model.OperationId;
    public string KindText => LocalizedText.Get(DeploymentText.Enum(DeploymentText.KindPrefix, Model.Kind));
    public string StateText => LocalizedText.Get(DeploymentText.Enum(DeploymentText.StatePrefix, Model.State));
    public string StageText => LocalizedText.Get(DeploymentText.Enum(DeploymentText.StagePrefix, Model.Stage));

    public string RevisionText => Model.TargetRevisionNumber is { } number
        ? "#" + number.ToString(CultureInfo.CurrentCulture)
        : "—";

    /// <summary>Null progress means no reliable denominator exists, so no percentage is shown.</summary>
    public string ProgressText => Model.Progress is { } percent
        ? percent.ToString(CultureInfo.CurrentCulture) + "%"
        : LocalizedText.Get(DeploymentText.Prefix + ".progress_unknown");

    public string StartedText => (Model.StartedAt ?? Model.CreatedAt).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string FinishedText => Model.CompletedAt is { } completed
        ? completed.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : LocalizedText.Get(DeploymentText.Prefix + ".operation_in_progress");

    public bool HasProblem => !string.IsNullOrWhiteSpace(Model.ProblemCode);
    public LocalizedStatus ProblemText => DeploymentText.Problem(Model.ProblemCode);

    /// <summary>A failed recovery is reported on its own line, next to the original failure.</summary>
    public bool HasRecoveryProblem => !string.IsNullOrWhiteSpace(Model.RecoveryProblemCode);
    public LocalizedStatus RecoveryText => DeploymentText.Problem(Model.RecoveryProblemCode);

    public bool IsActive => Model.State is DeploymentOperationState.Queued or DeploymentOperationState.Running;
    public bool CanCancel => Model.Cancellable && IsActive;

    public string IdentifierText => Model.OperationId.ToString("D", CultureInfo.InvariantCulture);
}
