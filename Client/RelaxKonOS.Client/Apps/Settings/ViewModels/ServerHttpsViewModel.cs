using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

internal sealed partial class ServerHttpsViewModel(IServerCertificates client, IAppPermissionScope permissions,
    CancellationToken lifetime = default) : ObservableObject
{
    public ObservableCollection<CertificateDto> Certificates { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCertificateDomains))]
    [NotifyCanExecuteChangedFor(nameof(DeployCommand))]
    private CertificateDto? _selectedCertificate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DeployCommand))]
    private bool _isOperationRunning;

    [ObservableProperty] private string _operationLog = "";
    [ObservableProperty] private string _statusText = "";

    public string SelectedCertificateDomains => SelectedCertificate is { } certificate
        ? string.Join(", ", certificate.SubjectAlternativeNames)
        : "";

    private bool CanRead => permissions.IsGranted(AppPermissions.ServerCertificatesRead);
    private bool CanRefresh => CanRead && !IsOperationRunning;
    private bool CanDeploy => CanRefresh && SelectedCertificate is not null
        && permissions.IsGranted(AppPermissions.ServerCertificatesManage);

    public async Task StartAsync()
    {
        if (CanRead) await RefreshAsync();
        else StatusText = LocalizedText.Get("certificates.permission.read_required");
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        IsOperationRunning = true;
        try { await LoadCertificatesAsync(SelectedCertificate?.Id); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { IsOperationRunning = false; }
    }

    private async Task LoadCertificatesAsync(Guid? selection)
    {
        try
        {
            var certificates = await client.ListAsync(lifetime) ?? [];
            Certificates.Clear();
            foreach (var certificate in certificates) Certificates.Add(certificate);
            SelectedCertificate = Certificates.FirstOrDefault(certificate => certificate.Id == selection);
            StatusText = LocalizedText.Format("certificates.status.ready", Certificates.Count);
        }
        catch (Exception) when (!lifetime.IsCancellationRequested)
        {
            Certificates.Clear();
            SelectedCertificate = null;
            StatusText = LocalizedText.Format("certificates.status.failed", LocalizedText.Get("certificates.error.request_failed"));
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private async Task DeployAsync()
    {
        if (!CanDeploy || SelectedCertificate is not { } certificate) return;
        IsOperationRunning = true;
        OperationLog = "";
        var label = LocalizedText.Get("certificates.operation.deploy");
        Guid? operationId = null;
        Report(LocalizedText.Format("certificates.operation.starting", label));
        try
        {
            var operation = await client.DeployKestrelAsync(certificate.Id, lifetime);
            if (operation is null)
            {
                Report(LocalizedText.Get("certificates.operation.not_found"));
                return;
            }
            if (operation.OperationId == Guid.Empty)
            {
                Report(LocalizedText.Format("certificates.operation.rejected", Problem(operation.ProblemCode)));
                return;
            }

            operationId = operation.OperationId;
            while (operation.State is CertificateOperationState.Queued or CertificateOperationState.Running)
            {
                var stageKey = operation.Stage switch
                {
                    "queued" => "certificates.operation.stage.queued",
                    "running" => "certificates.operation.stage.running",
                    "deploy-kestrel" => "certificates.operation.stage.deploying",
                    "cancel-requested" => "certificates.operation.stage.cancel_requested",
                    _ => "certificates.operation.stage.unknown",
                };
                Report(LocalizedText.Format("certificates.operation.progress", label, LocalizedText.Get(stageKey)));
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime);
                var updated = await client.GetOperationAsync(operation.OperationId, lifetime);
                if (updated is null)
                {
                    Report(LocalizedText.Get("certificates.operation.not_found"));
                    return;
                }
                operation = updated;
            }

            if (operation.State == CertificateOperationState.Succeeded)
            {
                await LoadCertificatesAsync(operation.CertificateId ?? certificate.Id);
                Report(LocalizedText.Format("certificates.operation.succeeded", label));
            }
            else if (operation.State == CertificateOperationState.Cancelled)
                Report(LocalizedText.Get("certificates.operation.cancelled"));
            else
                Report(LocalizedText.Format("certificates.operation.failed", label, Problem(operation.ProblemCode)));
        }
        catch (OperationCanceledException)
        {
            Report(LocalizedText.Format("certificates.operation.observation_stopped", operationId?.ToString("D") ?? "—"));
        }
        catch (CertificateApiException ex)
        {
            Report(LocalizedText.Format("certificates.operation.failed", label, Problem(ex.ProblemCode)));
        }
        catch (Exception)
        {
            Report(LocalizedText.Format("certificates.operation.exception", label, LocalizedText.Get("certificates.error.request_failed")));
        }
        finally { IsOperationRunning = false; }
    }

    private static string Problem(string? code)
    {
        if (code == "elevation-required") return LocalizedText.Get("certificates.problem.elevation_required");
        var unknown = LocalizedText.Get("certificates.problem.unknown");
        return code?.StartsWith("certificate.", StringComparison.Ordinal) == true
            ? LocalizedText.Get($"certificates.problem.{code["certificate.".Length..]}", unknown)
            : unknown;
    }

    private void Report(string text)
    {
        if (StatusText == text) return;
        StatusText = text;
        OperationLog = string.IsNullOrEmpty(OperationLog) ? text : OperationLog + Environment.NewLine + text;
    }
}
