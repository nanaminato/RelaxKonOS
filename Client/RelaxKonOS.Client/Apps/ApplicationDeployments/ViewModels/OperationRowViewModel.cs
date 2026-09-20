using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.ViewModels;

/// <summary>
/// One durable operation of an application. <see cref="ProgressText"/> distinguishes verified work
/// from an unknown denominator rather than fabricating a percentage the server never reported.
///
/// <see cref="DiagnosticsText"/> is the output of the step that produced the outcome, loaded on demand:
/// a problem code names a failure, but only the command's own text explains it, and the wizard's error
/// is gone the moment it is closed.
/// </summary>
public sealed partial class OperationRowViewModel(
    DeploymentOperationDto operation,
    Func<Guid, CancellationToken, Task<DeploymentOperationDiagnosticsDto?>>? loadDiagnostics = null) : ObservableObject
{
    private CancellationTokenSource? loading;

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

    /// <summary>Only a terminal operation with a recorded outcome can have output to show, and only a
    /// list that was given a way to fetch it offers the action at all.</summary>
    public bool CanLoadDiagnostics => loadDiagnostics is not null && !IsActive;

    [ObservableProperty] private string _diagnosticsText = string.Empty;
    [ObservableProperty] private bool _isDiagnosticsTruncated;
    [ObservableProperty] private bool _isLoadingDiagnostics;

    /// <summary>The pane is revealed only once there is something in it, so it never appears blank.</summary>
    public bool HasDiagnostics => DiagnosticsText.Length > 0;
    public bool ShowDiagnosticsAction => CanLoadDiagnostics && !HasDiagnostics;

    partial void OnDiagnosticsTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(ShowDiagnosticsAction));
    }

    [RelayCommand]
    private async Task LoadDiagnosticsAsync()
    {
        if (loadDiagnostics is null || HasDiagnostics || IsLoadingDiagnostics) return;
        loading?.Cancel();
        loading?.Dispose();
        loading = new CancellationTokenSource();
        IsLoadingDiagnostics = true;
        try
        {
            var diagnostics = await loadDiagnostics(Model.OperationId, loading.Token);
            if (diagnostics is not { Lines.Count: > 0 }) return;
            // The server already sanitized and length-limited every line; the client adds no framing.
            DiagnosticsText = string.Join(Environment.NewLine, diagnostics.Lines);
            IsDiagnosticsTruncated = diagnostics.Truncated;
        }
        catch (Exception exception) when (exception
            is ApplicationDeploymentClientException or HttpRequestException or InvalidOperationException
            or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            // The diagnosis is supplementary, so a failed load must not surface as an error of its own.
        }
        finally
        {
            IsLoadingDiagnostics = false;
        }
    }
}
