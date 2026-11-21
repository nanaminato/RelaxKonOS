using Avalonia.Utilities;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.App.ProcessGuardian;
using RelaxKonOS.Client.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ProcessGuardian;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ProcessGuardian;

/// <summary>Built-in UI for workloads supervised by the separately installed Guardian Agent.</summary>
public sealed class ProcessGuardianApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.processguardian"), "Process Guardian", "0.1.0", "🛡", "View RelaxKonOS Guardian Agent workloads", [AppPermissions.ServerGuardianRead, AppPermissions.ServerGuardianManage], ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Guardian]), InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IServerGuardian)) as IServerGuardian;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(Label("application.relaxkonos.processguardian.display_name"),
                new TextBlock { Text = Label("guardian.login_required"), Margin = new Avalonia.Thickness(24), TextWrapping = TextWrapping.Wrap },
                new Rect(200, 160, 460, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var viewModel = new ProcessGuardianViewModel(client, () => session.CurrentUser?.Username,
            () => session.CurrentServer?.Platform == HostPlatformKind.Windows, Label,
            code => PrivilegedHelperProblemText.TryFormat(code, out var message) ? message : null);
        var localization = App.Services.GetRequiredService<LocalizationService>();
        WeakEventHandlerManager.Subscribe<LocalizationService, SystemLanguageChangedEventArgs, ProcessGuardianViewModel>(
            localization, nameof(LocalizationService.LanguageChanged), viewModel.OnLanguageChanged);
        viewModel.OpenComponentManagement = () => (context.Services.GetService(typeof(RelaxKonOS.Runtime.ApplicationManager)) as RelaxKonOS.Runtime.ApplicationManager)?.Launch(new AppId("relaxkonos.server-center"));
        var views = new ProcessGuardianViews(Label);
        var window = context.ShowWindow(Label("application.relaxkonos.processguardian.display_name"), views.CreateWorkspace(viewModel), new Rect(80, 60, 1240, 700), Manifest.IconGlyph);
        viewModel.ShowPrivilegedHelperUnavailableAsync = problemCode => PrivilegedHelperUnavailableDialog.ShowAsync(context, window, problemCode);
        viewModel.RequestAdministratorApprovalAsync = () => RequestAdministratorApprovalAsync(context, window, session);
        viewModel.ShowEditorAsync = async isEdit =>
        {
            await context.ShowDialogAsync<bool>(window,
                Label(isEdit ? "guardian.editor.edit_title" : "guardian.editor.create_title"), dialog =>
                {
                    viewModel.CloseEditorAsync = () => { dialog.Close(true); return Task.CompletedTask; };
                    return views.CreateEditor(viewModel, () => dialog.Cancel(),
                        () => SelectExecutableAsync(viewModel), () => SelectWorkingDirectoryAsync(viewModel));
                }, new RelaxKonOS.Core.Primitives.Size(640, 650));
        };
        viewModel.ShowLogsAsync = workload =>
        {
            var logViewModel = new GuardianLogWindowViewModel(client, workload, Label);
            WeakEventHandlerManager.Subscribe<LocalizationService, SystemLanguageChangedEventArgs, GuardianLogWindowViewModel>(
                localization, nameof(LocalizationService.LanguageChanged), logViewModel.OnLanguageChanged);
            var logWindow = context.ShowWindow(logViewModel.Title, views.CreateLogs(logViewModel), new Rect(170, 120, 800, 520), Manifest.IconGlyph);
            logWindow.CloseRequested += (_, _) => _ = logViewModel.DisposeAsync();
            _ = logViewModel.StartAsync();
            return Task.CompletedTask;
        };
        _ = viewModel.StartAsync();
    }

    private static string Label(string key) =>
        ProcessGuardianLocalization.Resolve(App.Services.GetRequiredService<LocalizationService>(), key);

    private static async Task SelectExecutableAsync(ProcessGuardianViewModel vm)
    {
        var topLevel = Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null;
        if (topLevel is null) return;
        var selected = await UsageFilePicker.OpenFilePickerAsync(topLevel.StorageProvider, new FilePickerOpenOptions
        {
            Title = Label("guardian.create.executable.select"),
            AllowMultiple = false,
        }, UsageMemoryStore.CaptureServices(App.Services), "ProcessGuardian.executable");
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) vm.ExecutablePath = path;
    }

    private static async Task SelectWorkingDirectoryAsync(ProcessGuardianViewModel vm)
    {
        var topLevel = Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null;
        if (topLevel is null) return;
        var selected = await UsageFilePicker.OpenFolderPickerAsync(topLevel.StorageProvider, new FolderPickerOpenOptions
        {
            Title = Label("guardian.create.working_directory.select"),
            AllowMultiple = false,
        }, UsageMemoryStore.CaptureServices(App.Services), "ProcessGuardian.working-directory");
        var path = selected.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) vm.WorkingDirectory = path;
    }

    private static Task<RunAsAdministratorApproval?> RequestAdministratorApprovalAsync(AppContext context, RelaxKonOS.WindowManager.ManagedWindow owner, IAuthSession session) =>
        context.ShowDialogAsync<RunAsAdministratorApproval?>(owner, Label("guardian.admin_approval.title"), dialog =>
        {
            var username = new TextBox
            {
                    Text = string.Empty,
                PlaceholderText = Label("guardian.admin_approval.username"),
            };
            var password = new TextBox { PasswordChar = '•', PlaceholderText = Label("guardian.admin_approval.password") };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = Label("common.cancel") };
            cancel.Click += (_, _) => dialog.Cancel();
            var confirm = new Button { Content = Label("common.ok"), Classes = { "primary" } };
            confirm.Click += (_, _) => dialog.Close(new RunAsAdministratorApproval(username.Text?.Trim() ?? string.Empty, password.Text ?? string.Empty));
            actions.Children.Add(cancel);
            actions.Children.Add(confirm);
            return new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = Label("guardian.admin_approval.message"), TextWrapping = TextWrapping.Wrap },
                    username,
                    password,
                    actions,
                },
            };
        }, new RelaxKonOS.Core.Primitives.Size(460, 230));
}
