using System.Reflection;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Settings;

internal static class HostIdentityCompletionChecks
{
    public static void Run(LocalizationService localization)
    {
        foreach (var effective in new[] { SettingsEffectiveState.Immediate, SettingsEffectiveState.HostRestart })
        {
            var service = DispatchProxy.Create<IHostIdentityService, IdentityCompletionServiceStub>();
            var stub = (IdentityCompletionServiceStub)service;
            stub.Effective = effective;
            using var vm = new HostIdentityEditorViewModel(service,
                DispatchProxy.Create<IAuthSession, LanguageSessionStub>(), localization);
            vm.RequestAuthorizationAsync = _ => Task.FromResult(true);
            Check(!vm.CanEdit && !vm.ShowApplyAction && !vm.ShowQueryAction, "Unloaded state");
            vm.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            vm.DraftName = "WIN-3QLG75ESVRUX";
            Check(stub.MaximumLength == 15 ? vm.HasNameProblem && !vm.CanApply
                && vm.NameProblem.Contains("16") && vm.NameProblem.Contains("15")
                : !vm.HasNameProblem && vm.CanApply, "Remote platform length and specific feedback");
            vm.DraftName = "WIN-3QLG75ESVRU";
            Check(!vm.HasNameProblem && vm.CanApply, "Windows 15-character boundary");
            vm.DraftName = "new-host";
            Check(vm.CanApply && vm.HasDraft && vm.ResetDraftCommand.CanExecute(null), "Changed name can be applied directly or reset");
            vm.ResetDraftCommand.Execute(null);
            Check(!vm.HasDraft && !vm.CanApply && vm.DraftName == vm.PendingHostName, "Reset draft to current value");
            vm.DraftName = "new-host";
            vm.RequestAuthorizationAsync = _ => Task.FromResult(false);
            vm.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(stub.ApplyCalls == 0 && vm.HasDraft && vm.CanEdit && vm.CanApply, "Cancelled authorization preserves draft without writing");
            var preparedId = stub.PlanId;
            Check(preparedId != Guid.Empty, "Cancelled authorization retained the prepared plan");
            vm.RequestAuthorizationAsync = _ => Task.FromResult(true);
            var applying = vm.ApplyCommand.ExecuteAsync(null);
            Check(stub.PlanId == preparedId && stub.ApplyCalls == 1, "Retry uses the prepared plan");
            Check(vm.IsBusy && !vm.CanEdit && !vm.CanReload && !vm.IsCompleted, "Applying state");
            stub.Pending.SetResult(stub.Result(SettingsOperationState.Applied, "r2"));
            applying.GetAwaiter().GetResult();
            Check(vm.IsCompleted && vm.CanEdit && !vm.ShowQueryAction
                && vm.PendingHostName == "new-host" && vm.DraftName == "new-host", "Confirmed completion");
            Check(effective == SettingsEffectiveState.HostRestart
                ? vm.CurrentHostName == "old-host" && vm.RestartPending
                : vm.CurrentHostName == "new-host" && !vm.RestartPending, "Live and staged names");
            vm.DraftName = "next-host";
            vm.ResetDraftCommand.Execute(null);
            Check(!vm.HasDraft && vm.CanEdit, "Reset preserves completed operation");
            vm.DraftName = "next-host";
            stub.Pending = new();
            applying = vm.ApplyCommand.ExecuteAsync(null);
            Check(stub.Request!.ExpectedRevision == "r2", "Manual change uses confirmed revision");
            stub.Pending.SetResult(stub.Result(SettingsOperationState.Unknown, null));
            applying.GetAwaiter().GetResult();
            Check(!vm.CanEdit && !vm.CanReload && !vm.IsCompleted && vm.ShowQueryAction, "Unknown outcome retains query and disables reload");
            Check(!vm.HasDraft && !vm.ResetDraftCommand.CanExecute(null), "Unknown submitted operation cannot be discarded as a draft");
            vm.QueryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.IsCompleted && vm.CanEdit && !vm.ShowQueryAction, "Query-confirmed completion");
            stub.FailPreview = true;
            string? reported = null;
            vm.RequestReportProblemAsync = message => { reported = message; return Task.CompletedTask; };
            vm.DraftName = "failed-host";
            Check(vm.HasDraft && vm.CanApply, "A confirmed operation leaves a new editable draft");
            vm.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(reported is not null && reported.Contains("host name preview failed")
                && vm.HasDraft && vm.CanApply && vm.CanEdit,
                "A failed request reports one prompt and keeps the draft");
            stub.FailPreview = false;
            Console.WriteLine($"PASS: Host name completion, revision reuse, manual changes, recovery and failure prompt ({effective}).");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

public class IdentityCompletionServiceStub : DispatchProxy
{
    private readonly HostSettingsConnection _connection = new("test", Guid.NewGuid(), Guid.NewGuid());
    private readonly SettingsTarget _target = new("test", SettingsScope.HostMachine);
    private SettingsPlan? _plan;
    public SettingsEffectiveState Effective;
    public int MaximumLength => Effective == SettingsEffectiveState.HostRestart ? 15 : 63;
    public TaskCompletionSource<SettingsOperation> Pending = new();
    public HostnamePreviewRequest? Request;
    public int ApplyCalls;
    public bool FailPreview;
    public Guid RollbackId;
    public string? RollbackRevision;
    public Guid PlanId => _plan?.PlanId ?? Guid.Empty;
    public SettingsOperation Result(SettingsOperationState state, string? revision) =>
        new(_plan!.PlanId, "host.identity.hostname", _target, state, DateTimeOffset.UtcNow,
            revision, EffectiveState: Effective);

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "CaptureConnection": return _connection;
            case "IsCurrent": return true;
            case "ReadAsync": return Task.FromResult(new HostIdentitySnapshot(
                new("old-host", "old-host", MaximumLength, "r1", DateTimeOffset.UtcNow, "test"),
                _target, new(SettingsCapabilityState.Available), Effective));
            case "PreviewAsync":
                if (FailPreview) return Task.FromException<SettingsPlan>(new InvalidOperationException("host name preview failed"));
                Request = (HostnamePreviewRequest)args![1]!;
                _plan = new(Guid.NewGuid(), _target, Request.ExpectedRevision, DateTimeOffset.UtcNow.AddMinutes(5),
                    [new("host.identity.hostname", "old-host", Request.Change.HostName)], default, "test", Effective, "test-impact");
                return Task.FromResult(_plan);
            case "ApplyAsync": ApplyCalls++; return Pending.Task;
            case "GetOperationAsync": return Task.FromResult(Result(SettingsOperationState.Applied, "r4"));
            case "RollbackAsync":
                RollbackId = (Guid)args![1]!;
                RollbackRevision = (string)args![2]!;
                return Task.FromResult(new SettingsOperation(RollbackId, "host.identity.hostname", _target,
                    SettingsOperationState.RolledBack, DateTimeOffset.UtcNow, "r3", EffectiveState: Effective));
            default: throw new NotSupportedException(method.Name);
        }
    }
}
