using System.Net;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.AppSDK;

public sealed record InstallationTaskText(Func<string, string> Get, Func<string?, string> FormatProblemCode,
    Func<string?, bool> IsPrivilegedHelperFailure, Func<Exception, string?> GetAuthorizationProblem);

public sealed partial class InstallationTaskViewModel(InstallationTaskText text, IInstallationOperations client, IExternalAppSettings settings,
    InstallationServiceId service, bool retainFailedReceipt, Func<Task> refresh, Func<Task<bool>> requestElevation, string workspaceKey = "installation") : ObservableObject, IDisposable
{
    private IInstallationPackageOperations PackageOperations => client as IInstallationPackageOperations
        ?? throw new NotSupportedException("Package staging is unavailable for this installation task.");

    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim submitGate = new(1, 1);
    private Task? observation;
    private string? pendingKey;
    public bool HasPendingSubmission => pendingKey is not null;
    private CancellationTokenSource? transferCancellation;
    private InstallationStage transferStage;
    [ObservableProperty] private bool isTransferring;
    partial void OnIsTransferringChanged(bool value) => OnOperationChanged(Operation);
    public Func<string?, Task>? ShowPrivilegedHelperUnavailableAsync { get; set; }
    [ObservableProperty] private InstallationOperationDto? operation;
    [ObservableProperty] private string connectionText = "";
    [ObservableProperty] private bool isOperationLogExpanded;
    [ObservableProperty] private string operationLog = "";
    public string SummaryText => string.IsNullOrWhiteSpace(ConnectionText) ? StageText : ConnectionText;
    private string lastMessage = "";
    private void RecordProgress()
    {
        OnPropertyChanged(nameof(SummaryText));
        var message = SummaryText;
        if (string.IsNullOrWhiteSpace(message) || message == lastMessage) return;
        lastMessage = message;
        OperationLog = string.Join(Environment.NewLine,
            OperationLog.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Append($"[{DateTime.Now:HH:mm:ss}] {message}").TakeLast(200));
    }
    public bool IsActive => IsTransferring || Operation?.State is InstallationOperationState.Queued or InstallationOperationState.Running;
    public bool IsIndeterminate => IsTransferring || IsActive && Operation?.Progress is null;
    public int Progress => IsTransferring ? 0 : Operation?.Progress ?? 0;
    public string StageText => IsTransferring ? text.Get("installation.stage." + transferStage) : Operation is null ? "" : text.Get("installation.stage." + Operation.Stage)
        + (Operation.Progress is { } value ? $" ({value}%)" : "")
        + (Operation.ProblemCode is { Length: > 0 } code ? " · " + text.FormatProblemCode(code) : "");
    public bool HasMessage => !string.IsNullOrWhiteSpace(StageText) || !string.IsNullOrWhiteSpace(ConnectionText);
    private bool CanCancel => IsTransferring || IsActive && Operation?.Cancellable == true;
    partial void OnOperationChanged(InstallationOperationDto? value)
    {
        OnPropertyChanged(nameof(IsActive)); OnPropertyChanged(nameof(IsIndeterminate)); OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(StageText)); OnPropertyChanged(nameof(HasMessage));
        CancelCommand.NotifyCanExecuteChanged();
        RecordProgress();
    }
    partial void OnConnectionTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasMessage));
        RecordProgress();
    }

    /// <summary>
    /// Removes feedback from a completed installation before the host app starts an unrelated
    /// operation. An active installation is deliberately never cleared.
    /// </summary>
    public void DismissInactiveFeedback()
    {
        if (IsActive) return;
        Operation = null;
        ConnectionText = string.Empty;
    }

    public async Task SubmitAsync(InstallationOperationKind kind, object options)
    {
        if (!await submitGate.WaitAsync(0)) return;
        try
        {
            if (IsActive) return;
            // Retain the key after an uncertain response; retry can never start a duplicate installer.
            if (options is ManagedRuntimeInstallationRequest { Plan: { } runtimePlan }) pendingKey = runtimePlan.Request.IdempotencyKey;
            else if (options is ManagedRuntimeInstallationRequest { FrozenPackage: { } runtimePackage }) pendingKey = runtimePackage.Request.IdempotencyKey;
            else if (options is MihomoInstallationRequest { FrozenPackage: { } mihomoPackage }) pendingKey = mihomoPackage.Request.IdempotencyKey;
            if (options is MihomoInstallationRequest { Plan: { } mihomoPlan }) pendingKey = mihomoPlan.Request.IdempotencyKey;
            else if (options is NginxInstallationRequest { FrozenPackage: { } package }) pendingKey = package.Request.IdempotencyKey;
            else if (options is FrpInstallationRequest { FrozenArchive: { } bundle }) pendingKey = bundle.Request.IdempotencyKey;
            else if (options is RelaxKonOS.Protocol.Databases.PostgreSqlInstallationRequest { FrozenPackage: { } postgresql }) pendingKey = postgresql.Request.IdempotencyKey;
            else if (options is SquidInstallationRequest { FrozenPackage: { } squid }) pendingKey = squid.Request.IdempotencyKey;
            else if (options is SmbInstallationRequest { FrozenPackage: { } samba }) pendingKey = samba.Request.IdempotencyKey;
            else if (options is DockerInstallationRequest { FrozenPackage: { } docker }) pendingKey = docker.Request.IdempotencyKey;
            else if (options is TailscaleInstallationRequest { FrozenPackage: { } tailscale }) pendingKey = tailscale.Request.IdempotencyKey;
            else pendingKey ??= Guid.NewGuid().ToString("N");
            InstallationOperationDto? submitted;
            try { submitted = await client.StartAsync(service, kind, options, pendingKey, lifetime.Token); }
            catch (InstallationApiException error) when (error.ProblemCode == "elevation-required")
            {
                if (!await requestElevation()) return;
                submitted = await client.StartAsync(service, kind, options, pendingKey, lifetime.Token);
            }
            if (submitted is null) return;
            Operation = submitted; pendingKey = null; ConnectionText = "";
            await RememberAsync(submitted.OperationId);
            observation = ObserveAsync();
        }
        catch (InstallationApiException error)
        {
            ConnectionText = text.FormatProblemCode(error.ProblemCode);
            if (error.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Forbidden) pendingKey = null;
            await ShowHelperFailureAsync(error.ProblemCode);
        }
        catch (Exception error) when (text.GetAuthorizationProblem(error) is { } code)
        {
            ConnectionText = text.FormatProblemCode(code);
            await ShowHelperFailureAsync(code);
        }
        catch (OperationCanceledException) { }
        catch { ConnectionText = text.Get("installation.connection_unavailable"); }
        finally { submitGate.Release(); }
    }

    public async Task<string?> CreateFileReferenceAsync(string path)
    {
        try
        {
            var reference = await PackageOperations.CreateFileReferenceAsync(service, path, lifetime.Token);
            ConnectionText = string.Empty;
            return reference?.Id;
        }
        catch (InstallationApiException error)
        {
            ConnectionText = text.FormatProblemCode(error.ProblemCode);
            await ShowHelperFailureAsync(error.ProblemCode);
        }
        catch (OperationCanceledException) { }
        catch { ConnectionText = text.Get("installation.connection_unavailable"); }
        return null;
    }

    /// <summary>Fetches a fixed publisher URL through the desktop host, then uploads the finished
    /// archive to the server's actor-bound installation staging area.</summary>
    public async Task<string?> DownloadAndUploadPackageAsync(string url, string fileName)
        => await TransferPackageAsync(InstallationStage.Downloading,
            token => PackageOperations.DownloadAndUploadPackageAsync(service, url, fileName, token));

    private async Task<string?> TransferPackageAsync(InstallationStage stage,
        Func<CancellationToken, Task<InstallationFileReferenceDto?>> transfer)
    {
        if (IsActive) return null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        transferCancellation = cancellation;
        transferStage = stage;
        ConnectionText = string.Empty;
        IsTransferring = true;
        try
        {
            var reference = await transfer(cancellation.Token);
            ConnectionText = string.Empty;
            return reference?.Id;
        }
        catch (InstallationApiException error)
        {
            ConnectionText = text.FormatProblemCode(error.ProblemCode);
            await ShowHelperFailureAsync(error.ProblemCode);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!lifetime.IsCancellationRequested)
            {
                Operation = null;
                ConnectionText = text.Get("installation.stage." + InstallationStage.Cancelled);
            }
        }
        catch { ConnectionText = text.Get("installation.connection_unavailable"); }
        finally { IsTransferring = false; transferCancellation = null; }
        return null;
    }

    public async Task<string?> UploadPackageAsync(string fileName, Stream content)
        => await TransferPackageAsync(InstallationStage.Copying,
            token => PackageOperations.UploadPackageAsync(service, fileName, content, token));

    public async Task RestoreAsync()
    {
        if (IsTransferring) return;
        if (observation is { IsCompleted: false }) return;
        try
        {
            InstallationOperationDto? remembered = null;
            var saved = await settings.GetAsync(key: workspaceKey, cancellationToken: lifetime.Token);
            if (saved?.Value.TryGetProperty("operationId", out var value) == true && value.TryGetGuid(out var id))
                remembered = await client.GetAsync(id, lifetime.Token);
            if (remembered?.Service != service) remembered = null;
            var active = await client.GetActiveAsync(service, lifetime.Token);
            // A terminal task may be retained in workspace settings for recovery diagnostics, but
            // it must not reappear as the current app's installation banner after navigation.
            Operation = active ?? (remembered?.State is InstallationOperationState.Queued or InstallationOperationState.Running
                || retainFailedReceipt && remembered?.State is InstallationOperationState.Interrupted or InstallationOperationState.Failed
                ? remembered : null);
            if (Operation is not null)
            {
                await RememberAsync(Operation.OperationId);
                observation = ObserveAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch { ConnectionText = text.Get("installation.connection_unavailable"); }
    }

    private async Task RememberAsync(Guid id)
    {
        try
        {
            var existing = await settings.GetAsync(key: workspaceKey, cancellationToken: lifetime.Token);
            await settings.SetAsync(JsonSerializer.SerializeToElement(new { operationId = id }), key: workspaceKey,
                expectedRevision: existing?.Revision, cancellationToken: lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch { ConnectionText = text.Get("installation.workspace_save_failed"); }
    }

    private async Task ObserveAsync()
    {
        var delay = 500;
        try
        {
            while (IsActive && !lifetime.IsCancellationRequested)
            {
                await Task.Delay(delay, lifetime.Token); delay = Math.Min(delay * 2, 2000);
                try
                {
                    var latest = await client.GetAsync(Operation!.OperationId, lifetime.Token);
                    if (latest is null) { ConnectionText = text.Get("installation.operation_unavailable"); return; }
                    Operation = latest; ConnectionText = "";
                }
                catch (InstallationApiException error) when (error.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                { ConnectionText = text.Get("installation.operation_unavailable"); return; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch { ConnectionText = text.Get("installation.connection_unavailable"); }
            }
            if (!lifetime.IsCancellationRequested)
            {
                if (Operation?.State is InstallationOperationState.Failed or InstallationOperationState.Interrupted)
                    await ShowHelperFailureAsync(Operation.ProblemCode);
                await refresh();
            }
        }
        catch (OperationCanceledException) { }
        catch { ConnectionText = text.Get("installation.refresh_failed"); }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (transferCancellation is { } transfer) { transfer.Cancel(); return; }
        try { Operation = await client.CancelAsync(Operation!.OperationId, lifetime.Token) ?? Operation; }
        catch { ConnectionText = text.Get("installation.cancel_unavailable"); }
    }

    private Task ShowHelperFailureAsync(string? problemCode) =>
        text.IsPrivilegedHelperFailure(problemCode)
            ? ShowPrivilegedHelperUnavailableAsync?.Invoke(problemCode) ?? Task.CompletedTask
            : Task.CompletedTask;

    public void Dispose() => lifetime.Cancel();
}
