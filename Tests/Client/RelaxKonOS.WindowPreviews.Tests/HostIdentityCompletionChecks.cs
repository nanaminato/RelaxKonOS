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
            Check(stub.MaximumLength == 15 ? vm.HasNameProblem && !vm.CanPreview
                && vm.NameProblem.Contains("16") && vm.NameProblem.Contains("15")
                : !vm.HasNameProblem && vm.CanPreview, "Remote platform length and specific feedback");
            vm.DraftName = "WIN-3QLG75ESVRU";
            Check(!vm.HasNameProblem && vm.CanPreview, "Windows 15-character boundary");
            vm.DraftName = "new-host";
            vm.PreviewCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var completedId = vm.OperationId;
            var applying = vm.ApplyCommand.ExecuteAsync(null);
            Check(vm.IsBusy && !vm.CanEdit && !vm.HasPreview && !vm.IsCompleted, "Applying state");
            stub.Pending.SetResult(stub.Result(SettingsOperationState.Applied, "r2"));
            applying.GetAwaiter().GetResult();
            Check(vm.IsCompleted && vm.CanEdit && !vm.HasPreview && !vm.ShowQueryAction
                && vm.CanRollback && vm.PendingHostName == "new-host" && vm.DraftName == "new-host", "Confirmed completion");
            Check(effective == SettingsEffectiveState.HostRestart
                ? vm.CurrentHostName == "old-host" && vm.RestartPending
                : vm.CurrentHostName == "new-host" && !vm.RestartPending, "Live and staged names");
            vm.DraftName = "next-host";
            vm.PreviewCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(stub.Request!.ExpectedRevision == "r2", "Confirmed revision reused");
            vm.RollbackCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(stub.RollbackId.ToString("D") == completedId && stub.RollbackRevision == "r2"
                && vm.IsCompleted && vm.CanEdit && !vm.CanRollback && !vm.HasPreview
                && vm.CurrentHostName == "old-host" && vm.PendingHostName == "old-host" && !vm.RestartPending,
                "Restore original plan and names");
            vm.DraftName = "new-host";
            vm.PreviewCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(stub.Request!.ExpectedRevision == "r3", "Rollback revision reused");
            stub.Pending = new();
            applying = vm.ApplyCommand.ExecuteAsync(null);
            stub.Pending.SetResult(stub.Result(SettingsOperationState.Unknown, null));
            applying.GetAwaiter().GetResult();
            Check(!vm.CanEdit && !vm.IsCompleted && vm.ShowQueryAction && vm.HasOperation, "Unknown outcome retains query");
            vm.QueryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.IsCompleted && vm.CanEdit && !vm.ShowQueryAction, "Query-confirmed completion");
            Console.WriteLine($"PASS: Host name completion, revision reuse, rollback and recovery ({effective}).");
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
    public Guid RollbackId;
    public string? RollbackRevision;
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
                Request = (HostnamePreviewRequest)args![1]!;
                _plan = new(Guid.NewGuid(), _target, Request.ExpectedRevision, DateTimeOffset.UtcNow.AddMinutes(5),
                    [new("host.identity.hostname", "old-host", Request.Change.HostName)], default, "test", Effective, "test-impact");
                return Task.FromResult(_plan);
            case "ApplyAsync": return Pending.Task;
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
