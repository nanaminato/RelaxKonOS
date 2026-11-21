using RelaxKonOS.AppSDK;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.WindowManager;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

internal static class FileShareDialogChecks
{
    public static void Run()
    {
        CheckCapabilityAsync().GetAwaiter().GetResult();
        var vm = new FileServicesViewModel(DispatchProxy.Create<IServerFileServices, ShareDialogClient>(), new FirewallTestPermissions(), () => true);
        vm.StartAsync().GetAwaiter().GetResult();
        vm.ShareName = "test-share";
        vm.SharePath = "/test/share";
        vm.AddSharePermission("test-user", FileShareAccess.Read);
        Check(vm.CanManage, "Current SMB capabilities allow editing before a prompt starts.");
        var manager = new WindowManagerService();
        manager.Attach(new Canvas { Width = 1000, Height = 700 });
        var owner = manager.Create(new WindowCreateOptions(new("test.smb"), "Owner", new TextBlock(),
            new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 600)));
        ModalDialog<bool>? handle = null;
        var result = manager.ShowDialogAsync<bool>(owner, "Share", dialog =>
        {
            handle = dialog;
            var type = typeof(FileServicesViewModel).Assembly.GetType("RelaxKonOS.Client.Apps.FileServices.Views.FileServicesShareDialogView")!;
            return (Control)Activator.CreateInstance(type, [vm, dialog, false])!;
        });
        vm.SharePermissions[0].SelectedAccess = vm.SharePermissions[0].AccessOptions.Single(option => option.Value == FileShareAccess.ReadWrite);
        handle!.Cancel();
        Check(manager.Windows.Count == 3, "Editing an SMB permission row requires discard confirmation.");
        Confirmation(manager).NoCommand.Execute(null);
        PumpUntil(() => manager.Windows.Count == 2);
        Check(!result.IsCompleted && vm.SharePermissions[0].SelectedAccess.Value == FileShareAccess.ReadWrite,
            "Keeping changes preserves SMB permission input.");
        vm.IsAwaitingInput = true;
        manager.Close(handle.Window!);
        Check(!vm.CanEditShareDraft && !vm.CanManage && !result.IsCompleted,
            "Path or elevation confirmation locks fields and window close without showing a host operation.");
        Check(!vm.SaveShareAsync(false).GetAwaiter().GetResult(), "Direct save cannot bypass a pending input prompt.");
        vm.IsBusy = true;
        vm.IsAwaitingInput = false;
        manager.Close(handle.Window!);
        Check(!vm.CanEditShareDraft && !result.IsCompleted, "Host mutation keeps SMB draft and close locked.");
        vm.IsBusy = false;
        Check(vm.CanEditShareDraft, "Completing work restores SMB editor interaction.");
        handle.Cancel();
        Confirmation(manager).YesCommand.Execute(null);
        PumpUntil(() => manager.Windows.Count == 1);
        Check(result.IsCompleted, "Confirmed SMB discard closes the editor.");
        manager.Close(owner);
        Console.WriteLine("SMB share dialog checks passed.");
    }

    private static async Task CheckCapabilityAsync()
    {
        var id = new AppId("relaxkonos.file-services");
        var permissions = new CertificateCapabilityChecks.Permission(id.Value);
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var session = (EventAlertsAuthFixture)(object)auth;
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "smb-memory.json"));
        using var lifetime = new CancellationTokenSource();
        var capability = new ServerFileServicesCapability(id, permissions, new ExternalServerConnection(memory.Capture(auth)),
            DispatchProxy.Create<IServerFileServices, ShareDialogClient>(), lifetime.Token);

        await RejectAsync<UnauthorizedAccessException>(() => capability.GetStatusAsync(default));
        permissions.Granted.Add(AppPermissions.ServerFileServicesRead);
        Check((await capability.GetStatusAsync(default)).State == FileServiceRuntimeState.Running,
            "The SMB SDK forwards reads with a valid app grant and connection.");
        await RejectAsync<UnauthorizedAccessException>(() => capability.DeleteShareAsync("share", default));
        await RejectAsync<UnauthorizedAccessException>(() => capability.ElevateAsync(new("fixture", "unused"), default));
        session.ServiceId = "http://replacement.invalid";
        await RejectAsync<InvalidOperationException>(() => capability.GetStatusAsync(default));
        lifetime.Cancel();
        await RejectAsync<OperationCanceledException>(() => capability.GetStatusAsync(default));
        Console.WriteLine("SMB SDK withheld ungranted, stale and cancelled operations.");

        static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
        {
            try { await operation(); }
            catch (T) { return; }
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
    }
    private static ConfirmDialogViewModel Confirmation(WindowManagerService manager)
        => (ConfirmDialogViewModel)((Control)manager.Windows.Last().View.Content!).DataContext!;
    private static void PumpUntil(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, 2000))
            throw new Exception("SMB editor transition did not complete.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}

public class ShareDialogClient : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "GetCapabilitiesAsync" => Task.FromResult(new FileServiceCapabilitiesDto(true, false, false, true, false)),
        "GetStatusAsync" => Task.FromResult(new FileServiceStatusDto(FileServiceProtocol.Smb, FileServiceRuntimeState.Running, "test", true, true)),
        "ListSharesAsync" => Task.FromResult<IReadOnlyList<FileShareDto>>([]),
        "GetConnectionAsync" => Task.FromResult(new FileServiceConnectionInfoDto("test", 445, "test", "smb://test")),
        _ => throw new NotSupportedException(method.Name)
    };
}
