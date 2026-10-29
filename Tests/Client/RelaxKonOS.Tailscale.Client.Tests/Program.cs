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
    var client = new FakeClient();
    var broker = new Broker();
    var store = new TailscaleOperationReferences(Path.Combine(folder.FullName, "refs.json"));
    var model = new TailscaleViewModel(client, store, broker, language);
    await model.ActivateAsync();
    Check(model.Peers.Count == 1 && !model.CanConnect && client.ControlReads == 0 && broker.Calls == 0, "launch only observes status");
    await model.LoadControlAsync();
    Check(model.CanConnect && model.CanConfigure && !model.CanDisconnect, "controls depend on fresh baseline");
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
    await model.LoadControlAsync();
    Check(model.CanDisconnect && !model.CanConnect, "fresh connected baseline");
    var preparations = client.Prepares;
    model.EditConfigurationAsync = _ => Task.FromResult<TailscaleConfiguration?>(null);
    await model.ConfigureAsync();
    Check(client.Prepares == preparations, "cancel editor does not prepare");
    model.EditConfigurationAsync = value => Task.FromResult<TailscaleConfiguration?>(value);
    await model.ConfigureAsync();
    Check(client.Prepares == preparations, "unchanged config does not prepare");
    model.EditConfigurationAsync = _ => Task.FromResult<TailscaleConfiguration?>(new(false, true));
    await model.ConfigureAsync();
    Check(client.LastRequest?.Action == TailscaleAction.Configure && client.LastRequest.Configuration == new TailscaleConfiguration(false, true), "structured configuration only");
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
    Check(client.Executions == executed && reopened.Peers.Count == 0 && !reopened.IsBusy, "late response cannot update new host");
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
    checks += await TransportChecks.RunAsync();
    checks += await LoginChecks.RunAsync(language, folder.FullName);
    checks += await LoginTransportChecks.RunAsync();

    // Render the actual workspace pages and fixed-footer dialogs in every shipped language.
    var output = Path.Combine(System.AppContext.BaseDirectory, "layout-qa"); Directory.CreateDirectory(output);
    foreach (var culture in new[] { "en-US", "zh-CN", "ja-JP" })
    {
        settings.Language = culture; settings.Appearance = settings.Appearance with { Mode = RelaxKonOS.Protocol.Desktop.ThemeKind.Light }; Dispatcher.UIThread.RunJobs();
        var layoutClient = new FakeClient();
        var layout = new TailscaleViewModel(layoutClient, new TailscaleOperationReferences(Path.Combine(folder.FullName, "layout.json")), broker, language);
        var loginLayout = new TailscaleLoginViewModel(new LoginFake { Connection = layoutClient.Connection }, new TailscaleOperationReferences(Path.Combine(folder.FullName, "login-layout.json")), broker, language);
        var workspace = new TailscaleWorkspace(layout, loginLayout);
        var window = new Window { Content = workspace, Width = 980, Height = 640 }; window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        await layout.LoadControlAsync();
        loginLayout.ConfirmAsync = (_, _, _) => Task.FromResult(true);
        await loginLayout.StartAsync();
        var oldText = layout.StatusText; var oldPeer = layout.Peers[0].Status; var reads = layoutClient.ControlReads;
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
            if (page == "Devices") Check(workspace.GetVisualDescendants().OfType<DataGrid>().Single().CanUserResizeColumns, "resizable device columns");
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
        var dialogIndex = 0;
        foreach (var dialog in new Control[] { new TailscaleConfigurationDialog(new(true,false), _ => { }),
            new TailscaleConfirmationDialog(plan, layoutClient.Control, "test-host", language, _ => { }),
            new TailscaleRecoveryDialog(new(plan.OperationId,layoutClient.Control,true,ExpiresAt:DateTimeOffset.UtcNow.AddMinutes(5)),"test-host",language,_=>{ }),
            new TailscaleLoginDialog("test-host", plan.OperationId, false, "", language, _=>{}),
            new TailscaleLoginDialog("test-host", plan.OperationId, true, "Running", language, _=>{}) })
        {
            var host = new Window { Content = dialog, Width = 540, Height = 320 }; host.Show(); Dispatcher.UIThread.RunJobs(); host.UpdateLayout();
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            Check(buttons.All(button => button.TranslatePoint(default, dialog) is { } point && point.Y >= 0 && point.Y + button.Bounds.Height <= dialog.Bounds.Height + 1), "dialog buttons visible " + culture);
            using var image = new RenderTargetBitmap(new PixelSize(540,320)); image.Render(host); image.Save(Path.Combine(output, culture + "-" + dialog.GetType().Name + "-" + dialogIndex++ + ".png"), new PngBitmapEncoderOptions());
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
    public int Calls; public HostElevationCapability Capability; public string? Target;
    public Task<T> ExecuteAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> operation, CancellationToken cancellationToken = default)
    { Calls++; Capability = capability; Target = target; return operation(); }
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
    public TailscaleControlSnapshot Control = new(new('A',64), false, true, new(true,false),true);
    public int ControlReads, Prepares, Executions;
    public bool LoseResponse, BadPlan, UnknownResult, LoseCloseResponse, BadClosure, BadClosureTimestamp, PreparedRead, SuppressClosure;
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
        => Task.FromResult(new TailscaleStatusDto("observed","1.test","Stopped",new("node","host","host.example",["100.64.0.1"],true,false),[]));
    public Task<TailscaleControlSnapshot> GetControlAsync(HostSettingsConnection connection, CancellationToken ct) { ControlReads++; return Task.FromResult(Control); }
    public Task<TailscaleOperation> PrepareAsync(HostSettingsConnection connection,TailscalePrepareRequest request,CancellationToken ct)
    {
        Prepares++; LastRequest=request;
        LastPlan=new(request.OperationId,request.Action,TailscaleOperationState.Prepared,request.ExpectedRevision,DateTimeOffset.UtcNow.AddMinutes(5),DateTimeOffset.UtcNow,request.Configuration);
        _operations[request.OperationId]=LastPlan;
        return HoldPlan?.Task ?? Task.FromResult(BadPlan ? LastPlan with { OperationId=Guid.NewGuid() } : LastPlan);
    }
    public Task<TailscaleOperation> ExecuteAsync(HostSettingsConnection connection,Guid operationId,CancellationToken ct)
    {
        Executions++; var plan=_operations[operationId];
        Control=Control with { Connected=plan.Action==TailscaleAction.Configure ? Control.Connected : plan.Action==TailscaleAction.Connect, Configuration=plan.Configuration??Control.Configuration,Revision=new('B',64) };
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

