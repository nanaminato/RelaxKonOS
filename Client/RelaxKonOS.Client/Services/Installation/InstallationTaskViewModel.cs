using System.Net;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Client.Services.Installation;

public sealed partial class InstallationTaskViewModel(InstallationClient client, IAppSettingsClient settings,
    InstallationServiceId service, string appId, Func<Task> refresh, Func<Task<string?>> passwordPrompt) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim submitGate = new(1, 1);
    private Task? observation;
    private string? pendingKey;
    [ObservableProperty] private InstallationOperationDto? operation;
    [ObservableProperty] private string connectionText = "";
    public bool IsActive => Operation?.State is InstallationOperationState.Queued or InstallationOperationState.Running;
    public bool IsIndeterminate => IsActive && Operation?.Progress is null;
    public int Progress => Operation?.Progress ?? 0;
    public string StageText => Operation is null ? "" : LocalizedText.Get("installation.stage." + Operation.Stage)
        + (Operation.Progress is { } value ? $" ({value}%)" : "")
        + (Operation.ProblemCode is { Length: > 0 } code ? " · " + FormatProblemCode(code) : "");
    public bool HasMessage => !string.IsNullOrWhiteSpace(StageText) || !string.IsNullOrWhiteSpace(ConnectionText);
    private bool CanCancel => IsActive && Operation?.Cancellable == true;
    partial void OnOperationChanged(InstallationOperationDto? value)
    {
        OnPropertyChanged(nameof(IsActive)); OnPropertyChanged(nameof(IsIndeterminate)); OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(StageText)); OnPropertyChanged(nameof(HasMessage));
        CancelCommand.NotifyCanExecuteChanged();
    }
    partial void OnConnectionTextChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    public async Task SubmitAsync(InstallationOperationKind kind, object options)
    {
        if (!await submitGate.WaitAsync(0)) return;
        try
        {
            if (IsActive) return;
            // Retain the key after an uncertain response; retry can never start a duplicate installer.
            pendingKey ??= Guid.NewGuid().ToString("N");
            InstallationOperationDto? submitted;
            try { submitted = await client.StartAsync(service, kind, options, pendingKey, lifetime.Token); }
            catch (InstallationApiException error) when (error.ProblemCode == "elevation-required")
            {
                var password = await passwordPrompt();
                if (string.IsNullOrEmpty(password)) return;
                try { if (!await client.ElevateAsync(service, password, lifetime.Token)) return; }
                finally { password = null; }
                submitted = await client.StartAsync(service, kind, options, pendingKey, lifetime.Token);
            }
            if (submitted is null) return;
            Operation = submitted; pendingKey = null; ConnectionText = "";
            await RememberAsync(submitted.OperationId);
            observation = ObserveAsync();
        }
        catch (InstallationApiException error)
        {
            ConnectionText = FormatProblemCode(error.ProblemCode);
            if (error.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Forbidden) pendingKey = null;
        }
        catch (OperationCanceledException) { }
        catch { ConnectionText = LocalizedText.Get("installation.connection_unavailable"); }
        finally { submitGate.Release(); }
    }

    public async Task<string?> CreateFileReferenceAsync(string path)
    {
        try
        {
            var reference = await client.CreateFileReferenceAsync(service, path, lifetime.Token);
            ConnectionText = string.Empty;
            return reference?.Id;
        }
        catch (InstallationApiException error) { ConnectionText = FormatProblemCode(error.ProblemCode); }
        catch (OperationCanceledException) { }
        catch { ConnectionText = LocalizedText.Get("installation.connection_unavailable"); }
        return null;
    }

    public async Task RestoreAsync()
    {
        if (observation is { IsCompleted: false }) return;
        try
        {
            var saved = await settings.GetAsync(appId, AppSettingsScope.Workspace, "installation", lifetime.Token);
            if (saved?.Value.TryGetProperty("operationId", out var value) == true && value.TryGetGuid(out var id))
                Operation = await client.GetAsync(id, lifetime.Token);
            var active = await client.GetActiveAsync(service, lifetime.Token);
            Operation = active ?? Operation;
            if (Operation is not null)
            {
                await RememberAsync(Operation.OperationId);
                observation = ObserveAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch { ConnectionText = LocalizedText.Get("installation.connection_unavailable"); }
    }

    private async Task RememberAsync(Guid id)
    {
        try
        {
            var existing = await settings.GetAsync(appId, AppSettingsScope.Workspace, "installation", lifetime.Token);
            await settings.SaveAsync(appId, AppSettingsScope.Workspace, "installation", JsonSerializer.SerializeToElement(new { operationId = id }),
                expectedRevision: existing?.Revision, cancellationToken: lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch { ConnectionText = LocalizedText.Get("installation.workspace_save_failed"); }
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
                    if (latest is null) { ConnectionText = LocalizedText.Get("installation.operation_unavailable"); return; }
                    Operation = latest; ConnectionText = "";
                }
                catch (InstallationApiException error) when (error.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                { ConnectionText = LocalizedText.Get("installation.operation_unavailable"); return; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch { ConnectionText = LocalizedText.Get("installation.connection_unavailable"); }
            }
            if (!lifetime.IsCancellationRequested) await refresh();
        }
        catch (OperationCanceledException) { }
        catch { ConnectionText = LocalizedText.Get("installation.refresh_failed"); }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        try { Operation = await client.CancelAsync(Operation!.OperationId, lifetime.Token) ?? Operation; }
        catch { ConnectionText = LocalizedText.Get("installation.cancel_unavailable"); }
    }

    /// <summary>
    /// Installation operations retain their domain problem code in the durable task record.
    /// Resolve it through that domain's resources instead of exposing a transport-facing code
    /// in the shared installation progress panel.
    /// </summary>
    internal static string FormatProblemCode(string? problemCode)
    {
        if (PrivilegedHelperProblemText.TryFormat(problemCode, out var helperMessage)) return helperMessage;
        if (string.IsNullOrWhiteSpace(problemCode)) return LocalizedText.Get("installation.problem.unknown");

        var candidates = new List<string>();
        if (problemCode.StartsWith("installation.", StringComparison.Ordinal))
            candidates.Add("installation.problem." + problemCode["installation.".Length..]);
        else
        {
            var separator = problemCode.IndexOf('.');
            if (separator > 0 && separator < problemCode.Length - 1)
            {
                var domain = problemCode[..separator] switch
                {
                    "tunnel" => "tunnels",
                    "nginx" => "webservers",
                    "smb" => "file_services",
                    _ => problemCode[..separator],
                };
                candidates.Add(domain + ".problem." + problemCode[(separator + 1)..].Replace('.', '_'));
            }
        }
        candidates.Add("installation.problem." + problemCode);
        foreach (var key in candidates)
        {
            var localized = LocalizedText.Get(key);
            if (!string.Equals(localized, key, StringComparison.Ordinal)) return localized;
        }
        return LocalizedText.Get("installation.problem.unknown");
    }
    public void Dispose() => lifetime.Cancel();
}
