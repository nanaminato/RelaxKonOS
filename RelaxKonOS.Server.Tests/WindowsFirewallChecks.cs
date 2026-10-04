using System.Text.Json;
using RelaxKonOS.Protocol.Firewall;

internal static class WindowsFirewallChecks
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static async Task RunAsync()
    {
        var transport = new Transport();
        var service = new WindowsFirewallService(transport);
        foreach (var request in new[] {
            new CreateFirewallRuleRequest("limit", "in", "tcp", "any", "any", "443"),
            new("allow", "in", "tcp", "any;reboot", "any", "443"),
            new("allow", "in", "any", "any", "any", "443"),
            new("allow", "in", "tcp", "10.0.0.1/33", "any", "443") })
            TestAssert.Assert(!(await service.CreateRuleAsync(request, default)).Success, "Invalid Windows rule was accepted.");
        TestAssert.Assert(transport.Calls == 0, "Invalid rule reached the Helper.");
        TestAssert.Assert(!(await service.SetDefaultsAsync("reject", "allow", default)).Success && transport.Calls == 0, "Unsupported policy reached the Helper.");
        var valid = await service.CreateRuleAsync(new("allow", "out", "udp", "any", "2001:db8::/64", "1000:2000"), default);
        TestAssert.Assert(valid.Success && transport.Last?.Operation == PrivilegedOperationKind.FirewallWindowsCreateRule, "Windows rule was not routed to the dedicated operation.");
        TestAssert.Assert(transport.Last is not null && RelaxKonOS.PrivilegedHelper.WindowsFirewallOperations.ValidRequest(transport.Last), "Server and Helper validation disagree.");
        TestAssert.Assert(!RelaxKonOS.PrivilegedHelper.WindowsFirewallOperations.ValidRequest(transport.Last! with { Path = "C:\\Windows" }), "Helper accepted unrelated privileged fields.");
        transport.Result = new(false, ProblemCode: PrivilegedProblemCode.AccessDenied);
        TestAssert.Assert(!(await service.GetStatusAsync(default)).IsAvailable, "Denied read was reported as available.");
        var failed = false;
        try { await service.ListRulesAsync(default); } catch (FirewallRulesUnavailableException) { failed = true; }
        TestAssert.Assert(failed, "Denied Windows rule read was reported as an empty list.");
        if (OperatingSystem.IsWindows())
        {
            VerifyMutations();
            var status = RelaxKonOS.PrivilegedHelper.WindowsFirewallOperations.Execute(new(PrivilegedOperationKind.FirewallWindowsStatus));
            TestAssert.Assert(status.Success, "Native Windows firewall status read failed: " + status.ProblemCode);
            var dto = JsonSerializer.Deserialize<FirewallStatusDto>(Convert.FromBase64String(status.OutputBase64!));
            TestAssert.Assert(dto is { IsAvailable: true, Backend: "windows-defender" }, "Native Windows firewall status is invalid.");
            var rules = RelaxKonOS.PrivilegedHelper.WindowsFirewallOperations.Execute(new(PrivilegedOperationKind.FirewallWindowsRules));
            TestAssert.Assert(rules.Success, "Native Windows firewall rule enumeration failed: " + rules.ProblemCode);
        }
        Console.WriteLine("Windows firewall validation, routing, failed-read and native read checks passed.");
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VerifyMutations()
    {
        var policy = new FakePolicy();
        PrivilegedOperationResult Apply(PrivilegedOperationRequest request) => RelaxKonOS.PrivilegedHelper.WindowsFirewallOperations.Apply(policy, request, () => new FakeRule());
        TestAssert.Assert(Apply(new(PrivilegedOperationKind.FirewallWindowsSetEnabled, FirewallEnabled: true)).Success
            && policy.FirewallEnabled.Values.All(x => x), "Profile enable did not affect all profiles.");
        TestAssert.Assert(Apply(new(PrivilegedOperationKind.FirewallWindowsSetDefaults, FirewallIncomingPolicy: FirewallDefaultPolicy.Deny,
            FirewallOutgoingPolicy: FirewallDefaultPolicy.Allow)).Success && policy.DefaultInboundAction.Values.All(x => x == 0), "Default policies were mapped incorrectly.");
        var create = new PrivilegedOperationRequest(PrivilegedOperationKind.FirewallWindowsCreateRule,
            FirewallRuleAction: FirewallRuleAction.Allow, FirewallRuleDirection: FirewallRuleDirection.Out,
            FirewallRuleProtocol: FirewallRuleProtocol.Tcp, FirewallSource: "10.0.0.0/24", FirewallDestination: "any", FirewallPort: "443");
        TestAssert.Assert(Apply(create).Success, "Rule creation failed.");
        var rule = policy.Rules.Items.Single();
        TestAssert.Assert(rule.LocalAddresses == "10.0.0.0/24" && rule.RemoteAddresses == "*" && rule.RemotePorts == "443"
            && rule.Direction == 2 && rule.Profiles == 7 && rule.Enabled, "Outbound address/port mapping is incorrect.");
        var number = int.Parse(rule.Name.Split('.').Last());
        var replace = create with { Operation = PrivilegedOperationKind.FirewallWindowsReplaceRule, FirewallRuleNumber = number,
            FirewallRuleDirection = FirewallRuleDirection.In, FirewallSource = "any", FirewallDestination = "10.0.0.1", FirewallPort = "100:200" };
        TestAssert.Assert(Apply(replace).Success && policy.Rules.Items.Single().LocalPorts == "100-200", "Inbound port range mapping failed.");
        var baseline = policy.Rules.Items.Single();
        policy.Rules.FailNextAdd = true;
        try { Apply(replace); } catch (InvalidOperationException) { }
        TestAssert.Assert(policy.Rules.Items.Single() == baseline, "Failed replacement lost the original rule.");
        policy.LocalPolicyModifyState = 1;
        TestAssert.Assert(!Apply(new(PrivilegedOperationKind.FirewallWindowsDeleteRule, FirewallRuleNumber: number)).Success
            && policy.Rules.Items.Count == 1, "Group Policy denial changed a rule.");
        policy.LocalPolicyModifyState = 0;
        baseline.Grouping = "Other app";
        TestAssert.Assert(!Apply(new(PrivilegedOperationKind.FirewallWindowsDeleteRule, FirewallRuleNumber: number)).Success
            && policy.Rules.Items.Count == 1, "Non-owned rule was deleted.");
        baseline.Grouping = "RelaxKonOS Firewall";

        TestAssert.Assert(Apply(new(PrivilegedOperationKind.FirewallWindowsDeleteRule, FirewallRuleNumber: number)).Success && policy.Rules.Items.Count == 0, "Rule deletion failed.");
    }
    private sealed class Transport : IPrivilegedOperationTransport
    {
        internal int Calls;
        internal PrivilegedOperationRequest? Last;
        internal PrivilegedOperationResult Result = new(true);
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        { Calls++; Last = request; return Task.FromResult(Result); }
    }
}

// Public because the dynamic COM adapter's runtime binder accesses these members.
public sealed class FakePolicy
{
    public Dictionary<int, bool> FirewallEnabled { get; } = new() { [1] = false, [2] = false, [4] = false };
    public Dictionary<int, int> DefaultInboundAction { get; } = new() { [1] = 1, [2] = 1, [4] = 1 };
    public Dictionary<int, int> DefaultOutboundAction { get; } = new() { [1] = 1, [2] = 1, [4] = 1 };
    public int LocalPolicyModifyState { get; set; }
    public FakeRules Rules { get; } = new();
}
public sealed class FakeRules : System.Collections.IEnumerable
{
    public List<FakeRule> Items { get; } = [];
    public bool FailNextAdd { get; set; }
    public object Item(string name) => Items.FirstOrDefault(x => x.Name == name)
        ?? throw new System.Runtime.InteropServices.COMException("Missing", unchecked((int)0x80070002));
    public void Add(object rule) { if (FailNextAdd) { FailNextAdd = false; throw new InvalidOperationException(); } Items.Add((FakeRule)rule); }
    public void Remove(string name) => Items.RemoveAll(x => x.Name == name);
    public System.Collections.IEnumerator GetEnumerator() => Items.GetEnumerator();
}
public sealed class FakeRule
{
    public string Name { get; set; } = "";
    public string Grouping { get; set; } = "";
    public string Description { get; set; } = "";
    public int Direction { get; set; }
    public int Protocol { get; set; }
    public int Profiles { get; set; }
    public int Action { get; set; }
    public bool Enabled { get; set; }
    public string LocalAddresses { get; set; } = "*";
    public string RemoteAddresses { get; set; } = "*";
    public string LocalPorts { get; set; } = "*";
    public string RemotePorts { get; set; } = "*";
}
