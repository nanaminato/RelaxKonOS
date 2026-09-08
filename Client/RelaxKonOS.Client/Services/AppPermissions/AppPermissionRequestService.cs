using System.Collections.Concurrent;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Apps.Settings.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

/// <summary>Shell implementation of Android-style, app-owned runtime permission prompts.</summary>
public sealed class AppPermissionRequestService : IAppPermissionRequestService
{
    private readonly ApplicationManager _applications;
    private readonly IAppPermissionManager _permissions;
    private readonly IWindowManager _windows;
    private readonly LocalizationService _localization;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestLocks = new(StringComparer.Ordinal);

    public AppPermissionRequestService(
        ApplicationManager applications,
        IAppPermissionManager permissions,
        IWindowManager windows,
        LocalizationService localization)
    {
        _applications = applications;
        _permissions = permissions;
        _windows = windows;
        _localization = localization;
    }

    public AppPermissionStatus GetStatus(AppId appId, string permissionId) =>
        IsDeclared(appId, permissionId) ? _permissions.GetStatus(appId, permissionId) : AppPermissionStatus.Undecided;

    public async Task RequestUndecidedAsync(AppId appId, CancellationToken cancellationToken = default)
    {
        // V2 intentionally never asks for every declared capability during application launch.
        // A package requests a capability at the point it is used through its scoped SDK context.
        await Task.CompletedTask;
    }

    public Task<AppPermissionStatus> RequestAsync(AppId appId, string permissionId, CancellationToken cancellationToken = default) =>
        RequestCoreAsync(appId, permissionId, onlyIfUndecided: false, cancellationToken);

    public async Task OpenSettingsAsync(AppId appId)
    {
        if (_applications.GetManifest(appId) is null)
            return;
        await OnUiThreadAsync(() =>
        {
            _applications.Activate(new AppActivationRequest(RelaxKonOSActivationUris.SettingsAppPermissions(appId), appId));
            return Task.FromResult(true);
        });
    }

    private async Task<AppPermissionStatus> RequestCoreAsync(
        AppId appId, string permissionId, bool onlyIfUndecided, CancellationToken cancellationToken)
    {
        if (!IsDeclared(appId, permissionId))
            return AppPermissionStatus.Undecided;

        var gate = _requestLocks.GetOrAdd($"{appId.Value}\u001f{permissionId}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var before = _permissions.GetStatus(appId, permissionId);
            if (onlyIfUndecided && before != AppPermissionStatus.Undecided)
                return before;

            var decision = await OnUiThreadAsync(() => ShowPromptAsync(appId, permissionId, cancellationToken));
            if (decision is { } persisted)
            {
                _permissions.SetStatus(appId, permissionId, persisted);
                return persisted;
            }

            return before;
        }
        finally { gate.Release(); }
    }

    private async Task<AppPermissionStatus?> ShowPromptAsync(
        AppId appId, string permissionId, CancellationToken cancellationToken)
    {
        var app = _applications.Registered.FirstOrDefault(candidate => candidate.Id == appId);
        var permission = CoreAppPermissions.Find(permissionId);
        var owner = await WaitForOwnerAsync(appId, cancellationToken);
        if (app is null || permission is null || owner is null)
            return null;

        AppPermissionRequestDialogViewModel? viewModel = null;
        var result = await _windows.ShowDialogAsync<AppPermissionStatus?>(owner,
            LocalizedText.Get("permission.request.title", "Permission request"),
            dialog => new AppPermissionRequestDialogView
            {
                DataContext = viewModel = new AppPermissionRequestDialogViewModel(app, permission, _localization, dialog.Close),
            },
            new Size(460, 290));
        viewModel?.Dispose();
        return result;
    }

    private Task<ManagedWindow?> WaitForOwnerAsync(AppId appId, CancellationToken cancellationToken)
    {
        var existing = FindOwner(appId);
        if (existing is not null)
            return Task.FromResult<ManagedWindow?>(existing);

        var completion = new TaskCompletionSource<ManagedWindow?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ManagedWindow>? opened = null;
        CancellationTokenRegistration cancellation = default;
        opened = (_, window) =>
        {
            if (window.Info.OwnerAppId == appId && !window.IsModalDialog)
                completion.TrySetResult(window);
        };
        _windows.WindowOpened += opened;
        cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _ = completion.Task.ContinueWith(_ =>
        {
            _windows.WindowOpened -= opened;
            cancellation.Dispose();
        }, TaskScheduler.Default);
        return completion.Task;
    }

    private ManagedWindow? FindOwner(AppId appId) => _windows.Windows
        .LastOrDefault(window => window.Info.OwnerAppId == appId && !window.IsModalDialog);

    private bool IsDeclared(AppId appId, string permissionId) =>
        CoreAppPermissions.IsKnown(permissionId)
        && _applications.GetManifest(appId)?.Permissions.Contains(permissionId, StringComparer.Ordinal) == true;

    private static Task<T> OnUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task;
    }
}
