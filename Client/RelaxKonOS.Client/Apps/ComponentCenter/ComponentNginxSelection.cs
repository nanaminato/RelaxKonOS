using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.WebServers;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

internal sealed partial class ComponentNginxSelection(IServerWebServers client, IExternalServerConnection connection,
    HostPlatformKind? platform, IAppPermissionScope permissions, IWebServerAuthorization authorization) : ObservableObject
{
    public ObservableCollection<WebServerDto> Servers { get; } = [];
    public ObservableCollection<WebServerIntegrationCandidateDto> IntegrationCandidates { get; } = [];
    public bool IsWindowsServer => platform == HostPlatformKind.Windows;
    public bool HasOperationActivity => !OperationText.IsEmpty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectCurrentNginxCommand), nameof(UninstallManagedCommand))]
    private WebServerDto? _selectedServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IntegrateCommand))]
    private WebServerIntegrationCandidateDto? _selectedIntegrationCandidate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DiscoverCommand), nameof(SelectCurrentNginxCommand),
        nameof(IntegrateCommand), nameof(UninstallManagedCommand))]
    private bool _isBusy;

    [ObservableProperty] private LocalizedStatus _statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationActivity))]
    private LocalizedStatus _operationText;

    public Func<Task<bool>>? RequestIntegrationConfirmationAsync { get; set; }
    public Func<Task<bool>>? RequestManagedUninstallConfirmationAsync { get; set; }
    private Action? _dismissRuntimeFeedback;
    private Func<Task>? _uninstallRuntimeAsync;

    public void AttachManagedRuntimeActions(Action dismissFeedback, Func<Task> uninstallAsync)
    {
        _dismissRuntimeFeedback = dismissFeedback;
        _uninstallRuntimeAsync = uninstallAsync;
        UninstallManagedCommand.NotifyCanExecuteChanged();
    }

    private bool CanRead => connection.IsCurrent && permissions.IsGranted(AppPermissions.ServerWebServersRead);
    private bool CanManage => CanRead && permissions.IsGranted(AppPermissions.ServerWebServersManage);
    private bool CanRefresh => CanRead && !IsBusy;
    private bool CanSelect => CanManage && !IsBusy && SelectedServer is not null;
    private bool CanIntegrate => CanManage && !IsBusy && SelectedIntegrationCandidate is not null;
    private bool CanUninstall => CanSelect && _uninstallRuntimeAsync is not null && SelectedServer?.Capabilities?.CanUninstall == true;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => ReloadAsync(false);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task DiscoverAsync() => ReloadAsync(true);

    private async Task ReloadAsync(bool discover)
    {
        if (!CanRefresh) return;
        IsBusy = true;
        try { await LoadAsync(discover); }
        finally { IsBusy = false; }
    }

    private async Task LoadAsync(bool discover)
    {
        if (discover) StatusText = LocalizedStatus.Key("webservers.discover.running");
        try
        {
            var servers = (discover ? await client.DiscoverAsync() : await client.ListAsync()) ?? [];
            if (!CanRead) return;
            var candidates = await client.ListIntegrationCandidatesAsync() ?? [];
            if (!CanRead) return;
            var current = await client.GetCurrentNginxAsync();
            Clear();
            if (!CanRead) return;
            foreach (var server in servers) Servers.Add(server);
            foreach (var candidate in candidates) IntegrationCandidates.Add(candidate);
            SelectedServer = Servers.FirstOrDefault(server => server.Id == current?.ServerId);
            StatusText = LocalizedStatus.Format("webservers.discovery.summary", servers.Count + candidates.Count,
                servers.Count(server => server.ManagementMode == WebServerManagementMode.Managed),
                servers.Count(server => server.ManagementMode == WebServerManagementMode.Integrated), candidates.Count);
        }
        catch (Exception)
        {
            if (!discover) Clear();
            StatusText = LocalizedStatus.Format(discover ? "webservers.discover.failed" : "webservers.status.failed",
                LocalizedStatus.Key("webservers.error.request_failed"));
        }
    }

    private void Clear()
    {
        SelectedServer = null;
        SelectedIntegrationCandidate = null;
        Servers.Clear();
        IntegrationCandidates.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private async Task SelectCurrentNginxAsync()
    {
        if (!CanSelect || SelectedServer is not { } server) return;
        IsBusy = true;
        try
        {
            await client.SelectCurrentNginxAsync(new(server.Id));
            if (connection.IsCurrent) StatusText = LocalizedStatus.Key("webservers.current.saved");
        }
        catch (Exception) { StatusText = LocalizedStatus.Key("webservers.current.failed"); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanIntegrate))]
    private async Task IntegrateAsync()
    {
        if (!CanIntegrate || SelectedIntegrationCandidate is not { } candidate
            || RequestIntegrationConfirmationAsync is not { } confirm || !await confirm()
            || !CanIntegrate || SelectedIntegrationCandidate?.Id != candidate.Id) return;

        _dismissRuntimeFeedback?.Invoke();
        IsBusy = true;
        var label = LocalizedStatus.Key("webservers.operation.kind.integrate");
        OperationText = LocalizedStatus.Format("webservers.operation.starting", label);
        try
        {
            var operation = await authorization.ExecuteAsync(HostElevationCapability.NginxConfigurationWrite, candidate.Id, async () =>
            {
                if (!CanManage || SelectedIntegrationCandidate?.Id != candidate.Id)
                    throw new OperationCanceledException();
                return await client.IntegrateCandidateAsync(candidate.Id, new IntegrateWebServerRequest(true));
            });
            while (operation is { State: WebServerOperationState.Queued or WebServerOperationState.Running }
                && operation.OperationId != Guid.Empty)
            {
                OperationText = LocalizedStatus.Format("webservers.operation.progress", label,
                    LocalizedStatus.Key(operation.State == WebServerOperationState.Queued
                        ? "webservers.operation.stage.queued" : "webservers.operation.stage.running"));
                await Task.Delay(750);
                if (!CanRead) return;
                operation = await client.GetOperationAsync(operation.OperationId);
            }

            OperationText = operation switch
            {
                null => LocalizedStatus.Key("webservers.operation.not_found"),
                { OperationId: var id } when id == Guid.Empty => LocalizedStatus.Format("webservers.operation.rejected", Problem(operation.ProblemCode)),
                { State: WebServerOperationState.Succeeded } => LocalizedStatus.Format("webservers.operation.succeeded", label),
                { State: WebServerOperationState.Cancelled } => LocalizedStatus.Key("webservers.operation.cancelled"),
                _ => LocalizedStatus.Format("webservers.operation.failed", label, Problem(operation.ProblemCode)),
            };
            if (CanRead && operation?.State == WebServerOperationState.Succeeded) await LoadAsync(false);
        }
        catch (OperationCanceledException) { OperationText = LocalizedStatus.Key("webservers.operation.cancelled"); }
        catch (WebServerApiException ex) { OperationText = LocalizedStatus.Format("webservers.operation.failed", label, Problem(ex.ProblemCode)); }
        catch (Exception)
        {
            OperationText = LocalizedStatus.Format("webservers.operation.exception", label,
                LocalizedStatus.Key("webservers.error.request_failed"));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallManagedAsync()
    {
        if (!CanUninstall || _uninstallRuntimeAsync is not { } uninstall || SelectedServer is not { } original
            || RequestManagedUninstallConfirmationAsync is not { } confirm || !await confirm()
            || !CanUninstall || SelectedServer?.Id != original.Id) return;
        IsBusy = true;
        try
        {
            await uninstall();
            if (CanRead) await LoadAsync(false);
        }
        finally { IsBusy = false; }
    }

    private static LocalizedStatus Problem(string? problemCode)
    {
        if (WebServerAuthorization.FormatProblem(problemCode) is { } formatted) return LocalizedStatus.Literal(formatted);
        var unknown = LocalizedStatus.Key("webservers.problem.unknown");
        return problemCode?.StartsWith("webserver.", StringComparison.Ordinal) == true
            ? LocalizedStatus.Key("webservers.problem." + problemCode["webserver.".Length..], unknown.Resolve())
            : unknown;
    }
}
