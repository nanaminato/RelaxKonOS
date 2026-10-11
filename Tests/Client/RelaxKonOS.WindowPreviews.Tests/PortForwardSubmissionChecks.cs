using System.Reflection;
using RelaxKonOS.Client.Apps.PortForwarding;
using RelaxKonOS.Client.Apps.PortForwarding.ViewModels;
using RelaxKonOS.Client.Apps.PortForwarding.Views;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.WindowManager;
using Avalonia.Controls;
using Avalonia.Threading;

internal static class PortForwardSubmissionChecks
{
    public static void Run()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "RelaxKonOS-forward-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var blocker = Path.Combine(temporary, "blocker");
        var replacementBlocker = Path.Combine(temporary, "settings.json");
        var successfulPath = Path.Combine(temporary, "valid-settings.json");
        try
        {
            File.WriteAllText(blocker, "test");
            var actual = new PortForwardingService(null!, new PortForwardingSettingsStore(Path.Combine(blocker, "settings.json")));
            var before = actual.GetSettings();
            try { actual.SaveSettings(new PortForwardingSettings("new-host", "new-user", 2222)); throw new Exception("Real settings failure was swallowed."); }
            catch (IOException) { }
            if (actual.GetSettings() != before) throw new Exception("Failed disk save changed active settings.");
            Directory.CreateDirectory(replacementBlocker);
            var replacementService = new PortForwardingService(null!, new PortForwardingSettingsStore(replacementBlocker));
            var replacementBefore = replacementService.GetSettings();
            try { replacementService.SaveSettings(new PortForwardingSettings("new-host", "new-user", 2222)); throw new Exception("Replacement failure was swallowed."); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (replacementService.GetSettings() != replacementBefore || Directory.EnumerateFiles(temporary, "*.tmp").Any())
                throw new Exception("Replacement failure changed memory or retained temporary settings.");
            var concurrentStore = new PortForwardingSettingsStore(successfulPath);
            var concurrentService = new PortForwardingService(null!, concurrentStore);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { concurrentService.StartAsync(PortForwardRequest.Localhost(80), cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Cancelled start reached SSH launch."); }
            catch (OperationCanceledException) { }
            if (concurrentService.List().Count != 0) throw new Exception("Cancelled start registered a forward.");
            Parallel.For(0, 12, index => concurrentService.SaveSettings(new PortForwardingSettings("host-" + index, "user-" + index, 2200 + index)));
            if (concurrentService.GetSettings() != concurrentStore.Load() || Directory.EnumerateFiles(temporary, "*.tmp").Any())
                throw new Exception("Concurrent setting saves diverged between memory and disk.");
        }
        finally { File.Delete(blocker); File.Delete(successfulPath); if (Directory.Exists(replacementBlocker)) Directory.Delete(replacementBlocker); Directory.Delete(temporary); }
        var service = DispatchProxy.Create<IPortForwardingService, PendingForwardService>();
        var fake = (PendingForwardService)service;
        using var vm = new PortForwardingViewModel(service) { TargetAddress = "localhost:80", PreferredLocalPortText = "7300" };
        foreach (var invalid in new[] { "http://remote.test:80", "ftp://localhost:80", "http://test:test@localhost:80", "http://localhost:80/#section" })
        {
            vm.TargetAddress = invalid;
            vm.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            if (fake.Saves != 0 || fake.Starts != 0 || vm.TargetAddress != invalid) throw new Exception("Invalid forward persisted settings or lost its input.");
        }
        vm.TargetAddress = "localhost:80";
        var pending = vm.StartCommand.ExecuteAsync(null);
        if (!vm.IsBusy || fake.Saves != 1 || fake.Starts != 1) throw new Exception("Forward submission did not enter busy state.");
        vm.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        vm.SaveConnectionSettingsCommand.Execute(null);
        vm.OpenCreateForwardCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        if (fake.Saves != 1 || fake.Starts != 1 || vm.TargetAddress != "localhost:80" || vm.PreferredLocalPortText != "7300")
            throw new Exception("Busy forwarding commands changed settings, request count or draft.");
        fake.Pending.SetException(new InvalidOperationException("test refusal"));
        pending.GetAwaiter().GetResult();
        if (vm.IsBusy || vm.TargetAddress != "localhost:80") throw new Exception("Rejected forward lost the draft.");
        fake.FailSave = true;
        vm.SshHost = "unsaved-host";
        vm.SshPassword = "test-only-secret";
        vm.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        if (fake.Starts != 1 || vm.IsBusy || vm.SshHost != "unsaved-host" || vm.SshPassword != "test-only-secret"
            || vm.StatusText.Resolve() != "test settings refusal") throw new Exception("Settings failure was not retained locally before forward submission.");
        Console.WriteLine("PASS: Forward submission blocks duplicate settings writes and draft resets, and retains rejected input.");
        fake.FailSave = false;
        var original = new PortForwardInfo(Guid.NewGuid(), "localhost", 80, 7300, "http", "/", DateTimeOffset.UtcNow, "Running");
        var other = original with { Id = Guid.NewGuid(), RemotePort = 81 };
        fake.Items = [original, other];
        vm.RefreshCommand.Execute(null);
        vm.SelectedForward = original;
        vm.ShowForwardEditorAsync = async _ =>
        {
            var modeSaves = fake.Saves;
            var modeStarts = fake.Starts;
            await vm.StartCommand.ExecuteAsync(null);
            if (fake.Saves != modeSaves || fake.Starts != modeStarts) throw new Exception("Edit-mode forward was submitted as a new connection.");
            vm.TargetAddress = "http://localhost:90";
            vm.SelectedForward = other;
            vm.RefreshCommand.Execute(null);
            if (vm.TargetAddress != "http://localhost:90") throw new Exception("Forward refresh/selection replaced an open draft.");
            await vm.UpdateSelectedCommand.ExecuteAsync(null);
            if (fake.UpdatedId != original.Id) throw new Exception("Open forward editor updated another selection.");
            fake.Items = [original with { RemotePort = 99 }, other];
            var beforeConflict = fake.Saves;
            await vm.UpdateSelectedCommand.ExecuteAsync(null);
            if (fake.Saves != beforeConflict || vm.TargetAddress != "http://localhost:90") throw new Exception("Changed forwarding configuration was overwritten by an old draft.");
            fake.Items = [other];
            var before = fake.Saves;
            await vm.UpdateSelectedCommand.ExecuteAsync(null);
            if (fake.Saves != before || vm.TargetAddress != "http://localhost:90"
                || vm.StatusText.Resolve() != RelaxKonOS.Client.Localization.LocalizedText.Get("port_forwarding.error.target_changed"))
                throw new Exception("Missing original forward changed settings/draft or lacked recovery feedback.");
        };
        vm.OpenEditForwardCommand.ExecuteAsync(original).GetAwaiter().GetResult();
        var manager = new RelaxKonOS.WindowManager.WindowManager();
        manager.Attach(new Canvas { Width = 1000, Height = 700 });
        var owner = manager.Create(new WindowCreateOptions(new("test.forward"), "Owner", new TextBlock(), new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 600)));
        ModalDialog<bool>? handle = null;
        var result = manager.ShowDialogAsync<bool>(owner, "Forward", dialog =>
        { handle = dialog; return new PortForwardingEditorDialogView(vm, dialog, false); });
        vm.TargetAddress = "http://changed.test:80";
        handle!.Cancel();
        if (manager.Windows.Count != 3) throw new Exception("Forward draft closed without confirmation.");
        ((ConfirmDialogViewModel)((Control)manager.Windows.Last().View.Content!).DataContext!).NoCommand.Execute(null);
        Pump(() => manager.Windows.Count == 2);
        if (result.IsCompleted || vm.TargetAddress != "http://changed.test:80") throw new Exception("Keeping forward edits lost the draft.");
        vm.IsBusy = true;
        manager.Close(handle.Window!);
        if (result.IsCompleted) throw new Exception("Busy forward dialog closed.");
        vm.IsBusy = false;
        handle.Cancel();
        ((ConfirmDialogViewModel)((Control)manager.Windows.Last().View.Content!).DataContext!).YesCommand.Execute(null);
        Pump(() => result.IsCompleted);
        manager.Close(owner);
        vm.SshPassword = "test-only-secret";
        var readsBeforeClose = fake.Reads;
        var writesBeforeClose = fake.Saves;
        vm.Dispose();
        vm.RefreshCommand.Execute(null);
        vm.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        if (vm.SshPassword.Length != 0 || fake.Reads != readsBeforeClose || fake.Saves != writesBeforeClose)
            throw new Exception("Disposed forwarding window retained a password or continued reading/submitting.");
        Console.WriteLine("PASS: Forward modal retains kept drafts, blocks busy closing and supports confirmed discard.");
        var lateService = DispatchProxy.Create<IPortForwardingService, PendingForwardService>();
        var lateFake = (PendingForwardService)lateService;
        using var lateVm = new PortForwardingViewModel(lateService) { TargetAddress = "localhost:80" };
        var closeCalls = 0;
        lateVm.CloseForwardEditorAsync = () => { closeCalls++; return Task.CompletedTask; };
        var latePending = lateVm.StartCommand.ExecuteAsync(null);
        var beforeLateReads = lateFake.Reads;
        lateVm.Dispose();
        lateFake.Pending.SetResult(original);
        latePending.GetAwaiter().GetResult();
        if (closeCalls != 0 || lateVm.SelectedForward is not null || lateFake.Reads != beforeLateReads)
            throw new Exception("Late forwarding result changed or closed a disposed window.");
        var failedService = DispatchProxy.Create<IPortForwardingService, PendingForwardService>();
        var failedFake = (PendingForwardService)failedService;
        using var failedVm = new PortForwardingViewModel(failedService) { TargetAddress = "localhost:80" };
        var failedPending = failedVm.StartCommand.ExecuteAsync(null);
        var beforeFailedStatus = failedVm.StatusText.Resolve();
        failedVm.Dispose();
        failedFake.Pending.SetException(new IOException("late test failure"));
        failedPending.GetAwaiter().GetResult();
        if (failedVm.StatusText.Resolve() != beforeFailedStatus) throw new Exception("Late failure replaced a disposed window's status.");
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var probePort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var probeVm = new PortForwardingViewModel(failedService) { SelectedForward = original with { LocalPort = probePort } };
        var beforeProbeStatus = probeVm.StatusText.Resolve();
        var probe = probeVm.TestSelectedCommand.ExecuteAsync(null);
        using var accepted = listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        probeVm.Dispose();
        Pump(() => probe.IsCompleted);
        probe.GetAwaiter().GetResult();
        if (probeVm.StatusText.Resolve() != beforeProbeStatus) throw new Exception("Closed probe changed the disposed window's status.");
    }
    private static void Pump(Func<bool> done)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return done(); }, 2000)) throw new Exception("Forward modal transition timed out.");
    }
}
public class PendingForwardService : DispatchProxy
{
    public int Saves, Starts, Reads;
    public bool FailSave;
    public IReadOnlyList<PortForwardInfo> Items = [];
    public Guid? UpdatedId;
    public TaskCompletionSource<PortForwardInfo> Pending = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "GetSettings" => new PortForwardingSettings(),
        "List" => Read(),
        "add_ForwardsChanged" or "remove_ForwardsChanged" => null,
        "SaveSettings" => Save(),
        "StartAsync" => Start(),
        "UpdateAsync" => Update((Guid)args![0]!),
        _ => throw new NotSupportedException(method.Name)
    };
    private object? Save() { Saves++; if (FailSave) throw new IOException("test settings refusal"); return null; }
    private IReadOnlyList<PortForwardInfo> Read() { Reads++; return Items; }
    private Task<PortForwardInfo> Start() { Starts++; return Pending.Task; }
    private Task<PortForwardInfo> Update(Guid id) { UpdatedId = id; return Task.FromResult(Items.Single(item => item.Id == id)); }
}
