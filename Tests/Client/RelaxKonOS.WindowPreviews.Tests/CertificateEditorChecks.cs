using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Certificates;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.WindowManager;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

internal static class CertificateEditorChecks
{
    public static void Run()
    {
        foreach (var selfSigned in new[] { false, true })
        {
            var client = DispatchProxy.Create<IRemoteCertificateClient, CertificateEditorClient>();
            var stub = (CertificateEditorClient)(object)client;
            var vm = new CertificateManagerViewModel(client, DispatchProxy.Create<IAuthSession, SiteEditorSession>(), new FirewallTestPermissions())
            {
                Domains = "example.test", ContactEmail = "test@example.test", AcceptedTerms = true,
                SelfSignedDomains = "example.test"
            };
            var manager = new WindowManagerService();
            manager.Attach(new Canvas { Width = 1000, Height = 700 });
            var owner = manager.Create(new WindowCreateOptions(new("test.certificates"), "Owner", new TextBlock(),
                new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 600)));
            ModalDialog<bool>? handle = null;
            Control? editor = null;
            var result = manager.ShowDialogAsync<bool>(owner, "Certificate", dialog =>
            {
                handle = dialog;
                var name = selfSigned ? "SelfSignedCertificateDialogView" : "CertificateRequestDialogView";
                var type = typeof(CertificateManagerViewModel).Assembly.GetType("RelaxKonOS.Client.Apps.Certificates.Views." + name)!;
                editor = (Control)Activator.CreateInstance(type, [vm, dialog])!;
                return editor;
            });
            if (selfSigned) vm.SelfSignedValidityDays = 366;
            else vm.PublicReachabilityConfirmed = true;
            handle!.Cancel();
            Check(manager.Windows.Count == 3, "Certificate edits require discard confirmation.");
            Confirmation(manager).NoCommand.Execute(null);
            PumpUntil(() => manager.Windows.Count == 2);
            Check(!result.IsCompleted && (selfSigned ? vm.SelfSignedValidityDays == 366 : vm.PublicReachabilityConfirmed),
                "Keeping changes preserves certificate options.");
            vm.SelectedKeyAlgorithm = null;
            var invalid = selfSigned ? vm.TryCreateSelfSignedCertificateAsync() : vm.TryRequestCertificateAsync();
            Check(!invalid.GetAwaiter().GetResult() && stub.Calls == 0, "Invalid certificate input sends no request.");
            Dispatcher.UIThread.RunJobs();
            var feedback = editor!.FindControl<TextBlock>("EditorValidationFeedback")!;
            Check(feedback.IsVisible && feedback.Text == LocalizedText.Get("certificates.validation.key_algorithm_required"),
                "Certificate validation failure is visible inside its editor.");
            vm.SelectedKeyAlgorithm = vm.KeyAlgorithms[0];
            var submitted = selfSigned ? vm.TryCreateSelfSignedCertificateAsync() : vm.TryRequestCertificateAsync();
            Check(vm.IsOperationRunning && !vm.CanEditCertificateDraft, "Certificate submission locks fields.");
            var repeated = selfSigned ? vm.TryCreateSelfSignedCertificateAsync() : vm.TryRequestCertificateAsync();
            Check(!repeated.GetAwaiter().GetResult() && stub.Calls == 1, "Direct repeated certificate submission is blocked.");
            manager.Close(handle.Window!);
            Check(!result.IsCompleted && manager.Windows.Count == 2, "Certificate window close is blocked while running.");
            if (selfSigned)
            {
                vm.SelfSignedValidityDays = 400;
                Check(stub.Request is CreateSelfSignedCertificateRequest { ValidityDays: 366 }, "Self-signed request freezes the validated validity period.");
            }
            stub.Pending.SetResult(new(Guid.Empty, null, "test", CertificateOperationState.Failed, "failed", "certificate.test_rejected", null, null));
            PumpUntil(() => submitted.IsCompleted);
            Check(!submitted.GetAwaiter().GetResult() && vm.CanEditCertificateDraft && !result.IsCompleted,
                "A rejected certificate request restores editing without closing the draft.");
            handle.Cancel();
            Confirmation(manager).YesCommand.Execute(null);
            PumpUntil(() => manager.Windows.Count == 1);
            Check(result.IsCompleted, "Confirmed certificate discard closes the editor.");
            manager.Close(owner);
        }
        Console.WriteLine("Certificate editor checks passed.");
    }
    private static ConfirmDialogViewModel Confirmation(WindowManagerService manager)
        => (ConfirmDialogViewModel)((Control)manager.Windows.Last().View.Content!).DataContext!;
    private static void PumpUntil(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, 2000))
            throw new Exception("Certificate editor transition did not complete.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}

public class CertificateEditorClient : DispatchProxy
{
    public TaskCompletionSource<CertificateOperationDto> Pending = new();
    public object? Request;
    public int Calls;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name is not ("RequestAsync" or "CreateSelfSignedAsync")) throw new NotSupportedException(method.Name);
        Request = args![0];
        Calls++;
        return Pending.Task;
    }
}
