using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Firewall;
using RelaxKonOS.Protocol.Firewall;

internal static class FirewallSubmissionChecks
{
    public static void Run()
    {
        var client = DispatchProxy.Create<IRemoteFirewallClient, FirewallSubmissionStub>();
        var stub = (FirewallSubmissionStub)(object)client;
        var vm = new FirewallViewModel(client, new FirewallTestPermissions()) { IsAvailable = true };
        vm.SelectedRule = new(7, "allow", "in", "tcp", "any", "any", "80");
        vm.Port = "8080";
        var updating = vm.UpdateRuleAsync();
        Check(vm.IsLoading && stub.Number == 7 && stub.Request!.Port == "8080", "Rule update freezes the selected target and draft.");
        Check(!vm.UpdateRuleAsync().GetAwaiter().GetResult() && !vm.AddRuleAsync().GetAwaiter().GetResult()
            && stub.Calls == 1, "Direct repeated rule writes are blocked while busy.");
        stub.Pending.SetResult(new(false, "test.refused"));
        Check(!updating.GetAwaiter().GetResult() && !vm.IsLoading && vm.Port == "8080", "Refused update preserves editable input.");
        vm.IsAvailable = false;
        Check(!vm.UpdateRuleAsync().GetAwaiter().GetResult() && stub.Calls == 1, "Unavailable facts do not permit direct writes.");
        Console.WriteLine("Firewall submission checks passed.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public class FirewallSubmissionStub : DispatchProxy
{
    public TaskCompletionSource<FirewallOperationResult> Pending = new();
    public int Number;
    public int Calls;
    public UpdateFirewallRuleRequest? Request;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name != "UpdateRuleAsync") throw new InvalidOperationException(method.Name);
        Number = (int)args![0]!;
        Request = (UpdateFirewallRuleRequest)args[1]!;
        Calls++;
        return Pending.Task;
    }
}

internal sealed class FirewallTestPermissions : IAppPermissionScope
{
    public AppPermissionStatus GetStatus(string permissionId) => AppPermissionStatus.Granted;
    public bool IsGranted(string permissionId) => true;
    public Task<AppPermissionStatus> RequestAsync(string permissionId, CancellationToken cancellationToken = default)
        => Task.FromResult(AppPermissionStatus.Granted);
    public Task OpenSettingsAsync() => Task.CompletedTask;
}
