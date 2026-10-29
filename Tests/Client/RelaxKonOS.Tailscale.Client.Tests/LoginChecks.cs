using RelaxKonOS.Client.Apps.Tailscale;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Tailscale;

internal static class LoginChecks
{
    internal static async Task<int> RunAsync(LocalizationService language, string folder)
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Login client assertion " + count); count++; }
        var client = new LoginFake(); var broker = new Broker();
        var path = Path.Combine(folder, "login-checks.json");
        var store = new TailscaleOperationReferences(path);
        var model = new TailscaleLoginViewModel(client, store, broker, language);
        model.Activate();
        Check(model.CanStart && client.Prepares == 0);
        await model.StartAsync();
        Check(client.Prepares == 1 && client.Executes == 0 && model.Operations.Count == 1);
        model.ConfirmAsync = (_, _, _) => Task.FromResult(false);
        await model.StartAsync(); Check(client.Executes == 0);
        model.ConfirmAsync = (_, _, _) => Task.FromResult(true);
        client.BadPlan = true;
        await model.StartAsync(); Check(client.Executes == 0);
        client.BadPlan = false;
        client.LoseExecute = true;
        await model.StartAsync();
        Check(client.Executes == 1 && !model.CanStart && !model.CanOpen && model.CanRead);
        var pending = model.Selected;
        model.Selected = model.Operations.Last(); await model.ReadAsync();
        Check(!model.CanStart && !model.CanOpen);
        model.Selected = pending; await model.ReadAsync();
        Check(client.Executes == 1 && model.CanOpen);
        var opened = 0;
        model.OpenBrowserAsync = uri => { Check(uri.OriginalString == LoginFake.Url); opened++; return Task.FromResult(true); };
        await model.OpenAsync(); Check(opened == 1 && client.Challenges == 1 && client.Executes == 1);
        Check(!File.ReadAllText(path).Contains("login.tailscale.com") && !File.ReadAllText(path).Contains("Issued"));
        client.BadChallenge = true;
        await model.OpenAsync(); Check(opened == 1);
        client.BadChallenge = false;
        client.SwitchOnChallenge = true;
        await model.OpenAsync(); Check(opened == 1);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(model.Operations.Count > 0 && !model.CanOpen);
        model.Deactivate();

        var broken = new TailscaleLoginViewModel(client, new BrokenReferences(), broker, language)
            { ConfirmAsync = (_, _, _) => Task.FromResult(true) };
        broken.Activate(); await broken.StartAsync(); Check(client.Executes == 1); broken.Deactivate();

        var recoveryClient = new LoginFake();
        var recoveryStore = new TailscaleOperationReferences(Path.Combine(folder, "login-recovery.json"));
        var recovery = new TailscaleLoginViewModel(recoveryClient, recoveryStore, broker, language)
            { ConfirmAsync = (_, _, _) => Task.FromResult(true) };
        recovery.Activate(); await recovery.StartAsync();
        Check(recovery.CanOpen && recovery.CanInspect && !recovery.CanClose);
        await recovery.InspectAsync(); Check(recovery.CanClose);
        recovery.ConfirmAsync = (_, _, _) => Task.FromResult(false);
        await recovery.CloseAsync(); Check(recoveryClient.Closes == 0);
        recovery.ConfirmAsync = (_, _, _) => Task.FromResult(true);
        recoveryClient.LoseClose = true;
        await recovery.CloseAsync(); Check(recoveryClient.Closes == 1 && !recovery.CanOpen);
        await recovery.ReadAsync(); Check(recoveryClient.Closes == 1 && recovery.CanStart && !recovery.CanOpen);
        var id = recovery.Selected!.Id;
        recovery.Deactivate(); recovery.Activate();
        Check(recovery.Selected?.Id == id && !recovery.CanOpen);
        await recovery.ReadAsync(); Check(recovery.CanStart && !recovery.CanInspect && recoveryClient.Executes == 1);
        recovery.Deactivate();

        var approvalClient = new LoginFake { ResultState = TailscaleLoginState.ApprovalRequired };
        var approvalModel = new TailscaleLoginViewModel(approvalClient, store, broker, language)
            { ConfirmAsync = (_, _, _) => Task.FromResult(true) };
        approvalModel.Activate(); await approvalModel.StartAsync();
        Check(!approvalModel.CanOpen && approvalModel.CanInspect);
        await approvalModel.InspectAsync(); Check(approvalModel.CanClose);
        await approvalModel.CloseAsync(); Check(approvalModel.CanStart && approvalClient.Closes == 1);
        approvalModel.Deactivate();

        var heldClient = new LoginFake { HoldPrepare = new() };
        var held = new TailscaleLoginViewModel(heldClient, store, broker, language)
            { ConfirmAsync = (_, _, _) => Task.FromResult(true) };
        held.Activate(); var waiting = held.StartAsync(); held.Deactivate();
        heldClient.HoldPrepare.SetResult(heldClient.Last!); await waiting;
        Check(heldClient.Executes == 0 && held.Operations.Count == 0 && !held.IsBusy);
        return count;
    }
}

internal sealed class LoginFake : ITailscaleLoginClient
{
    internal const string Url = "https://login.tailscale.com/a/0123456789abcdef";
    public event EventHandler<AuthSessionStateChangedEventArgs>? SessionChanged;
    internal HostSettingsConnection Connection = new("login-host", Guid.NewGuid(), Guid.NewGuid());
    internal int Prepares, Executes, Challenges, Closes;
    internal bool BadPlan, BadChallenge, LoseExecute, LoseClose, SwitchOnChallenge;
    internal TaskCompletionSource<TailscaleLoginOperation>? HoldPrepare;
    internal TailscaleLoginOperation? Last;
    internal TailscaleLoginState ResultState = TailscaleLoginState.Issued;
    private readonly Dictionary<Guid, TailscaleLoginOperation> _records = [];
    private readonly TailscaleLoginObservation _observation = new(new('B',64),new('C',64),"Running",false);
    public HostSettingsConnection CaptureConnection() => Connection;
    public bool IsCurrent(HostSettingsConnection connection) => connection == Connection;
    public Task<TailscaleLoginOperation> PrepareAsync(HostSettingsConnection connection, Guid id, CancellationToken ct)
    {
        Prepares++; Last = new(id,TailscaleLoginState.Prepared,new('A',64),DateTimeOffset.UtcNow.AddMinutes(5),DateTimeOffset.UtcNow);
        _records[id] = Last;
        return HoldPrepare?.Task ?? Task.FromResult(BadPlan ? Last with { OperationId = Guid.NewGuid() } : Last);
    }
    public Task<TailscaleLoginOperation> ExecuteAsync(HostSettingsConnection connection, Guid id, CancellationToken ct)
    {
        Executes++; var result = _records[id] with { State = ResultState }; _records[id] = result;
        if (LoseExecute) { LoseExecute = false; throw new HttpRequestException("private"); }
        return Task.FromResult(result);
    }
    public Task<TailscaleLoginOperation> GetAsync(HostSettingsConnection connection, Guid id, CancellationToken ct) => Task.FromResult(_records[id]);
    public Task<TailscaleLoginChallengeResponse> ChallengeAsync(HostSettingsConnection connection, Guid id, CancellationToken ct)
    {
        Challenges++;
        if (SwitchOnChallenge) { Connection = Connection with { SessionId = Guid.NewGuid() }; SessionChanged?.Invoke(this,new(AuthSessionState.Authenticated)); }
        return Task.FromResult(new TailscaleLoginChallengeResponse(id, BadChallenge ? "https://example.com/secret" : Url, _records[id].ExpiresAt));
    }
    public Task<TailscaleLoginRecoverySnapshot> RecoveryAsync(HostSettingsConnection connection, Guid id, CancellationToken ct)
        => Task.FromResult(new TailscaleLoginRecoverySnapshot(id,_observation,true,ExpiresAt:DateTimeOffset.UtcNow.AddMinutes(5)));
    public Task<TailscaleLoginOperation> CloseAsync(HostSettingsConnection connection, TailscaleLoginCloseRequest request, CancellationToken ct)
    {
        Closes++; var plan = _records[request.OperationId];
        var result = plan with { Closure = new(new(plan.OperationId,Guid.NewGuid(),connection.UserId,connection.SessionId,plan.ExpectedRevision,plan.ExpiresAt),_observation,DateTimeOffset.UtcNow) };
        _records[request.OperationId] = result;
        if (LoseClose) { LoseClose = false; throw new HttpRequestException("private"); }
        return Task.FromResult(result);
    }
}
