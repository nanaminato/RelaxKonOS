using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Terminal;

/// <summary>Opens and coordinates a terminal workspace through the SDK window service.</summary>
public static class TerminalWorkspace
{
    private static readonly SemaphoreSlim Opening = new(1, 1);

    public static async Task ActivateAsync(IExternalAppContext context, bool restoreOnly,
        Action<string?, bool> openSession, CancellationToken cancellationToken)
    {
        await Opening.WaitAsync(cancellationToken);
        try
        {
            if (context.TerminalSessions is not { } discovery)
            {
                if (!restoreOnly) openSession(null, false);
                return;
            }

            IReadOnlyList<RelaxKonOS.Protocol.Hubs.TerminalSessionInfo> sessions;
            try { sessions = await discovery.ListAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch
            {
                if (!restoreOnly && discovery.IsCurrent) openSession(null, false);
                return;
            }

            if (cancellationToken.IsCancellationRequested || !discovery.IsCurrent) return;
            var opened = false;
            foreach (var session in sessions.Where(session => !session.HasExited).OrderBy(session => session.CreatedAt))
            {
                if (cancellationToken.IsCancellationRequested || !discovery.IsCurrent) return;
                if (!TerminalSessionReservations.TryReserve(session.SessionId)) continue;
                try { openSession(session.SessionId, session.IsAdministrator); }
                catch
                {
                    TerminalSessionReservations.Release(session.SessionId);
                    throw;
                }
                opened = true;
            }
            if (!opened && !restoreOnly) openSession(null, false);
        }
        finally { Opening.Release(); }
    }

    public static void Open(IExternalAppContext context, string? iconGlyph,
        Func<string?, string?, bool, TerminalTabViewModel> createSession, Func<Task> openEnvironment,
        Action<string?> openAdministrator, string? sessionId, string? workingDirectory, bool isAdministrator)
    {
        string Label(string key) => TerminalLocalization.Resolve(context.SystemLanguage, key);
        var workspace = new TerminalWorkspaceView(context.SystemLanguage);
        var owner = context.Windows.ShowWindow(
            Label(isAdministrator ? "terminal.administrator_title" : "terminal.window_title"),
            workspace, new Rect(120, 80, 820, 540), iconGlyph);
        var window = owner.Window;
        void RefreshVisibility() => workspace.SetNativeSurfacesVisible(window.IsActive && window.IsOnScreen);
        RefreshVisibility();
        window.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ManagedWindow.IsActive) or nameof(ManagedWindow.State))
                RefreshVisibility();
        };
        window.FocusRequested += (_, _) => RefreshVisibility();
        window.View.BoundsInteractionStarted += (_, _) => workspace.SetNativeSurfacesVisible(false);
        window.View.BoundsInteractionCompleted += (_, _) => RefreshVisibility();

        var closingTab = false;
        void AddSession(string? restoredId)
        {
            var model = createSession(restoredId, workingDirectory, isAdministrator);
            model.RequestEnvironmentAsync = openEnvironment;
            model.RequestAdministratorTerminal = () => openAdministrator(workingDirectory);
            model.RequestSettingsAsync = async () =>
            {
                workspace.SetNativeSurfacesVisible(false);
                try
                {
                    await owner.ShowDialogAsync<bool>(Label("terminal.settings"), dialog =>
                    {
                        model.CloseSettingsAction = () => dialog.Close(true);
                        return new TerminalSettingsView { DataContext = model };
                    }, new Size(460, 440));
                }
                finally
                {
                    model.CloseSettingsAction = null;
                    RefreshVisibility();
                }
            };
            workspace.AddTab(model, new TerminalView { DataContext = model });
        }

        Task<bool> ConfirmCloseAsync(string message) => owner.ConfirmAsync(
            Label("terminal.close_active.title"), Label(message), Label("terminal.close_active.confirm"));

        workspace.RequestNewTab = () => AddSession(null);
        workspace.RequestCloseTabAsync = async model =>
        {
            if (closingTab || !workspace.Tabs.Contains(model)) return;
            if (workspace.Tabs.Count == 1)
            {
                window.CloseCommand.Execute(null);
                return;
            }
            closingTab = true;
            try
            {
                if (!model.HasLiveSession || await ConfirmCloseAsync("terminal.close_tab.message"))
                {
                    workspace.RemoveTab(model);
                    model.Detach();
                }
            }
            finally { closingTab = false; }
        };
        window.CanCloseAsync = async () => !closingTab && (!workspace.Tabs.Any(model => model.HasLiveSession)
            || await ConfirmCloseAsync("terminal.close_active.message") && !closingTab);
        owner.Closed.Register(() =>
        {
            foreach (var model in workspace.Tabs) model.Detach();
        });
        AddSession(sessionId);
    }
}
