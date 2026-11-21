using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Apps.Explorer;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using RelaxKonOS.App.ProcessGuardian;
using System.Runtime.CompilerServices;
using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.ProcessGuardian;

internal static class GuardianCapabilityChecks
{
    public static void VerifyViews()
    {
        var guardian = DispatchProxy.Create<IServerGuardian, GuardianCapabilityClient>();
        var views = new ProcessGuardianViews(key => key);
        var model = new ProcessGuardianViewModel(guardian, () => "fixture", () => true, key => key, _ => null);
        var workspace = views.CreateWorkspace(model);
        var table = workspace.GetLogicalDescendants().OfType<DataGrid>().Single();
        if (!ReferenceEquals(table.ItemsSource, model.Workloads) || table.Columns.Count != 8)
            throw new InvalidOperationException("Guardian workspace did not bind the workload table.");

        var pickedExecutable = false;
        var editor = views.CreateEditor(model, () => { },
            () => { pickedExecutable = true; return Task.CompletedTask; }, () => Task.CompletedTask);
        var picker = editor.GetLogicalDescendants().OfType<Button>().First(button => Equals(button.Content, "guardian.create.browse"));
        picker.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        if (!pickedExecutable) throw new InvalidOperationException("Guardian editor did not delegate executable selection.");
        var readOnly = new ProcessGuardianViewModel(guardian, () => "fixture", () => true, key => key, _ => null, () => false);
        if (readOnly.OpenCreateWorkloadCommand.CanExecute(null) || readOnly.StartWorkloadCommand.CanExecute(null)
            || !readOnly.OpenLogsCommand.CanExecute(null))
            throw new InvalidOperationException("Guardian did not disable management commands for a read-only grant.");

        Console.WriteLine("Guardian application workspace and editor checks passed.");
    }

    public static void VerifyHostInteraction(ISystemLanguage language, ShellSettings settings)
    {
        var id = new AppId("relaxkonos.processguardian");
        var grants = new CertificateCapabilityChecks.Permission(id.Value);
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "guardian-host-memory.json"));
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        using var http = new HttpClient();
        var factory = new ExternalAppContextFactory(grants, grants, language, settings, null!, auth, null!,
            windows, null!, new ExplorerClient(http, auth, memory), new SshDesktopSession(null!, settings), null!,
            null!, null!, null!, null!, memory, null!, null!, null!, null!);
        var manifest = new ApplicationManifest(id, "Process Guardian", "1.0.0", "", "",
            [AppPermissions.ServerGuardianRead, AppPermissions.ServerGuardianManage]);
        var guardian = factory.Create(manifest).GuardianHost
            ?? throw new InvalidOperationException("Missing Guardian host interactions.");
        var foreign = factory.Create(manifest with { Id = new AppId("fixture.other") });
        if (foreign.GuardianHost is not null || guardian.CurrentUsername is not null)
            throw new InvalidOperationException("Guardian host interactions exposed the session without an app grant.");
        grants.Granted.Add(AppPermissions.ServerGuardianRead);
        if (guardian.CurrentUsername != "fixture")
            throw new InvalidOperationException("Guardian could not read the current username with a read grant.");
        ((EventAlertsAuthFixture)(object)auth).ServiceId = "http://replacement.invalid";
        if (guardian.CurrentUsername is not null)
            throw new InvalidOperationException("Guardian host interactions exposed the replaced session.");
        Console.WriteLine("Guardian host interaction access checks passed.");
    }

    public static async Task RunAsync()
    {
        var id = new AppId("relaxkonos.processguardian");
        var grants = new CertificateCapabilityChecks.Permission(id.Value);
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "guardian-memory.json"));
        var client = DispatchProxy.Create<IServerGuardian, GuardianCapabilityClient>();
        var calls = (GuardianCapabilityClient)(object)client;
        using var lifetime = new CancellationTokenSource();
        IServerGuardian guardian = new ServerGuardianCapability(id, grants,
            new ExternalServerConnection(memory.Capture(auth)), client, lifetime.Token);

        await RejectAsync<UnauthorizedAccessException>(() => guardian.ListWorkloadsAsync());
        await using (var denied = guardian.WatchLogsAsync("worker").GetAsyncEnumerator())
            await RejectAsync<UnauthorizedAccessException>(() => denied.MoveNextAsync().AsTask());

        grants.Granted.Add(AppPermissions.ServerGuardianManage);
        await RejectAsync<UnauthorizedAccessException>(() => guardian.ApplyActionAsync("worker", "stop"));
        grants.Granted.Remove(AppPermissions.ServerGuardianManage);
        grants.Granted.Add(AppPermissions.ServerGuardianRead);
        await guardian.ListWorkloadsAsync();
        await RejectAsync<UnauthorizedAccessException>(() => guardian.ApplyActionAsync("worker", "stop"));
        if (calls.Reads != 1 || calls.Writes != 0) throw new InvalidOperationException("Guardian grant check failed.");

        grants.Granted.Add(AppPermissions.ServerGuardianManage);
        await guardian.ApplyActionAsync("worker", "stop");
        if (calls.Writes != 1) throw new InvalidOperationException("Guardian management call was not delegated.");

        await using var logs = guardian.WatchLogsAsync("worker").GetAsyncEnumerator();
        if (!await logs.MoveNextAsync() || logs.Current.Count != 0)
            throw new InvalidOperationException("Guardian did not forward the log snapshot.");
        var nextSnapshot = logs.MoveNextAsync().AsTask();

        var session = (EventAlertsAuthFixture)(object)auth;
        session.ServiceId = "http://replacement.invalid";
        session.NotifyStateChanged();
        await RejectAsync<InvalidOperationException>(() => guardian.ListWorkloadsAsync());
        await RejectAsync<OperationCanceledException>(async () => await nextSnapshot.WaitAsync(TimeSpan.FromSeconds(5)));
        lifetime.Cancel();
        await RejectAsync<OperationCanceledException>(() => guardian.ListWorkloadsAsync());
        if (calls.Reads != 1) throw new InvalidOperationException("Guardian read reached an invalidated connection.");
        var editor = new ProcessGuardianViewModel(client, () => "fixture", () => true, key => key, _ => null)
        {
            DefinitionId = "worker",
            DefinitionName = "Worker",
            ExecutablePath = "/bin/worker",
            WorkingDirectory = "/work",
            RunAs = "other",
        };
        var approvedDraftWasCaptured = false;
        editor.SaveWithAdministratorApprovalAsync = definition =>
        {
            approvedDraftWasCaptured = definition.RunAs == "other" && definition.ExecutablePath == "/bin/worker" && editor.IsLoading;
            return Task.FromResult<GuardianAgentResponse?>(null);
        };
        await editor.CreateWorkloadCommand.ExecuteAsync(null);
        if (!approvedDraftWasCaptured || editor.IsLoading || calls.Writes != 1)
            throw new InvalidOperationException("Cancelling Guardian approval submitted a workload.");

        Console.WriteLine("Guardian SDK permission and lifetime checks passed.");
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

public class GuardianCapabilityClient : DispatchProxy
{
    public int Reads { get; private set; }
    public int Writes { get; private set; }

    protected override object Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case nameof(IServerGuardian.GetStatusAsync):
                return Task.FromResult(new GuardianStatusDto(true, true, string.Empty, "1.0.0"));
            case nameof(IServerGuardian.ListWorkloadsAsync):
                Reads++;
                return Task.FromResult<IReadOnlyList<GuardianWorkloadDto>>([]);
            case nameof(IServerGuardian.UpsertAsync):
            case nameof(IServerGuardian.ApplyActionAsync):
                Writes++;
                return Task.FromResult(new GuardianAgentResponse(true, string.Empty));
            case nameof(IServerGuardian.WatchLogsAsync):
                return WatchAsync((CancellationToken)args![1]!);
            default:
                throw new NotSupportedException(method?.Name);
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<GuardianLogEntryDto>> WatchAsync(
        [EnumeratorCancellation] CancellationToken token)
    {
        yield return [];
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
}
