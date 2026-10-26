using Avalonia.LogicalTree;
using Avalonia.Controls;
using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Squid;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Proxy;

internal static class SquidWorkspaceChecks
{
    public static void Run()
    {
        // The Server answers the gateway contract on every platform, so the manifest must not narrow
        // itself back to Linux: that would hide the only window in which a Windows host can explain
        // itself, and the application must not borrow another application's icon either.
        var manifest = new SquidApp().Manifest;
        Check(manifest.Server.SupportedPlatforms.Count == 0
            && manifest.Server.RequiredCapabilities.SequenceEqual([ServerCapabilities.GatewayProxy]),
            "Squid must reach every host that advertises gateway-proxy regardless of platform.");
        Check(manifest.IconPath == "avares://RelaxKonOS.Client/Assets/AppIcons/squid.png",
            "Squid must render its own icon instead of the proxy manager icon.");

        VerifyHttpContract();
        var fake = new SquidClientStub();
        using var vm = new SquidViewModel(fake);
        Check(!vm.ApplyCommand.CanExecute(null) && !vm.RefreshCommand.CanExecute(null), "Permission decisions must precede requests.");
        vm.CanRead = true;
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(fake.Reads == 1 && vm.BindAddress == "127.0.0.1" && !vm.ApplyCommand.CanExecute(null), "Read-only users can read without enabling writes.");
        vm.CanManage = true;
        vm.Port = "8080"; vm.AllowedDomains = ".example.org\nexample.net"; vm.UpstreamHost = "127.0.0.1";
        Check(vm.BuildConfiguration() is { Port: 8080, AllowedDomains.Length: 2, Upstream.Port: 7890 }, "Editor fields must produce the current structured contract.");
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(vm.Port == "8080" && vm.HasUnsavedChanges, "Refreshing service facts must preserve unsaved edits.");
        vm.ConfirmApplyAsync = () => Task.FromResult(false);
        Complete(vm.ApplyCommand.ExecuteAsync(null));
        Check(fake.Submissions.Count == 0, "Cancelling replacement must never queue a host mutation.");
        vm.ConfirmApplyAsync = () => Task.FromResult(true);
        fake.LoseFirstResponse = true;
        Complete(vm.ApplyCommand.ExecuteAsync(null));
        Check(vm.HasPending && vm.ResumeCommand.CanExecute(null) && !vm.ApplyCommand.CanExecute(null) && !vm.StartCommand.CanExecute(null), "An uncertain request must block duplicate mutations.");
        vm.Port = "9090";
        Complete(vm.ResumeCommand.ExecuteAsync(null));
        Check(fake.Submissions.Count == 2 && fake.Submissions[0].Key == fake.Submissions[1].Key
            && ReferenceEquals(fake.Submissions[0].Body, fake.Submissions[1].Body)
            && ((ApplySquidConfigurationRequest)fake.Submissions[1].Body).Configuration.Port == 8080,
            "Resume must replay the original body and key, even if the local draft changes.");
        Check(!vm.HasPending && fake.OperationReads == 1, "Only a terminal server operation clears the pending state.");
        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { RecoveryRequired = true } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(!vm.StartCommand.CanExecute(null) && !vm.ApplyCommand.CanExecute(null)
            && vm.StopCommand.CanExecute(null) && vm.DisableCommand.CanExecute(null) && vm.ValidateCommand.CanExecute(null), "Recovery permits only safe lifecycle actions and native validation.");
        vm.ConfirmReloadAsync = () => Task.FromResult(true);
        Complete(vm.LoadStoredCommand.ExecuteAsync(null));
        Check(vm.Port == "1270" && !vm.HasUnsavedChanges, "Explicit reload must restore saved structured state.");
        Check(!vm.LoadStoredCommand.CanExecute(null), "Nothing is offered to load while the draft matches the saved configuration.");
        vm.Port = "1271";
        Check(vm.LoadStoredCommand.CanExecute(null), "A dirty draft can be replaced by the saved configuration.");
        vm.Port = "1270";

        // Credentials live outside configuration drafts and are never returned by account reads.
        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { RecoveryRequired = false, Managed = true } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        vm.AccessModeIndex = (int)SquidAccessMode.Password;
        vm.AllowedNetworks = "";
        Check(vm.IsConfigurationValid && !vm.CanApply && vm.ShowNoEnabledAccounts,
            "Password-only drafts do not require networks but cannot publish before an account is enabled.");
        vm.NewAccountName = "desktop-test"; vm.AccountPassword = "test-only-Password!92";
        vm.CanManage = false;
        Complete(vm.CreateAccountCommand.ExecuteAsync(null));
        Check(!vm.CanCreateAccount, "Read-only users cannot submit account mutations.");
        vm.CanManage = true;
        Check(vm.CanCreateAccount && vm.HasAccountDraft, "A valid proxy account can be created and its unsubmitted password participates in the close guard.");
        Complete(vm.CreateAccountCommand.ExecuteAsync(null));
        Check(vm.AccountPassword.Length == 0 && !vm.HasAccountDraft && vm.Accounts.Single().Username == "desktop-test" && vm.CanApply,
            "Account creation clears the password field, refreshes metadata and unlocks password-mode publishing.");
        Check(!SquidConfigurationRenderer.Preview(vm.BuildConfiguration()).Configuration!.Contains("test-only-Password!92"),
            "Configuration previews never contain submitted account passwords.");
        vm.SelectedAccount = vm.Accounts.Single();
        vm.AccountPassword = "test-only-NewPass!93";
        Complete(vm.ResetAccountPasswordCommand.ExecuteAsync(null));
        Check(fake.Submissions.Last().Body is SquidAccountMutation { Action: SquidAccountAction.SetPassword } && vm.AccountPassword.Length == 0,
            "Password reset is a separate typed mutation and clears the input.");
        fake.Snapshot = fake.Snapshot with { State = fake.Snapshot.State with { Configuration = new SquidConfiguration { AccessMode = SquidAccessMode.Password } } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        vm.SelectedAccount = vm.Accounts.Single();
        Check(!vm.CanToggleAccount && !vm.CanRemoveSelectedAccount, "Active authentication protects the last enabled account.");
        fake.Snapshot = fake.Snapshot with { State = fake.Snapshot.State with { Configuration = new SquidConfiguration { Port = 1270 } } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        vm.SelectedAccount = vm.Accounts.Single();
        Complete(vm.ToggleAccountCommand.ExecuteAsync(null));
        Check(!vm.Accounts.Single().Enabled, "An account can be disabled while IP mode is active.");
        Complete(vm.ToggleAccountCommand.ExecuteAsync(null));
        Check(vm.Accounts.Single().Enabled, "A disabled account can be enabled again.");
        vm.ConfirmDeleteAccountAsync = _ => Task.FromResult(false);
        var beforeDelete = fake.Submissions.Count;
        Complete(vm.DeleteAccountCommand.ExecuteAsync(null));
        Check(fake.Submissions.Count == beforeDelete, "Cancelling account deletion preserves the account.");
        vm.ConfirmDeleteAccountAsync = _ => Task.FromResult(true);
        Complete(vm.DeleteAccountCommand.ExecuteAsync(null));
        Check(vm.Accounts.Count == 0, "Confirmed deletion refreshes account metadata.");
        vm.AccessModeIndex = (int)SquidAccessMode.IpWhitelist;
        vm.AllowedNetworks = "127.0.0.1/32";

        // The lifecycle buttons follow the observed service state instead of offering every action.
        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { RecoveryRequired = false, Managed = true, Running = true, Enabled = true } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(!vm.StartCommand.CanExecute(null) && vm.StopCommand.CanExecute(null)
            && vm.RestartCommand.CanExecute(null) && vm.ReloadCommand.CanExecute(null)
            && !vm.EnableCommand.CanExecute(null) && vm.DisableCommand.CanExecute(null),
            "A running, enabled service offers stop, restart, reload and disable only.");
        var bootLabelWhenEnabled = vm.BootActionText.ToString();
        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { Running = false, Enabled = false } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(vm.StartCommand.CanExecute(null) && vm.EnableCommand.CanExecute(null)
            && !vm.StopCommand.CanExecute(null) && !vm.RestartCommand.CanExecute(null) && !vm.ReloadCommand.CanExecute(null),
            "A stopped service offers start and enable only.");
        Check(vm.ToggleBootCommand.CanExecute(null) && vm.BootActionText.ToString() != bootLabelWhenEnabled,
            "The single boot control states the action the next click performs.");

        // Lifecycle actions need a managed file; the host refuses them, so the window must not offer them.
        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { Managed = false } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(vm.ShowAdoptionRequired && vm.NeedsAdoption && !vm.StartCommand.CanExecute(null) && !vm.ApplyCommand.CanExecute(null),
            "An unmanaged squid.conf blocks start and apply until ownership is selected.");
        vm.AdoptExistingConfiguration = true;
        Check(vm.ApplyCommand.CanExecute(null) && !vm.NeedsAdoption, "Selecting ownership unlocks apply only.");
        vm.AdoptExistingConfiguration = false;

        // A host without Squid still opens the window; it must explain the host instead of offering
        // a configuration or a lifecycle action that the Helper would reject anyway.
        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { Supported = false, Installed = false, Managed = false, Running = false, Enabled = false } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(vm.ShowUnsupported && !vm.CanEdit && !vm.ApplyCommand.CanExecute(null) && !vm.StartCommand.CanExecute(null)
            && !vm.ValidateCommand.CanExecute(null) && !vm.ToggleBootCommand.CanExecute(null),
            "An unsupported host shows the platform notice, offers no editable draft and disables every host action.");
        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { Supported = true } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));

        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { PackageManagementSupported = false } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(!vm.CanInstall && !vm.CanUninstall && vm.ShowPackageUnsupported,
            "A Linux host without supported package management explains why both package actions are disabled.");
        var packageRequests = fake.Submissions.Count;
        var navigations = 0;
        vm.OpenComponentCenter = () => navigations++;
        vm.ManageComponentCommand.Execute(null);
        Check(navigations == 1 && fake.Submissions.Count == packageRequests,
            "Squid installation navigates to Component Center without a package mutation.");
        Check(typeof(SquidViewModel).GetProperty("InstallCommand") is null,
            "Squid no longer exposes its former application installer.");
        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { PackageManagementSupported = true } };
        var beforePackage = fake.Submissions.Count;
        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { Installed = true, Managed = false } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(vm.CanUninstall && !vm.CanInstall, "Unmanaged installed packages can be uninstalled.");
        vm.ConfirmPackageAsync = _ => Task.FromResult(false);
        beforePackage = fake.Submissions.Count;
        Complete(vm.UninstallCommand.ExecuteAsync(null));
        Check(fake.Submissions.Count == beforePackage, "Cancelling uninstall preserves the package.");
        vm.ConfirmPackageAsync = _ => Task.FromResult(true);
        Complete(vm.UninstallCommand.ExecuteAsync(null));
        Check(fake.Submissions.Last().Body is SquidLifecycleRequest { Action: SquidServiceAction.Uninstall },
            "Uninstall uses the typed lifecycle contract.");

        // Draft problems are reported in the card that owns the field, and they gate every publish action.
        vm.Port = "999";
        Check(!vm.IsConfigurationValid && vm.ListenerProblems.Count == 1
            && !vm.ApplyCommand.CanExecute(null) && !vm.PreviewCommand.CanExecute(null) && !vm.ValidateCommand.CanExecute(null),
            "An invalid port is reported in place and disables preview, validate and apply.");
        vm.Port = "1270"; vm.AllowedNetworks = "";
        Check(vm.SourceProblems.Count == 1 && !vm.IsConfigurationValid, "An empty source list is reported on the listener card.");
        vm.AllowedNetworks = "127.0.0.1/32";
        Check(vm.IsConfigurationValid && vm.SourceProblems.Count == 0 && vm.ListenerProblems.Count == 0,
            "Correcting the draft clears the reported problems.");

        // A rendered file must never look current after the draft changed underneath it.
        Complete(vm.PreviewCommand.ExecuteAsync(null));
        Check(vm.HasPreview && !vm.IsPreviewStale, "Preview renders the current draft.");
        vm.AllowedDomains = "example.net";
        Check(vm.IsPreviewStale, "Editing after a preview marks the rendered file stale.");

        vm.CanManage = false;
        Check(vm.ShowReadOnly && !vm.CanEdit && !vm.PreviewCommand.CanExecute(null),
            "Observers get the read-only notice and no editable draft.");
        vm.CanManage = true;

        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { Managed = true, Running = true, Enabled = true } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Complete(vm.PreviewCommand.ExecuteAsync(null));

        var view = SquidApp.CreateView(vm);
        var window = new Window { Width = 960, Height = 760, Content = view };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var previewDirectory = Path.Combine(AppContext.BaseDirectory, "preview-qa"); Directory.CreateDirectory(previewDirectory);
        using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(960, 760)))
        { image.Render(view); image.Save(Path.Combine(previewDirectory, "squid.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
        view.FindControl<Button>("PageButton5")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(view.FindControl<StackPanel>("Page5")!.IsVisible && !view.FindControl<StackPanel>("AccessEditor")!.IsVisible && view.FindControl<TextBox>("AccountPasswordBox")!.PasswordChar != default,
            "Access control lists accounts while keeping masked editing fields inside a closed modal.");
        using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(960, 760)))
        { image.Render(view); image.Save(Path.Combine(previewDirectory, "squid-access.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
        foreach (var workspace in new UserControl[] { (UserControl)view, new RelaxKonOS.Client.Apps.ProxyServer.Views.ProxyServerWorkspace() })
        {
            foreach (var field in workspace.GetLogicalDescendants().OfType<TextBox>().Where(field => !field.IsReadOnly))
                Check(field.GetLogicalAncestors().OfType<StackPanel>().Any(panel => panel.Name?.EndsWith("Editor", StringComparison.Ordinal) == true && !panel.IsVisible), "Editable text fields belong to an unopened editor, not the main workspace.");
        }
        var editorOwner = view.FindControl<StackPanel>("AccessEditor")!.Parent;
        Complete(RelaxKonOS.Client.Controls.WorkspaceEditorDialogView.OpenAsync((UserControl)view, "AccessEditor", "Access", (title, editor) =>
        {
            Check(editor.IsVisible && editor.Parent is null && ReferenceEquals(editor.DataContext, vm), "Opening detaches the same draft and keeps its binding owner.");
            var wrapper = new RelaxKonOS.Client.Controls.WorkspaceEditorDialogView { DataContext = vm };
            wrapper.FindControl<ContentControl>("EditorHost")!.Content = editor;
            var dialogWindow = new Window { Content = wrapper, Width = 420, Height = 640 };
            dialogWindow.Show(); Dispatcher.UIThread.RunJobs();
            Check(view.FindControl<ComboBox>("AccessModeBox")!.Bounds.Height > 0, "The modal measures its editor at compact width.");
            using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(420, 640)))
            { image.Render(dialogWindow); image.Save(Path.Combine(previewDirectory, "squid-access-editor.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
            wrapper.FindControl<ContentControl>("EditorHost")!.Content = null; dialogWindow.Close();
            return Task.CompletedTask;
        }));
        Check(!view.FindControl<StackPanel>("AccessEditor")!.IsVisible && ReferenceEquals(view.FindControl<StackPanel>("AccessEditor")!.Parent, editorOwner), "Closing restores the hidden editor for a later edit without duplicating controls.");
        view.FindControl<Button>("PageButton0")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var boxes = new[] { "BindAddressBox", "PortBox", "NetworksBox", "AllowedDomainsBox", "DeniedDomainsBox", "UpstreamHostBox", "UpstreamPortBox", "PreviewBox", "DetailBox" }.Select(name => view.FindControl<TextBox>(name)!).ToList();
        Check(boxes.Count == 9, "The workspace renders seven editor fields plus the preview and detail panes.");
        Check(boxes.Count(box => box.IsReadOnly) == 2, "Only the rendered file and its details are read-only.");
        var overview = view.FindControl<StackPanel>("Page0")!;
        Check(overview.IsVisible && !view.FindControl<StackPanel>("Page4")!.IsVisible, "The workspace starts on the runtime overview.");
        view.FindControl<Button>("PageButton2")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Check(!overview.IsVisible && view.FindControl<StackPanel>("Page2")!.IsVisible, "Navigation shows the selected page while retaining draft controls.");
        window.Height = 1080; Dispatcher.UIThread.RunJobs();
        using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(960, 1080)))
        { image.Render(view); image.Save(Path.Combine(previewDirectory, "squid-tall.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
        window.Height = 760; Dispatcher.UIThread.RunJobs();
        window.Width = 420; Dispatcher.UIThread.RunJobs();
        using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(420, 760)))
        { image.Render(view); image.Save(Path.Combine(previewDirectory, "squid-compact.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
        Check(view.Bounds.Width > 0, "The scrollable editor must remain renderable at compact width.");
        Check(view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CompactWorkspaceMenu").IsVisible,
            "A narrow workspace provides compact page navigation.");
        vm.AllowedDomains = "example.org";
        Complete(vm.PreviewCommand.ExecuteAsync(null));
        Dispatcher.UIThread.RunJobs();
        Check(vm.HasPreview && !vm.IsPreviewStale, "Previewing from the pinned action bar renders the edited draft and clears the stale mark.");
        Check(view.FindControl<StackPanel>("Page4")!.IsVisible,
            "A preview requested from the pinned bar opens the configuration publishing page.");
        using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(420, 760)))
        { image.Render(view); image.Save(Path.Combine(previewDirectory, "squid-preview-scroll.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
        window.Close();
        vm.InvalidateSession();
        Check(!vm.CanRead && !vm.CanManage && vm.AllowedDomains == "" && vm.Snapshot is null, "A changed login must clear old host facts and disable writes.");

        // Blocking host states get their own band so the reason an action is missing is visible.
        fake.Snapshot = fake.Snapshot with { Status = new SquidStatus(true, true, true, true, false, "hash", true, true) };
        using (var noticeVm = new SquidViewModel(fake) { CanRead = true, CanManage = false })
        {
            Complete(noticeVm.RefreshCommand.ExecuteAsync(null));
            Check(noticeVm.ShowRecoveryRequired && noticeVm.ShowReadOnly && noticeVm.ShowAdoptionRequired && !noticeVm.CanEdit,
                "Recovery, ownership and read-only states render as notices and disable the draft.");
            var noticeView = SquidApp.CreateView(noticeVm);
            var noticeWindow = new Window { Width = 960, Height = 520, Content = noticeView };
            noticeWindow.Show(); Dispatcher.UIThread.RunJobs();
            using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(960, 520)))
            { image.Render(noticeView); image.Save(Path.Combine(previewDirectory, "squid-notices.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
            noticeWindow.Close();
        }
        Console.WriteLine("Squid workspace permissions, draft preservation, replacement confirmation, idempotent recovery, lifecycle and layout checks passed.");
    }
    private static void VerifyHttpContract()
    {
        var session = DispatchProxy.Create<IAuthSession, SquidSessionStub>();
        var state = (SquidSessionStub)(object)session;
        using var handler = new SquidHttpStub();
        using var http = new HttpClient(handler);
        var client = new RemoteSquidClient(http, session);
        var preview = client.PreviewAsync(new SquidConfiguration(), default); Complete(preview);
        Check(!preview.Result.Valid && preview.Result.Errors.Single() == "invalid", "HTTP 200 invalid previews must deserialize without losing structured errors.");
        var validation = client.ValidateAsync(new SquidConfiguration(), default); Complete(validation);
        Check(!validation.Result.Valid && validation.Result.Diagnostics == "parse error", "Native validation diagnostics must survive the HTTP client boundary.");
        Complete(client.SubmitAsync(GatewayProxyRoutes.Lifecycle, new SquidLifecycleRequest(SquidServiceAction.Reload), "retained-key", default));
        Check(handler.LastKey == "retained-key" && handler.LastBearer == "access" && handler.LastPath == GatewayProxyRoutes.Lifecycle,
            "The client must send the login bearer, closed route and retained idempotency key.");
        using var body = JsonDocument.Parse(handler.LastBody!);
        Check(body.RootElement.GetProperty("action").GetString() == "reload", "Lifecycle must use the shared Squid enum contract.");
        var before = handler.Calls;
        state.Login = state.Login with { Id = Guid.NewGuid() };
        try { Complete(client.SubmitAsync(GatewayProxyRoutes.Lifecycle, new SquidLifecycleRequest(SquidServiceAction.Stop), "new-key", default)); throw new Exception("Stale login was allowed."); }
        catch (InvalidOperationException) { }
        Check(handler.Calls == before, "A stale application window must never mutate the newly selected host.");
    }
    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Squid workspace check timed out.");
        task.GetAwaiter().GetResult();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class SquidClientStub : ISquidClient
    {
        public SquidConfigurationSnapshot Snapshot = new(new(new SquidConfiguration { Port = 1270 }, 1, 1, "hash", null), new(true, true, true, true, true, "hash", true), true);
        public int Reads; public int OperationReads; public bool LoseFirstResponse; public bool OperationFailed;
        public List<(string Key, object Body)> Submissions = [];
        private readonly Guid _id = Guid.NewGuid();
        private SquidAccountMutation? _pendingAccount;
        public List<SquidAccount> Accounts = [];
        public Task<SquidAccount[]> ReadAccountsAsync(CancellationToken ct) => Task.FromResult(Accounts.ToArray());
        public Task<SquidConfigurationSnapshot> ReadAsync(CancellationToken ct) { Reads++; return Task.FromResult(Snapshot); }
        public Task<SquidConfigurationPreview> PreviewAsync(SquidConfiguration configuration, CancellationToken ct) => Task.FromResult(SquidConfigurationRenderer.Preview(configuration));
        public Task<SquidNativeValidation> ValidateAsync(SquidConfiguration configuration, CancellationToken ct) => Task.FromResult(new SquidNativeValidation(true, null, "parsed"));
        public Task<ProxyOperationAcceptedDto> SubmitAsync(string route, object body, string key, CancellationToken ct)
        {
            Submissions.Add((key, body));
            if (body is SquidAccountMutation mutation) _pendingAccount = mutation;
            if (LoseFirstResponse) { LoseFirstResponse = false; throw new HttpRequestException("response lost"); }
            return Task.FromResult(new ProxyOperationAcceptedDto(_id));
        }
        public Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken ct)
        {
            if (!OperationFailed && _pendingAccount is { } mutation)
            {
                var existing = Accounts.FindIndex(x => x.Username == mutation.Username);
                if (mutation.Action == SquidAccountAction.Create) Accounts.Add(new(mutation.Username, true));
                else if (mutation.Action == SquidAccountAction.Delete && existing >= 0) Accounts.RemoveAt(existing);
                else if (mutation.Action == SquidAccountAction.SetEnabled && existing >= 0) Accounts[existing] = Accounts[existing] with { Enabled = mutation.Enabled!.Value };
                _pendingAccount = null;
            }
            OperationReads++; return Task.FromResult(new ProxyOperationDto(id, "squid.configuration.apply", OperationFailed ? ProxyOperationState.Failed : ProxyOperationState.Succeeded, "completed", OperationFailed ? "squid.package.install_failed" : "", null, DateTimeOffset.UtcNow));
        }
    }
}

public class SquidSessionStub : DispatchProxy
{
    public SessionDto Login = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, default, Guid.NewGuid(), "test", DateTimeOffset.UtcNow);
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_CurrentSession" => Login,
        "get_State" => AuthSessionState.Authenticated,
        "get_EffectiveBaseUrl" => "https://squid.example/",
        "GetAccessTokenAsync" => Task.FromResult<string?>("access"),
        _ => throw new InvalidOperationException(method.Name)
    };
}
internal sealed class SquidHttpStub : HttpMessageHandler
{
    public int Calls;
    public string? LastKey, LastBearer, LastPath, LastBody;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; LastKey = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
        LastBearer = request.Headers.Authorization?.Parameter; LastPath = request.RequestUri!.AbsolutePath;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        object value = LastPath switch
        {
            GatewayProxyRoutes.Preview => new SquidConfigurationPreview(false, null, ["invalid"]),
            GatewayProxyRoutes.Validate => new SquidNativeValidation(false, "squid.configuration.parse_failed", "parse error"),
            GatewayProxyRoutes.Lifecycle => new ProxyOperationAcceptedDto(Guid.NewGuid()),
            _ => throw new InvalidOperationException(LastPath)
        };
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: RelaxKonOSJsonOptions.Default) };
    }
}
