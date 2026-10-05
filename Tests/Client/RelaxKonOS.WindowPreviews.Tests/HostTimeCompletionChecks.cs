using System.Reflection;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Settings;

internal static class HostTimeCompletionChecks
{
    public static void Run(LocalizationService localization)
    {
        var service = DispatchProxy.Create<IHostTimeService, TimeCompletionServiceStub>();
        var stub = (TimeCompletionServiceStub)service;
        using var vm = new HostTimeEditorViewModel(service,
            DispatchProxy.Create<IAuthSession, LanguageSessionStub>(), localization);
        vm.RequestAuthorizationAsync = _ => Task.FromResult(true);
        vm.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        vm.SelectedZone = "not-in-remote-catalog";
        Check(!vm.CanApply, "Only remote catalog values can be applied");
        vm.SelectedZone = "UTC";
        Check(vm.CanApply && vm.HasDraft && vm.ResetDraftCommand.CanExecute(null), "Changed value can be applied directly or reset");
        vm.ResetDraftCommand.Execute(null);
        Check(!vm.HasDraft && !vm.CanApply && vm.SelectedZone == vm.CurrentZone, "Reset draft to current value");
        vm.SelectedZone = "UTC";
        vm.RequestAuthorizationAsync = _ => Task.FromResult(false);
        vm.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(stub.ApplyCalls == 0 && vm.HasDraft && vm.CanEdit && vm.CanApply, "Cancelled authorization preserves draft without writing");
        var preparedId = vm.OperationId;
        vm.RequestAuthorizationAsync = _ => Task.FromResult(true);
        var applying = vm.ApplyCommand.ExecuteAsync(null);
        var completedPlanId = vm.OperationId;
        Check(preparedId == completedPlanId && stub.ApplyCalls == 1, "Retry uses the prepared plan");
        Check(vm.IsBusy && !vm.CanEdit && !vm.CanReload && !vm.IsCompleted, "Apply progress cannot be discarded by reloading");
        stub.Pending.SetResult(stub.Result(SettingsOperationState.Applied, "r2"));
        applying.GetAwaiter().GetResult();
        Check(vm.IsCompleted && vm.CanEdit && !vm.ShowQueryAction
            && vm.ShowRollbackAction && vm.SelectedZone == "UTC", "Confirmed completion");
        vm.SelectedZone = "Asia/Shanghai";
        vm.ResetDraftCommand.Execute(null);
        Check(!vm.HasDraft && vm.CanRollback, "Reset preserves completed operation rollback");
        vm.SelectedZone = "Asia/Shanghai";
        vm.RollbackCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(stub.RollbackId.ToString("D") == completedPlanId && stub.RollbackRevision == "r2" && vm.IsCompleted && vm.CanEdit
            && !vm.ShowRollbackAction && vm.CurrentZone == "Asia/Shanghai", "Restore completed plan");
        vm.SelectedZone = "UTC";
        stub.Pending = new();
        applying = vm.ApplyCommand.ExecuteAsync(null);
        Check(stub.Request!.ExpectedRevision == "r3", "Direct apply after rollback uses confirmed revision");
        stub.Pending.SetResult(stub.Result(SettingsOperationState.Unknown, null));
        applying.GetAwaiter().GetResult();
        Check(!vm.CanEdit && !vm.CanReload && !vm.IsCompleted && vm.ShowQueryAction && vm.HasOperation, "Unknown outcome locks edits and reload");
        Check(!vm.HasDraft && !vm.ResetDraftCommand.CanExecute(null), "Unknown submitted operation cannot be discarded as a draft");
        vm.QueryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(vm.CanEdit && vm.IsCompleted && !vm.ShowQueryAction, "Query-confirmed completion");
        Console.WriteLine("PASS: Time zone completion, revision reuse, rollback and unknown-outcome recovery.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

public class TimeCompletionServiceStub : DispatchProxy
{
    private readonly HostSettingsConnection _connection = new("test", Guid.NewGuid(), Guid.NewGuid());
    private readonly SettingsTarget _target = new("test", SettingsScope.HostMachine);
    private SettingsPlan? _plan;
    public TaskCompletionSource<SettingsOperation> Pending = new();
    public TimeZonePreviewRequest? Request;
    public int ApplyCalls;
    public string? RollbackRevision;
    public Guid RollbackId;
    public SettingsOperation Result(SettingsOperationState state, string? revision) =>
        new(_plan!.PlanId, "host/time", _target, state, DateTimeOffset.UtcNow, revision);

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "CaptureConnection": return _connection;
            case "IsCurrent": return true;
            case "ReadAsync": return Task.FromResult(new HostTimeSnapshot(
                new("Asia/Shanghai", ["Asia/Shanghai", "UTC"], "r1", DateTimeOffset.UtcNow, "test"),
                _target, new(SettingsCapabilityState.Available)));
            case "PreviewAsync":
                Request = (TimeZonePreviewRequest)args![1]!;
                _plan = new(Guid.NewGuid(), _target, Request.ExpectedRevision, DateTimeOffset.UtcNow.AddMinutes(5),
                    [new("host/time", "Asia/Shanghai", Request.Change.TimeZoneId)],
                    default, "test", SettingsEffectiveState.Immediate, "test-impact");
                return Task.FromResult(_plan);
            case "ApplyAsync": ApplyCalls++; return Pending.Task;
            case "GetOperationAsync": return Task.FromResult(Result(SettingsOperationState.Applied, "r4"));
            case "RollbackAsync":
                RollbackId = (Guid)args![1]!;
                RollbackRevision = (string)args![2]!;
                return Task.FromResult(new SettingsOperation(RollbackId, "host/time", _target,
                    SettingsOperationState.RolledBack, DateTimeOffset.UtcNow, "r3"));
            default: throw new NotSupportedException(method.Name);
        }
    }
}
