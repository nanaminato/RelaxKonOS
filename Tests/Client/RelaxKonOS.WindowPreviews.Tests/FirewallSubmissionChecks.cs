using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Firewall;
using RelaxKonOS.Protocol.Firewall;

internal static class FirewallSubmissionChecks
{
    public static void Run()
    {
        UpdateFreezesItsTarget();
        ConfirmationGates();
        DefaultPolicies();
        DraftValidation();
        Console.WriteLine("Firewall submission checks passed.");
    }

    /// <summary>A write freezes the rule number and draft it was launched with, and a second write
    /// cannot start while the first is in flight.</summary>
    private static void UpdateFreezesItsTarget()
    {
        var host = Host();
        var vm = new FirewallViewModel(host.Client, new FirewallTestPermissions()) { IsAvailable = true };
        vm.SelectedRule = new(7, "allow", "in", "tcp", "any", "any", "80");
        vm.Port = "8080";
        host.Stub.HoldUpdates = true;
        var updating = vm.UpdateRuleAsync();
        Check(vm.IsLoading && host.Stub.Number == 7 && host.Stub.Request!.Port == "8080",
            "Rule update freezes the selected target and draft.");
        Check(!vm.UpdateRuleAsync().GetAwaiter().GetResult() && !vm.AddRuleAsync().GetAwaiter().GetResult()
            && host.Stub.UpdateCalls == 1, "Direct repeated rule writes are blocked while busy.");
        host.Stub.Pending.SetResult(new(false, "firewall.invalid_rule"));
        Pump(updating);
        Check(updating.IsCompleted && !updating.Result && !vm.IsLoading && vm.Port == "8080",
            "Refused update preserves editable input.");
        Check(vm.HasEditorMessage && !vm.EditorMessage.Resolve().Contains("firewall.", StringComparison.Ordinal),
            "A refused rule names the cause in the dialog instead of showing the host problem code.");
        vm.IsAvailable = false;
        Check(!vm.UpdateRuleAsync().GetAwaiter().GetResult() && host.Stub.UpdateCalls == 1,
            "Unavailable facts do not permit direct writes.");
    }

    /// <summary>Every host change asks first, and a refused confirmation leaves the host untouched.</summary>
    private static void ConfirmationGates()
    {
        var host = Host();
        var vm = new FirewallViewModel(host.Client, new FirewallTestPermissions());
        vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(!vm.DeleteRuleCommand.CanExecute(null), "Nothing can be deleted before a rule is selected.");
        vm.SelectedRow = vm.Rules[0];
        var prompts = new List<string>();
        vm.ConfirmAsync = (title, _) =>
        {
            prompts.Add(title);
            return Task.FromResult(false);
        };
        vm.DeleteRuleCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(host.Stub.DeleteCalls == 0 && prompts.Count == 1,
            "Deleting a rule asks first, and refusing the question leaves the host untouched.");
        Check(host.Stub.EnabledCalls == 0, "Neither action ran before its confirmation.");
        vm.DisableCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(host.Stub.EnabledCalls == 0 && prompts.Count == 2, "Stopping the firewall asks first as well.");
        vm.ConfirmAsync = (title, _) =>
        {
            prompts.Add(title);
            return Task.FromResult(true);
        };
        vm.DeleteRuleCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(host.Stub.DeleteCalls == 1 && host.Stub.DeletedNumber == 1,
            "A confirmed delete reaches the host with the selected rule number.");
        Check(vm.Rules.Count == 2 && vm.SelectedRule is null,
            "The rule list and selection are re-read from the host after the delete.");
    }

    /// <summary>Default policies are a draft: nothing is written until a policy differs and the user saves.</summary>
    private static void DefaultPolicies()
    {
        var host = Host();
        var vm = new FirewallViewModel(host.Client, new FirewallTestPermissions());
        vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(!vm.HasDefaultChanges && !vm.SaveDefaultsCommand.CanExecute(null),
            "Saving is offered only when a policy differs from the host.");
        vm.SelectedIncomingPolicy = vm.Policies.First(option => option.Value == "allow");
        Check(vm.HasDefaultChanges && vm.SaveDefaultsCommand.CanExecute(null) && !vm.ShowLockoutWarning,
            "A changed policy becomes one pending change.");
        vm.SelectedIncomingPolicy = vm.Policies.First(option => option.Value == "deny");
        vm.SelectedOutgoingPolicy = vm.Policies.First(option => option.Value == "deny");
        Check(vm.ShowLockoutWarning, "A deny default warns before it is saved.");
        vm.ConfirmAsync = (_, _) => Task.FromResult(true);
        vm.SaveDefaultsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(host.Stub.DefaultsCalls == 1 && host.Stub.Defaults!.OutgoingPolicy == "deny",
            "Saving sends the selected default policies once confirmed.");
        Check(!vm.HasDefaultChanges, "After the host accepts the policies the editor reads the host values back.");
        vm.ConfirmAsync = (_, _) => Task.FromResult(false);
        vm.SelectedOutgoingPolicy = vm.Policies.First(option => option.Value == "allow");
        vm.SaveDefaultsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(host.Stub.DefaultsCalls == 1 && vm.HasDefaultChanges,
            "Refusing the confirmation keeps the pending policy instead of writing it.");
    }

    /// <summary>Field-level validation stops a bad draft before it becomes a host request.</summary>
    private static void DraftValidation()
    {
        var host = Host();
        var vm = new FirewallViewModel(host.Client, new FirewallTestPermissions()) { IsAvailable = true };
        vm.Port = "99999";
        Check(!vm.IsDraftValid && vm.PortProblem is not null && !vm.CanSubmitDraft,
            "An out-of-range port blocks submit with a field message.");
        Check(!vm.AddRuleAsync().GetAwaiter().GetResult() && host.Stub.CreateCalls == 0,
            "An invalid draft never reaches the host.");
        vm.Port = string.Empty;
        vm.SelectedProtocol = vm.Protocols.First(option => option.Value == "any");
        vm.Port = "443";
        Check(vm.PortProblem is not null, "The any protocol refuses a specific port before the host does.");
        vm.SelectedProtocol = vm.Protocols.First(option => option.Value == "tcp");
        vm.Port = "8000:8080";
        vm.Source = "192.0.2.0/24";
        Check(vm.IsDraftValid && vm.CanSubmitDraft, "A valid draft becomes submittable.");
        Check(vm.DraftSummary.Contains("8000:8080") && vm.DraftSummary.Contains("192.0.2.0/24"),
            "The preview reads the draft back with the entered port and source.");
        Check(vm.AddRuleAsync().GetAwaiter().GetResult() && host.Stub.Created!.Port == "8000:8080"
            && host.Stub.Created.Source == "192.0.2.0/24", "A valid draft is submitted unchanged.");
        Check(vm.Rules.Count == 4 && vm.Source.Length == 0 && vm.Port.Length == 0,
            "A successful add clears the draft and reloads the host rules.");
        vm.RuleFilter = "22";
        Check(vm.Rules.Count == 1 && vm.HasFilter && vm.Rules[0].Number == 1,
            "The filter narrows the table without touching the host.");
        Check(vm.Rules[0].SourceLabel != vm.Rules[0].Rule.Source && vm.Rules[0].PortLabel != "any",
            "Table cells show localized labels instead of raw host tokens.");
        vm.ClearFilterCommand.Execute(null);
        Check(vm.Rules.Count == 4 && !vm.HasFilter, "Clearing the filter restores every host rule.");
    }

    private static (IRemoteFirewallClient Client, FirewallHostStub Stub) Host()
    {
        var client = DispatchProxy.Create<IRemoteFirewallClient, FirewallHostStub>();
        return (client, (FirewallHostStub)(object)client);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    /// <summary>Drains the UI queue until the awaited write finishes, instead of blocking the UI thread.</summary>
    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        if (!task.IsCompleted) throw new TimeoutException("Firewall write did not complete.");
    }
}

/// <summary>A host stub that answers every firewall call, so the checks can tell which requests were
/// actually sent and what the host state looked like afterwards.</summary>
public class FirewallHostStub : DispatchProxy
{
    public FirewallStatusDto Status = new(true, true, "ufw", "0.36.2", "deny", "allow");
    public List<FirewallRuleDto> Rules =
    [
        new(1, "allow", "in", "tcp", "any", "any", "22") { AddressFamily = "IPv4 + IPv6" },
        new(3, "deny", "in", "tcp", "192.0.2.0/24", "any", "8080"),
        new(6, "limit", "in", "tcp", "any", "any", "443"),
    ];
    public bool HoldUpdates;
    public TaskCompletionSource<FirewallOperationResult> Pending = new();
    public int Number;
    public int UpdateCalls;
    public int CreateCalls;
    public int DeleteCalls;
    public int EnabledCalls;
    public int DefaultsCalls;
    public int? DeletedNumber;
    public bool? Enabled;
    public UpdateFirewallRuleRequest? Request;
    public CreateFirewallRuleRequest? Created;
    public UpdateFirewallDefaultsRequest? Defaults;

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "GetStatusAsync" => Task.FromResult(Status),
        "ListRulesAsync" => Task.FromResult<IReadOnlyList<FirewallRuleDto>>([.. Rules]),
        "SetEnabledAsync" => SetEnabled((UpdateFirewallEnabledRequest)args![0]!),
        "SetDefaultsAsync" => SetDefaults((UpdateFirewallDefaultsRequest)args![0]!),
        "CreateRuleAsync" => Create((CreateFirewallRuleRequest)args![0]!),
        "UpdateRuleAsync" => Update(args!),
        "DeleteRuleAsync" => Delete((int)args![0]!),
        _ => throw new InvalidOperationException(method.Name),
    };

    private Task<FirewallOperationResult> SetEnabled(UpdateFirewallEnabledRequest request)
    {
        EnabledCalls++;
        Enabled = request.Enabled;
        Status = Status with { IsEnabled = request.Enabled };
        return Task.FromResult(new FirewallOperationResult(true));
    }

    private Task<FirewallOperationResult> SetDefaults(UpdateFirewallDefaultsRequest request)
    {
        DefaultsCalls++;
        Defaults = request;
        Status = Status with { DefaultIncomingPolicy = request.IncomingPolicy, DefaultOutgoingPolicy = request.OutgoingPolicy };
        return Task.FromResult(new FirewallOperationResult(true));
    }

    private Task<FirewallOperationResult> Create(CreateFirewallRuleRequest request)
    {
        CreateCalls++;
        Created = request;
        Rules.Add(new FirewallRuleDto(Rules.Max(rule => rule.Number) + 1, request.Action, request.Direction,
            request.Protocol, request.Source, request.Destination, request.Port));
        return Task.FromResult(new FirewallOperationResult(true));
    }

    private Task<FirewallOperationResult> Update(object?[] args)
    {
        UpdateCalls++;
        Number = (int)args[0]!;
        Request = (UpdateFirewallRuleRequest)args[1]!;
        return HoldUpdates ? Pending.Task : Task.FromResult(new FirewallOperationResult(true));
    }

    private Task<FirewallOperationResult> Delete(int number)
    {
        DeleteCalls++;
        DeletedNumber = number;
        Rules.RemoveAll(rule => rule.Number == number);
        return Task.FromResult(new FirewallOperationResult(true));
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
