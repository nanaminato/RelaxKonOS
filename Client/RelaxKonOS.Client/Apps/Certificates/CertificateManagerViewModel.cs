using System.Collections.ObjectModel;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Client.Apps.Certificates;

/// <summary>
/// Window-local certificate manager state. Long-running operations are tracked by id and polled
/// to a terminal state; private keys and ACME account material never leave the server.
/// </summary>
public sealed partial class CertificateManagerViewModel : LocalizedObservableObject
{
    private readonly IRemoteCertificateClient _client;
    private readonly IAuthSession _session;
    private readonly IAppPermissionScope _permissions;
    private CancellationTokenSource? _operationCts;
    private Guid? _activeOperationId;

    public CertificateManagerViewModel(IRemoteCertificateClient client, IAuthSession session, IAppPermissionScope permissions)
    {
        _client = client;
        _session = session;
        _permissions = permissions;
        ChallengeTypes =
        [
            Option(CertificateChallengeType.DirectHttp01, "certificates.challenge.direct_http01"),
            Option(CertificateChallengeType.WebRootHttp01, "certificates.challenge.webroot_http01"),
            Option(CertificateChallengeType.Dns01, "certificates.challenge.dns01"),
        ];
        KeyAlgorithms =
        [
            Option(CertificateKeyAlgorithm.EcdsaP256, "certificates.key.ecdsa_p256"),
            Option(CertificateKeyAlgorithm.Rsa2048, "certificates.key.rsa2048"),
        ];
        SelectedChallengeType = ChallengeTypes[0];
        SelectedKeyAlgorithm = KeyAlgorithms[0];
    }

    public ObservableCollection<CertificateDto> Certificates { get; } = [];
    public IReadOnlyList<CertificateOption<CertificateChallengeType>> ChallengeTypes { get; }
    public IReadOnlyList<CertificateOption<CertificateKeyAlgorithm>> KeyAlgorithms { get; }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DeployCommand), nameof(RenewCommand), nameof(RevokeCommand), nameof(DeleteCommand))]
    private CertificateDto? _selectedCertificate;
    [ObservableProperty] private LocalizedStatus _statusText = LocalizedText.Ref("certificates.status.loading");
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOperationActivity))]
    private LocalizedStatus _operationText;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(PreflightCommand), nameof(RequestCommand))]
    private bool _isLoading;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(PreflightCommand), nameof(RequestCommand), nameof(DeployCommand), nameof(RenewCommand), nameof(RevokeCommand), nameof(DeleteCommand), nameof(CancelOperationCommand))]
    private bool _isOperationRunning;
    [ObservableProperty] private string _domains = string.Empty;
    [ObservableProperty] private string _contactEmail = string.Empty;
    [ObservableProperty] private CertificateOption<CertificateChallengeType>? _selectedChallengeType;
    [ObservableProperty] private CertificateOption<CertificateKeyAlgorithm>? _selectedKeyAlgorithm;
    [ObservableProperty] private bool _acceptedTerms;
    [ObservableProperty] private bool _publicReachabilityConfirmed;
    [ObservableProperty] private string _selfSignedDomains = string.Empty;
    [ObservableProperty] private int _selfSignedValidityDays = 365;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPreflightResult))]
    private LocalizedStatus _preflightText;

    public bool IsRoot => string.Equals(_session.CurrentUser?.Username, "root", StringComparison.Ordinal);
    public bool HasOperationActivity => !string.IsNullOrWhiteSpace(OperationText);
    public bool HasPreflightResult => !string.IsNullOrWhiteSpace(PreflightText);

    public async Task StartAsync() => await RefreshAsync();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!HasReadPermission)
        {
            Certificates.Clear();
            SelectedCertificate = null;
            StatusText = LocalizedText.Ref("certificates.permission.read_required");
            return;
        }

        IsLoading = true;
        try
        {
            var certificates = await _client.ListAsync() ?? [];
            Certificates.Clear();
            SelectedCertificate = null;
            foreach (var certificate in certificates) Certificates.Add(certificate);
            StatusText = LocalizedText.Ref("certificates.status.ready", certificates.Count);
        }
        catch (Exception)
        {
            Certificates.Clear();
            SelectedCertificate = null;
            StatusText = LocalizedText.Ref("certificates.status.failed", LocalizedText.Get("certificates.error.request_failed"));
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task PreflightAsync()
    {
        if (!TryParseDomains(out var domains)) return;
        if (SelectedChallengeType is not { } challengeType)
        {
            PreflightText = LocalizedText.Ref("certificates.validation.challenge_required");
            return;
        }
        IsLoading = true;
        PreflightText = LocalizedText.Ref("certificates.preflight.running");
        try
        {
            var result = await _client.PreflightAsync(new CertificatePreflightRequest(domains, challengeType.Value));
            PreflightText = FormatPreflight(result);
        }
        catch (CertificateApiException exception)
        {
            PreflightText = LocalizedText.Ref("certificates.preflight.failed", ProblemText(exception.ProblemCode));
        }
        catch (Exception)
        {
            PreflightText = LocalizedText.Ref("certificates.preflight.failed", LocalizedText.Get("certificates.error.request_failed"));
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRequest))]
    private async Task RequestAsync() => await TryRequestCertificateAsync();

    /// <summary>Runs the request flow for the request dialog and reports whether issuance succeeded.</summary>
    public async Task<bool> TryRequestCertificateAsync()
    {
        if (!TryParseDomains(out var domains)) return false;
        if (SelectedChallengeType is not { } challengeType)
        {
            StatusText = LocalizedText.Ref("certificates.validation.challenge_required");
            return false;
        }
        if (SelectedKeyAlgorithm is not { } keyAlgorithm)
        {
            StatusText = LocalizedText.Ref("certificates.validation.key_algorithm_required");
            return false;
        }
        if (!AcceptedTerms)
        {
            StatusText = LocalizedText.Ref("certificates.validation.terms_required");
            return false;
        }
        if (string.IsNullOrWhiteSpace(ContactEmail))
        {
            StatusText = LocalizedText.Ref("certificates.validation.email_required");
            return false;
        }

        var request = new RequestCertificateRequest(
            domains,
            challengeType.Value,
            ContactEmail.Trim(),
            AcceptedTerms,
            keyAlgorithm.Value,
            PublicReachabilityConfirmed);
        return await RunOperationAsync(
            LocalizedText.Get("certificates.operation.request"),
            ct => _client.RequestAsync(request, ct),
            onSuccess: async op => { if (op.CertificateId is { } id) await SelectCertificateAsync(id, ct: default); },
            ct: default);
    }

    /// <summary>Creates a locally-issued certificate without contacting an ACME provider.</summary>
    public async Task<bool> TryCreateSelfSignedCertificateAsync()
    {
        var parsed = SelfSignedDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(domain => domain.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (parsed.Length == 0)
        {
            StatusText = LocalizedText.Ref("certificates.validation.domains_required");
            return false;
        }
        if (SelectedKeyAlgorithm is not { } keyAlgorithm)
        {
            StatusText = LocalizedText.Ref("certificates.validation.key_algorithm_required");
            return false;
        }
        if (SelfSignedValidityDays is < 1 or > 825)
        {
            StatusText = LocalizedText.Ref("certificates.validation.validity_days_invalid");
            return false;
        }
        return await RunOperationAsync(
            LocalizedText.Get("certificates.operation.create_self_signed"),
            ct => _client.CreateSelfSignedAsync(new CreateSelfSignedCertificateRequest(parsed, keyAlgorithm.Value, SelfSignedValidityDays), ct),
            onSuccess: async op => { if (op.CertificateId is { } id) await SelectCertificateAsync(id, ct: default); }, ct: default);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private Task DeployAsync() => RunOperationForSelectedAsync("deploy",
        (id, ct) => _client.DeployKestrelAsync(id, ct));

    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private Task RenewAsync() => RunOperationForSelectedAsync("renew",
        (id, ct) => _client.RenewAsync(id, ct));

    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private Task RevokeAsync() => RunOperationForSelectedAsync("revoke",
        (id, ct) => _client.RevokeAsync(id, new RevokeCertificateRequest(true), ct));

    [RelayCommand(CanExecute = nameof(CanActOnSelected))]
    private Task DeleteAsync() => RunOperationForSelectedAsync("delete",
        (id, ct) => _client.DeleteAsync(id, new DeleteCertificateRequest(true), ct));

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private async Task CancelOperationAsync() => await CancelActiveOperationAsync();

    /// <summary>Requests cancellation from the server before stopping this window's wait loop.</summary>
    public async Task CancelActiveOperationAsync()
    {
        if (_operationCts is null) return;
        try
        {
            if (_activeOperationId is { } operationId)
                await _client.CancelOperationAsync(operationId);
            await _operationCts.CancelAsync();
        }
        catch (Exception)
        {
            // Cancellation is best-effort: the poll loop and server operation both observe it.
        }
    }

    private async Task RunOperationForSelectedAsync(string kindKey, Func<Guid, CancellationToken, Task<CertificateOperationDto>> start)
    {
        if (SelectedCertificate is null) return;
        await RunOperationAsync(
            LocalizedText.Get($"certificates.operation.{kindKey}"),
            ct => start(SelectedCertificate.Id, ct),
            onSuccess: async op => { if (op.CertificateId is { } id) await SelectCertificateAsync(id, ct: default); },
            ct: default);
    }

    private async Task<bool> RunOperationAsync(string label, Func<CancellationToken, Task<CertificateOperationDto>> start, Func<CertificateOperationDto, Task>? onSuccess, CancellationToken ct)
    {
        if (!HasManagePermission)
        {
            StatusText = LocalizedText.Ref("certificates.permission.manage_required");
            return false;
        }

        _operationCts?.Dispose();
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _operationCts.Token;
        IsOperationRunning = true;
        OperationText = LocalizedText.Ref("certificates.operation.starting", label);
        try
        {
            var operation = await start(token);
            if (operation is null)
            {
                OperationText = LocalizedText.Ref("certificates.operation.not_found");
                return false;
            }
            if (operation.OperationId == Guid.Empty)
            {
                OperationText = LocalizedText.Ref("certificates.operation.rejected", ProblemText(operation.ProblemCode));
                return false;
            }
            _activeOperationId = operation.OperationId;
            operation = await PollOperationAsync(operation, token);
            if (operation.State == CertificateOperationState.Succeeded)
            {
                OperationText = LocalizedText.Ref("certificates.operation.succeeded", label);
                if (onSuccess is not null) await onSuccess(operation);
                await RefreshAsync();
                return true;
            }
            else if (operation.State == CertificateOperationState.Cancelled)
                OperationText = LocalizedText.Ref("certificates.operation.cancelled");
            else
                OperationText = LocalizedText.Ref("certificates.operation.failed", label, ProblemText(operation.ProblemCode));
            return false;
        }
        catch (OperationCanceledException)
        {
            OperationText = LocalizedText.Ref("certificates.operation.cancelled");
            return false;
        }
        catch (CertificateApiException exception)
        {
            OperationText = LocalizedText.Ref("certificates.operation.failed", label, ProblemText(exception.ProblemCode));
            return false;
        }
        catch (Exception)
        {
            OperationText = LocalizedText.Ref("certificates.operation.exception", label, LocalizedText.Get("certificates.error.request_failed"));
            return false;
        }
        finally
        {
            IsOperationRunning = false;
            _activeOperationId = null;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async Task<CertificateOperationDto> PollOperationAsync(CertificateOperationDto operation, CancellationToken cancellationToken)
    {
        while (operation.State is CertificateOperationState.Queued or CertificateOperationState.Running)
        {
            OperationText = LocalizedText.Ref("certificates.operation.progress", OperationName(operation.Kind), OperationStage(operation.Stage));
            await Task.Delay(PollInterval, cancellationToken);
            var updated = await _client.GetOperationAsync(operation.OperationId, cancellationToken);
            if (updated is null) break;
            operation = updated;
        }
        return operation;
    }

    private async Task SelectCertificateAsync(Guid id, CancellationToken ct)
    {
        var match = Certificates.FirstOrDefault(c => c.Id == id);
        if (match is not null) { SelectedCertificate = match; return; }
        try
        {
            var fresh = await _client.GetAsync(id, ct);
            if (fresh is not null) { Certificates.Add(fresh); SelectedCertificate = fresh; }
        }
        catch (Exception) { /* selection is best-effort; RefreshAsync already re-reads the list */ }
    }

    private bool TryParseDomains(out IReadOnlyList<string> domains)
    {
        var parsed = Domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parsed.Length == 0)
        {
            StatusText = LocalizedText.Ref("certificates.validation.domains_required");
            domains = Array.Empty<string>();
            return false;
        }
        domains = parsed;
        return true;
    }

    /// <summary>
    /// Composes the multi-line preflight report as a decomposable value rather than one resolved
    /// string, so the whole block re-localizes when the display language changes.
    /// </summary>
    private static LocalizedStatus FormatPreflight(CertificatePreflightResultDto result)
    {
        if (!result.CanProceed)
            return LocalizedText.Ref("certificates.preflight.cannot_proceed", ProblemText(result.ProblemCode));
        var lines = new List<LocalizedStatus> { LocalizedText.Ref("certificates.preflight.can_proceed") };
        if (result.Port80Available is { } port80)
            lines.Add(LocalizedText.Ref(port80 ? "certificates.preflight.port80_available" : "certificates.preflight.port80_unavailable"));
        if (result.RequiresAdministrator)
            lines.Add(LocalizedText.Ref("certificates.preflight.requires_admin"));
        foreach (var domain in result.Domains ?? [])
        {
            if (!string.IsNullOrEmpty(domain.ProblemCode))
                lines.Add(LocalizedText.Ref("certificates.preflight.domain_problem", domain.Domain, ProblemText(domain.ProblemCode)));
        }
        if (result.RequiresPublicReachabilityConfirmation)
            lines.Add(LocalizedText.Ref("certificates.preflight.confirm_reachability"));
        return LocalizedStatus.Join("\n", lines);
    }

    private bool HasReadPermission => _permissions.IsGranted(AppPermissions.ServerCertificatesRead);
    private bool HasManagePermission => HasReadPermission && _permissions.IsGranted(AppPermissions.ServerCertificatesManage);
    private bool CanRefresh => HasReadPermission && !IsLoading && !IsOperationRunning;
    private bool CanManage => HasManagePermission && !IsLoading && !IsOperationRunning;
    private bool CanRequest => CanManage;
    private bool CanActOnSelected => CanManage && SelectedCertificate is not null;
    private bool CanCancelOperation => IsOperationRunning;

    private static string OperationName(string? kind) => kind switch
    {
        "issue" or "request" => LocalizedText.Get("certificates.operation.request"),
        "create-self-signed" => LocalizedText.Get("certificates.operation.create_self_signed"),
        "deploy-kestrel" or "deploy" => LocalizedText.Get("certificates.operation.deploy"),
        "renew" => LocalizedText.Get("certificates.operation.renew"),
        "revoke" => LocalizedText.Get("certificates.operation.revoke"),
        "delete" => LocalizedText.Get("certificates.operation.delete"),
        _ => LocalizedText.Get("certificates.operation.unknown"),
    };

    private static string OperationStage(string? stage) => stage switch
    {
        "queued" => LocalizedText.Get("certificates.operation.stage.queued"),
        "running" => LocalizedText.Get("certificates.operation.stage.running"),
        "issue" => LocalizedText.Get("certificates.operation.stage.issuing"),
        "create-self-signed" => LocalizedText.Get("certificates.operation.stage.creating_self_signed"),
        "deploy-kestrel" => LocalizedText.Get("certificates.operation.stage.deploying"),
        "renew" => LocalizedText.Get("certificates.operation.stage.renewing"),
        "revoke" => LocalizedText.Get("certificates.operation.stage.revoking"),
        "delete" => LocalizedText.Get("certificates.operation.stage.deleting"),
        "cancelled" => LocalizedText.Get("certificates.operation.stage.cancelled"),
        "interrupted" => LocalizedText.Get("certificates.operation.stage.interrupted"),
        _ => LocalizedText.Get("certificates.operation.stage.unknown"),
    };

    private static string ProblemText(string? problemCode)
    {
        if (string.IsNullOrWhiteSpace(problemCode) || !problemCode.StartsWith("certificate.", StringComparison.Ordinal))
            return LocalizedText.Get("certificates.problem.unknown");
        return LocalizedText.Get($"certificates.problem.{problemCode["certificate.".Length..]}", LocalizedText.Get("certificates.problem.unknown"));
    }

    private static CertificateOption<TValue> Option<TValue>(TValue value, string labelKey) where TValue : struct => new(value, LocalizedText.Get(labelKey));

    // ACME issuance can take tens of seconds; poll gently to avoid hammering the host.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
}

/// <summary>Strongly-typed choice for a combo box. Keeps the enum value while showing a localized label.</summary>
public sealed record CertificateOption<TValue>(TValue Value, string Label) where TValue : struct;
