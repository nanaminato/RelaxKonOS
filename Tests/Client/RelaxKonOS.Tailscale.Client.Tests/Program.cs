using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Apps.Tailscale;
using RelaxKonOS.Client.Apps.Tailscale.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;

AppBuilder.Configure<TestApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
using var appearance = new AppearanceService(Application.Current!, new SystemStyleRegistry());
var settings = new ShellSettings(appearance) { Language = "en-US" };
var language = new LocalizationService(settings, new SshDesktopSession(null!, new ShellSettings(null!)));
using var services = new ServiceCollection().AddSingleton(language).BuildServiceProvider();
typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, services);
SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
var work = RunChecksAsync();
var deadline = DateTime.UtcNow.AddSeconds(90);
while (!work.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
if (!work.IsCompleted) throw new TimeoutException("Tailscale desktop checks timed out");
work.GetAwaiter().GetResult();

async Task RunChecksAsync()
{
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var folder = Directory.CreateTempSubdirectory("relaxkonos-tailscale-client-");
try
{
    Check(new TailscaleApp().Manifest.Permissions.Contains(RelaxKonOS.Core.Applications.AppPermissions.ServerComponentsRead), "launch dependency inspection permission is declared");
    var client = new FakeClient();
    var broker = new Broker();
    var store = new TailscaleOperationReferences(Path.Combine(folder.FullName, "refs.json"));
    var model = new TailscaleViewModel(client, store, broker, language);
    await model.ActivateAsync();
    Check(model.Peers.Count == 1 && !model.CanConnect && client.ControlReads == 0 && broker.Calls == 0, "launch only observes status");
    await model.LoadControlAsync();
    Check(model.CanConnect && model.CanEditPreferences && !model.CanDisconnect, "controls depend on fresh baseline");
    Check(broker.Capability == HostElevationCapability.HostNetworkChange && broker.Target == TailscaleAccess.Resource, "precise elevation resource");
    var original = client.Connection;
    model.ConfirmAsync = (_, _, _) => Task.FromResult(false);
    await model.ConnectAsync();
    Check(client.Prepares == 1 && client.Executions == 0, "cancel confirmation never executes");
    Check(store.Read(original).Count == 1, "prepared reference is discoverable");
    model.ConfirmAsync = null;
    await model.ConnectAsync();
    Check(client.Executions == 0, "absent confirmation never executes");
    model.ConfirmAsync = (plan, baseline, target) =>
    {
        Check(plan.State == TailscaleOperationState.Prepared && baseline.Revision == client.Control.Revision && target == original.ServiceId, "confirmation receives exact host and baseline");
        Check(store.Read(original).Contains(plan.OperationId), "reference published before confirmation and execution");
        return Task.FromResult(true);
    };
    await model.ConnectAsync();
    Check(client.Executions == 1 && !model.CanConnect && model.OperationText == language.Get("tailscale.operation_state.Applied", ""), "verified receipt without optimistic control baseline");
    Check(model.CanDisconnect && model.HasControl, "applied change re-reads the live baseline");
    await model.LoadControlAsync();
    Check(model.CanDisconnect && !model.CanConnect, "fresh connected baseline");
    // Preference edits use the same structured plan, confirmation and verified receipt.
    var preparations = client.Prepares;
    model.ConfirmAsync = (_, _, _) => Task.FromResult(false);
    var cancelled = await model.SetAcceptDnsAsync(false);
    Check(!cancelled && client.Executions == 1 && model.Preferences?.AcceptDns == true, "cancelled preference change executes nothing and keeps the host value");
    model.ConfirmAsync = (_, _, _) => Task.FromResult(true);
    var applied = await model.SetAcceptDnsAsync(false);
    Check(applied && client.Executions == 2 && client.LastRequest?.Action == TailscaleAction.ConfigurePreferences
        && client.LastRequest.Preferences?.AcceptDns == false && client.LastRequest.Preferences?.AcceptRoutes == false,
        "structured preference change only");
    Check(model.Preferences?.AcceptDns == false && model.HasControl, "applied preference re-reads the host");
    Check(client.Prepares == preparations + 2, "each preference attempt is planned once");
    Check(model.AccountLogin == "owner@example.com" && model.AccountName == "Owner Name" && model.TailnetDisplay == "example.ts.net",
        "account and tailnet are displayed");
    Check(model.SelfLine.Contains("host", StringComparison.Ordinal), "this-device line carries the host and address");
    model.SelectedPeer = model.Peers[0];
    Check(model.SelectedPeerVisible && model.Peers[0].OsLabel == "Linux" && model.Peers[0].Owner == "owner@example.com",
        "device details carry OS and owner");
    // Exit-node selection uses the closed preference plan and only offers daemon-approved candidates.
    client.WithExitNodePeer = true;
    await model.RefreshAsync(); await model.LoadControlAsync();
    Check(model.ExitNodeCandidates.Count == 1 && model.ExitNodeCandidates[0].Ip == "100.64.0.9", "exit-node candidates come from approved peers");
    model.EditPreferencesDialogAsync = (current, candidates, platform) =>
    {
        Check(current == model.Preferences && platform == "windows" && candidates.Count == 1, "preference editor receives the live snapshot and candidates");
        return Task.FromResult<TailscalePreferences?>(current with
        {
            ShieldsUp = true, ExitNode = candidates[0].Ip, ExitNodeAllowLanAccess = true, AutoUpdate = true,
        });
    };
    await model.EditPreferencesAsync();
    Check(client.LastRequest?.Action == TailscaleAction.ConfigurePreferences
        && client.LastRequest.Preferences?.ShieldsUp == true
        && client.LastRequest.Preferences?.ExitNode == "100.64.0.9"
        && client.LastRequest.Preferences?.ExitNodeAllowLanAccess == true
        && client.Executions == 3, "exit node and extended preferences use one structured plan");
    Check(model.Preferences?.ShieldsUp == true && model.ExitNodePreferenceText == "gateway", "applied exit node is shown by name");
    await model.LoadControlAsync();
    client.LoseResponse = true;
    await model.DisconnectAsync();
    Check(model.OperationText == language.Get("tailscale.awaiting_receipt", "") && !model.CanDisconnect, "lost reply stays unproven");
    var executed = client.Executions;
    var pendingReference = model.SelectedReference;
    model.SelectedReference = model.Operations.Last();
    await model.ReopenAsync(); await model.LoadControlAsync();
    Check(!model.CanConnect && !model.CanDisconnect, "unrelated receipt cannot clear an unresolved submission");
    model.SelectedReference = pendingReference;
    client.PreparedRead = true; await model.ReopenAsync(); await model.LoadControlAsync();
    Check(!model.CanConnect && !model.CanDisconnect,"prepared observation cannot settle a lost submission");
    client.PreparedRead = false;
    await model.ReopenAsync();
    Check(client.Executions == executed && model.OperationText == language.Get("tailscale.operation_state.Applied", ""), "reopen reads original evidence only");
    model.Deactivate();
    var reopened = new TailscaleViewModel(client, new TailscaleOperationReferences(Path.Combine(folder.FullName, "refs.json")), broker, language);
    await reopened.ActivateAsync();
    Check(reopened.Operations.Count > 0 && reopened.OperationText == language.Get("tailscale.operation_empty", ""), "new window loads references without cached outcome");
    await reopened.ReopenAsync();
    Check(reopened.OperationText == language.Get("tailscale.operation_state.Applied", ""), "new window reads original receipt");
    Check(store.Read(original with { UserId = Guid.NewGuid() }).Count == 0 && store.Read(original with { ServiceId = "another-host" }).Count == 0, "references isolate host and user");
    Check(store.Read(original with { SessionId = Guid.NewGuid() }).Count > 0, "same user can reopen after a new login");
    await reopened.LoadControlAsync();
    reopened.ConfirmAsync = (_, _, _) => { client.SwitchHost(); return Task.FromResult(true); };
    await reopened.ConnectAsync(); Dispatcher.UIThread.RunJobs();
    Check(client.Executions == executed, "host switch during confirmation prevents write");
    await reopened.RefreshAsync(); await reopened.LoadControlAsync();
    reopened.ConfirmAsync = (_, _, _) => Task.FromResult(true);
    client.BadPlan = true;
    await reopened.ConnectAsync();
    Check(client.Executions == executed, "mismatched prepared operation rejected");
    client.BadPlan = false;
    client.HoldPlan = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = reopened.ConnectAsync();
    Check(reopened.IsBusy, "pending preparation marks busy");
    client.SwitchHost(); Dispatcher.UIThread.RunJobs();
    client.HoldPlan.SetResult(client.LastPlan!); await pending; client.HoldPlan = null;
    Check(client.Executions == executed && !reopened.IsBusy && reopened.Target == client.Connection.ServiceId
        && reopened.Operations.Count == 0, "late response cannot update new host");
    reopened.Deactivate();
    // A persistence failure must stop the mutation before it is submitted.
    var blocked = new TailscaleViewModel(client, new BrokenReferences(), broker, language) { ConfirmAsync = (_, _, _) => Task.FromResult(true) };
    await blocked.ActivateAsync(); await blocked.LoadControlAsync(); await blocked.ConnectAsync();
    Check(client.Executions == executed && blocked.StatusText == language.Get("tailscale.operation_reference_failed", ""), "unpersisted reference blocks execute");
    blocked.Deactivate();
    // A cancelled old window cannot publish a later reply into a reattached view model.
    await model.ActivateAsync(); await model.LoadControlAsync();
    model.ConfirmAsync = (_, _, _) => Task.FromResult(true);
    client.HoldExecute = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var waiting = model.ConnectAsync();
    Check(model.IsBusy, "pending execute busy");
    model.Deactivate(); client.HoldExecute.SetResult(client.LastResult!); await waiting; client.HoldExecute = null;
    await model.ActivateAsync();
    Check(model.OperationText == language.Get("tailscale.operation_empty", "") && !model.IsBusy, "closed window response discarded");
    model.Deactivate();
    var recoveryClient = new FakeClient { UnknownResult = true };
    var recoveryModel = new TailscaleViewModel(recoveryClient, new TailscaleOperationReferences(Path.Combine(folder.FullName,"recovery.json")),broker,language)
        { ConfirmAsync = (_,_,_) => Task.FromResult(true) };
    await recoveryModel.ActivateAsync(); await recoveryModel.LoadControlAsync(); await recoveryModel.ConnectAsync();
    Check(recoveryModel.CanInspectRecovery && !recoveryModel.CanCloseRecovery,"unknown receipt enables inspection only");
    recoveryClient.RecoveryReady=false; await recoveryModel.InspectRecoveryAsync();
    Check(!recoveryModel.CanCloseRecovery,"active CLI cannot be closed");
    recoveryClient.RecoveryReady=true; await recoveryModel.InspectRecoveryAsync();
    Check(recoveryModel.CanCloseRecovery,"stable observation enables explicit acknowledgement");
    await recoveryModel.CloseRecoveryAsync(); Check(recoveryClient.CloseCalls==0,"missing closure confirmation does nothing");
    recoveryModel.ConfirmCloseAsync=(_,_)=>Task.FromResult(false); await recoveryModel.CloseRecoveryAsync();
    Check(recoveryClient.CloseCalls==0,"cancelled closure confirmation does nothing");
    recoveryModel.ConfirmCloseAsync=(preview,target)=>
    {
        Check(preview.OperationId==recoveryModel.SelectedReference!.Id && target==recoveryClient.Connection.ServiceId,"closure confirmation binds original operation and host");
        return Task.FromResult(true);
    };
    await recoveryModel.CloseRecoveryAsync();
    Check(recoveryClient.CloseCalls==1 && recoveryClient.Executions==1 && recoveryModel.OperationText==language.Get("tailscale.recovery.closed",""),"closure preserves unknown outcome and never replays native action");
    await recoveryModel.LoadControlAsync(); await recoveryModel.DisconnectAsync(); await recoveryModel.InspectRecoveryAsync();
    recoveryClient.LoseCloseResponse=true; await recoveryModel.CloseRecoveryAsync();
    Check(recoveryModel.OperationText==language.Get("tailscale.recovery.awaiting_receipt",""),"lost closure response remains unproven");
    recoveryClient.SuppressClosure=true; await recoveryModel.ReopenAsync();
    Check(recoveryModel.OperationText==language.Get("tailscale.recovery.awaiting_receipt",""),"missing original closure cannot settle pending acknowledgement");
    recoveryClient.SuppressClosure=false;
    recoveryClient.BadClosure=true; await recoveryModel.ReopenAsync();
    Check(recoveryModel.OperationText==language.Get("tailscale.recovery.awaiting_receipt",""),"foreign actor closure cannot clear pending acknowledgement");
    recoveryClient.BadClosure=false; recoveryClient.BadClosureTimestamp=true; await recoveryModel.ReopenAsync();
    Check(recoveryModel.OperationText==language.Get("tailscale.recovery.awaiting_receipt",""),"incomplete closure cannot clear pending acknowledgement");
    recoveryClient.BadClosureTimestamp=false; await recoveryModel.InspectRecoveryAsync();
    Check(recoveryClient.CloseCalls==2 && recoveryModel.OperationText==language.Get("tailscale.recovery.closed",""),"inspection reconciles a recorded closure without resubmitting it");
    await recoveryModel.ReopenAsync();
    Check(recoveryClient.CloseCalls==2 && recoveryModel.OperationText==language.Get("tailscale.recovery.closed",""),"reopening reconciles closure without repeating it");
    recoveryModel.Deactivate();
    // Finite, de-duplicated references contain no operation results.
    for (var i = 0; i < 35; i++) store.Remember(original, Guid.NewGuid());
    Check(store.Read(original).Count == 30, "reference retention bounded");
    var last = store.Read(original)[0]; store.Remember(original, last);
    Check(store.Read(original).Count == 30 && store.Read(original)[0] == last, "reference duplicate does not grow history");
    Check(!File.ReadAllText(Path.Combine(folder.FullName, "refs.json")).Contains("Applied"), "outcomes never stored locally");

    // Logout is an explicit, journaled action; the host becomes signed out and must log in again.
    var logoutClient = new FakeClient();
    var logoutBroker = new Broker();
    var logoutModel = new TailscaleViewModel(logoutClient, new TailscaleOperationReferences(Path.Combine(folder.FullName, "logout.json")),
        logoutBroker, language) { ConfirmAsync = (_, _, _) => Task.FromResult(true) };
    await logoutModel.ActivateAsync(); await logoutModel.LoadControlAsync();
    Check(logoutModel.CanLogout && logoutModel.ConnectionStateText == language.Get("tailscale.disconnected", ""), "signed-in host offers logout");
    await logoutModel.LogoutAsync();
    Check(logoutClient.LoggedOut && !logoutModel.CanLogout && !logoutModel.CanConnect
        && logoutModel.ConnectionStateText == language.Get("tailscale.backend.NeedsLogin", "")
        && logoutModel.ConnectionHint == language.Get("tailscale.login_required", ""), "logout leaves the host signed out and points to login");
    logoutModel.Deactivate();

    // The connection page reads itself, keeps the last snapshot when a background refresh fails,
    // and never asks for a mutation grant just to observe.
    var probeClient = new FakeClient();
    var probeBroker = new Broker();
    var probe = new TailscaleViewModel(probeClient, new TailscaleOperationReferences(Path.Combine(folder.FullName, "probe.json")), probeBroker, language);
    await probe.ActivateAsync();
    probe.SetVisiblePage("control");
    await probe.EnsureControlAsync();
    Check(probe.HasControl && probeClient.ControlReads == 1 && probeBroker.Calls == 0 && probeBroker.GrantCalls == 0, "connection page loads itself without elevation");
    probeClient.StatusError = true;
    await probe.AutoRefreshOnceAsync();
    Check(probe.HasControl && probe.Peers.Count == 1 && probe.StatusText == language.Get("tailscale.auto_refresh_failed", ""), "failed background refresh keeps the last snapshot");
    probeClient.StatusError = false;
    await probe.AutoRefreshOnceAsync();
    Check(probe.StatusText == language.Get("tailscale.state.observed", ""), "recovered background refresh clears the failure");
    probe.Deactivate();
    var unreachable = new TailscaleViewModel(new FakeClient { ControlError = true },
        new TailscaleOperationReferences(Path.Combine(folder.FullName, "unreachable.json")), new Broker(), language);
    await unreachable.ActivateAsync();
    await unreachable.EnsureControlAsync();
    Check(!unreachable.HasControl && unreachable.ControlUnavailable && unreachable.HasControlProblem, "unreachable control read is reported instead of prompting");
    Check(unreachable.ConnectionHint == language.Get("tailscale.control_unavailable", ""), "unreachable control read explains itself");
    unreachable.Deactivate();

    // Device identities stay grant-gated, but the page grants itself only after an explicit action.
    var peersClient = new FakeClient { Restricted = true };
    var peersBroker = new Broker();
    var peers = new TailscaleViewModel(peersClient, new TailscaleOperationReferences(Path.Combine(folder.FullName, "peers.json")), peersBroker, language);
    await peers.ActivateAsync();
    Check(peers.PeersRestricted && peers.CanAuthorizePeers, "restricted device list offers explicit authorization");
    peersClient.Restricted = false;
    await peers.AuthorizePeersAsync();
    Check(peersBroker.GrantCalls == 1 && peersBroker.Capability == HostElevationCapability.HostNetworkChange
        && peersBroker.Target == TailscaleAccess.Resource && !peers.PeersRestricted
        && peers.StatusText == language.Get("tailscale.authorization_granted", ""), "authorization refreshes the device list");
    peers.PeerFilter = "no-such-device";
    Check(peers.FilteredPeers.Count == 0 && peers.NoMatchingPeers, "device filter hides non-matches");
    peers.PeerFilter = "host";
    Check(peers.FilteredPeers.Count == 1, "device filter matches the host name");
    peers.Deactivate();

    checks += await TransportChecks.RunAsync();
    checks += await LoginChecks.RunAsync(language, folder.FullName);
    checks += await LoginTransportChecks.RunAsync();

    // Render the actual workspace pages and fixed-footer dialogs in every shipped language.
    var output = Path.Combine(System.AppContext.BaseDirectory, "layout-qa"); Directory.CreateDirectory(output);
    foreach (var culture in new[] { "en-US", "zh-CN", "ja-JP" })
    {
        settings.Language = culture;
        Check(language.Get("application.relaxkonos.tailscale.display_name", "") == (culture switch { "zh-CN" => "Tailscale 管理器", "ja-JP" => "Tailscale マネージャー", _ => "Tailscale Manager" }), "localized launch guard application name");
        settings.Appearance = settings.Appearance with { Mode = RelaxKonOS.Protocol.Desktop.ThemeKind.Light }; Dispatcher.UIThread.RunJobs();
        var layoutClient = new FakeClient();
        var layout = new TailscaleViewModel(layoutClient, new TailscaleOperationReferences(Path.Combine(folder.FullName, "layout.json")), broker, language);
        var loginLayout = new TailscaleLoginViewModel(new LoginFake { Connection = layoutClient.Connection }, new TailscaleOperationReferences(Path.Combine(folder.FullName, "login-layout.json")), broker, language)
            { AccountLineProvider = () => layout.AccountLine };
        var workspace = new TailscaleWorkspace(layout, loginLayout);
        var window = new Window { Content = workspace, Width = 980, Height = 640 }; window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        await layout.LoadControlAsync();
        loginLayout.ConfirmAsync = (_, _, _) => Task.FromResult(true);
        await loginLayout.StartAsync();
        var oldText = layout.StatusText; var oldPeer = layout.Peers[0].Status; var reads = layoutClient.ControlReads;
        layout.SelectedPeer = layout.Peers[0];
        settings.Language = culture == "en-US" ? "zh-CN" : "en-US"; Dispatcher.UIThread.RunJobs();
        Check(layout.StatusText != oldText && layout.Peers[0].Status != oldPeer && layoutClient.ControlReads == reads, "live language switch does not reload host");
        settings.Language = culture; Dispatcher.UIThread.RunJobs();
        foreach (var width in new[] { 980, 540 })
        foreach (var page in new[] { "Devices", "Control", "Operations", "Login" })
        {
            window.Width = width;
            var button = workspace.FindControl<Button>(page)!;
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var content = workspace.FindControl<ContentControl>("Page")!;
            Check(content.Bounds.Width > 200 && content.Bounds.Height > 200, "elastic page " + culture + width + page);
            Check(!workspace.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("[missing:") == true), "localized workspace " + culture + page);
            if (page == "Devices")
            {
                var grid = workspace.GetVisualDescendants().OfType<DataGrid>().Single();
                Check(grid.CanUserResizeColumns, "resizable device columns");
                grid.SelectedItem = layout.Peers[0];
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Check(layout.SelectedPeerVisible, "selected device details are visible");
            }
            using var image = new RenderTargetBitmap(new PixelSize(width, 640)); image.Render(window);
            image.Save(Path.Combine(output, culture + "-" + width + "-" + page + ".png"), new PngBitmapEncoderOptions());
        }
        settings.Appearance = settings.Appearance with { Mode = RelaxKonOS.Protocol.Desktop.ThemeKind.Dark }; Dispatcher.UIThread.RunJobs();
        workspace.FindControl<Button>("Control")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Check(window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark, "workspace follows dark theme");
        using (var dark = new RenderTargetBitmap(new PixelSize(540,640))) { dark.Render(window); dark.Save(Path.Combine(output, culture + "-dark-Control.png"), new PngBitmapEncoderOptions()); }
        window.Close(); Dispatcher.UIThread.RunJobs();
        var plan = new TailscaleOperation(Guid.NewGuid(), TailscaleAction.Configure, TailscaleOperationState.Prepared, new('A',64),
            DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow, new(false,true));
        var logoutPlan = new TailscaleOperation(Guid.NewGuid(), TailscaleAction.Logout, TailscaleOperationState.Prepared, new('A',64),
            DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);
        var dialogIndex = 0;
        foreach (var dialog in new Control[] {
            new TailscaleConfirmationDialog(plan, layoutClient.Control, "test-host", language, _ => { }),
            new TailscaleConfirmationDialog(logoutPlan, layoutClient.Control, "test-host", language, _ => { }),
            new TailscalePreferencesDialog(layoutClient.Control.Preferences!, [], "windows", language, _ => { }),
            new TailscaleAboutDialog(new TailscaleAboutText(language, "0.1.0", "test-host", "1.test", "host", "example.ts.net", "owner@example.com"), () => { }),
            new TailscaleRecoveryDialog(new(plan.OperationId,layoutClient.Control,true,ExpiresAt:DateTimeOffset.UtcNow.AddMinutes(5)),"test-host",language,_=>{ }),
            new TailscaleLoginDialog("test-host", plan.OperationId, false, "", language, _=>{}),
            new TailscaleLoginDialog("test-host", plan.OperationId, true, "Running", language, _=>{}) })
        {
            var host = new Window { Content = dialog, Width = 560, Height = 600 }; host.Show(); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            Check(buttons.All(button => button.TranslatePoint(default, dialog) is { } point && point.Y >= 0 && point.Y + button.Bounds.Height <= dialog.Bounds.Height + 1), "dialog buttons visible " + culture);
            using var image = new RenderTargetBitmap(new PixelSize(560,600)); image.Render(host); image.Save(Path.Combine(output, culture + "-" + dialog.GetType().Name + "-" + dialogIndex++ + ".png"), new PngBitmapEncoderOptions());
            host.Close(); Dispatcher.UIThread.RunJobs();
        }
    }
    Console.WriteLine($"{checks} Tailscale desktop behavior/transport/layout checks passed.");
    Console.WriteLine("Previews: " + output);
}
finally { folder.Delete(true); }

}

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Avalonia.Controls.DataGrid/")) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        Styles.Add(new StyleInclude(new Uri("avares://RelaxKonOS.UI/")) { Source = new Uri("avares://RelaxKonOS.UI/Themes/RelaxKonOSTheme.axaml") });
    }
}
sealed class Broker : IHostElevationBroker
{
    public int Calls; public int GrantCalls; public bool GrantResult = true;
    public HostElevationCapability Capability; public string? Target;
    public Task<T> ExecuteAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> operation, CancellationToken cancellationToken = default)
    { Calls++; Capability = capability; Target = target; return operation(); }
    public Task<bool> RequestGrantAsync(HostElevationCapability capability, string target, CancellationToken cancellationToken = default)
    { GrantCalls++; Capability = capability; Target = target; return Task.FromResult(GrantResult); }
}
sealed class BrokenReferences : ITailscaleOperationReferences
{
    public IReadOnlyList<Guid> Read(HostSettingsConnection connection) => [];
    public void Remember(HostSettingsConnection connection, Guid operationId) => throw new IOException("private local path");
}
sealed class FakeClient : ITailscaleClient
{
    public event EventHandler<AuthSessionStateChangedEventArgs>? SessionChanged;
    public HostSettingsConnection Connection = new("test-host", Guid.NewGuid(), Guid.NewGuid());
    public TailscaleControlSnapshot Control = new(new('A',64), false, true, new(true,false),true,
        Preferences: new(true, false, false, null, false, false, false, false), Platform: "windows");
    public int ControlReads, Prepares, Executions;
    public bool LoseResponse, BadPlan, UnknownResult, LoseCloseResponse, BadClosure, BadClosureTimestamp, PreparedRead, SuppressClosure;
    public bool Restricted, StatusError, ControlError, WithExitNodePeer, LoggedOut;
    public bool RecoveryReady = true;
    public int CloseCalls;
    public TaskCompletionSource<TailscaleOperation>? HoldPlan, HoldExecute;
    public TailscalePrepareRequest? LastRequest;
    public TailscaleOperation? LastPlan, LastResult;
    private readonly Dictionary<Guid,TailscaleOperation> _operations = new();
    public void SwitchHost() { Connection = Connection with { ServiceId = Guid.NewGuid().ToString(), SessionId = Guid.NewGuid() }; SessionChanged?.Invoke(this,new(AuthSessionState.Authenticated)); }
    public HostSettingsConnection CaptureConnection() => Connection;
    public bool IsCurrent(HostSettingsConnection connection) => connection == Connection;
    public Task<TailscaleStatusDto> GetStatusAsync(HostSettingsConnection connection, CancellationToken ct)
    {
        if (StatusError) throw new HttpRequestException("tailscale.unavailable");
        var self = new TailscalePeerDto("node", "host", "host.example", ["100.64.0.1"], true, false,
            Os: "linux", Tags: ["server"], KeyExpiry: DateTimeOffset.UtcNow.AddDays(60), Owner: "owner@example.com");
        var peers = WithExitNodePeer
            ? new[] { new TailscalePeerDto("exit", "gateway", "gateway.example", ["100.64.0.9"], true, false,
                Os: "linux", ExitNodeOption: true, Tags: ["exit"], Owner: "owner@example.com") }
            : [];
        return Task.FromResult(new TailscaleStatusDto("observed","1.test",LoggedOut ? "NeedsLogin" : "Stopped",self,
            Restricted ? null : peers, PeersRestricted: Restricted, TailnetName: LoggedOut ? null : "example.ts.net",
            MagicDnsSuffix: "example.ts.net", Account: LoggedOut ? null : new("owner@example.com", "Owner Name")));
    }
    public Task<TailscaleControlSnapshot> GetControlAsync(HostSettingsConnection connection, CancellationToken ct)
    {
        ControlReads++;
        if (ControlError) throw new HttpRequestException("tailscale.control_unavailable");
        return Task.FromResult(Control);
    }
    public Task<TailscaleOperation> PrepareAsync(HostSettingsConnection connection,TailscalePrepareRequest request,CancellationToken ct)
    {
        Prepares++; LastRequest=request;
        LastPlan=new(request.OperationId,request.Action,TailscaleOperationState.Prepared,request.ExpectedRevision,DateTimeOffset.UtcNow.AddMinutes(5),DateTimeOffset.UtcNow,request.Configuration, Preferences: request.Preferences);
        _operations[request.OperationId]=LastPlan;
        return HoldPlan?.Task ?? Task.FromResult(BadPlan ? LastPlan with { OperationId=Guid.NewGuid() } : LastPlan);
    }
    public Task<TailscaleOperation> ExecuteAsync(HostSettingsConnection connection,Guid operationId,CancellationToken ct)
    {
        Executions++; var plan=_operations[operationId];
        if (plan.Action == TailscaleAction.Logout) LoggedOut = true;
        Control=Control with { Connected=plan.Action is TailscaleAction.Configure or TailscaleAction.ConfigurePreferences ? Control.Connected : plan.Action==TailscaleAction.Connect,
            Authenticated=plan.Action==TailscaleAction.Logout ? false : Control.Authenticated,
            CanControl=plan.Action==TailscaleAction.Logout ? false : Control.CanControl,
            Configuration=plan.Configuration??Control.Configuration, Preferences=plan.Preferences??Control.Preferences, Revision=new('B',64) };
        LastResult=plan with { State=UnknownResult ? TailscaleOperationState.Unknown : TailscaleOperationState.Applied,ObservedRevision=UnknownResult ? null : Control.Revision }; _operations[operationId]=LastResult;
        if(LoseResponse) { LoseResponse=false; throw new HttpRequestException("private transport detail"); }
        return HoldExecute?.Task ?? Task.FromResult(LastResult);
    }
    public Task<TailscaleOperation> GetOperationAsync(HostSettingsConnection connection,Guid operationId,CancellationToken ct)
    {
        var operation=_operations[operationId];
        if(PreparedRead) operation=operation with { State=TailscaleOperationState.Prepared,ObservedRevision=null,Closure=null };
        if(SuppressClosure) operation=operation with { Closure=null };
        if(operation.Closure is { } closure)
        {
            if(BadClosure) operation=operation with { Closure=closure with { Actor=Guid.NewGuid().ToString("D") } };
            if(BadClosureTimestamp) operation=operation with { Closure=closure with { ClosedAt=default } };
        }
        return Task.FromResult(operation);
    }
    public Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(HostSettingsConnection connection,Guid operationId,CancellationToken ct)
        => Task.FromResult(_operations[operationId].Closure is { } closure
            ? new TailscaleRecoverySnapshot(operationId,closure.Observation,false,closure)
            : new TailscaleRecoverySnapshot(operationId,RecoveryReady ? Control : null,RecoveryReady,
                ProblemCode:RecoveryReady ? null : "tailscale.recovery.cli_active",ExpiresAt:DateTimeOffset.UtcNow.AddMinutes(5)));
    public Task<TailscaleOperation> CloseAsync(HostSettingsConnection connection,TailscaleCloseRequest request,CancellationToken ct)
    {
        CloseCalls++;
        var operation=_operations[request.OperationId] with { Closure=new(request.OperationId,Control,DateTimeOffset.UtcNow,connection.UserId.ToString("D")) };
        _operations[request.OperationId]=operation;
        if(LoseCloseResponse) { LoseCloseResponse=false; throw new HttpRequestException("private close reply diagnostics"); }
        return Task.FromResult(operation);
    }
}

