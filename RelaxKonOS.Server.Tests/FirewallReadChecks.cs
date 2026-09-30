using RelaxKonOS.Protocol.Firewall;

internal static class FirewallReadChecks
{
    internal static async Task RunAsync()
    {
        var transport = new Transport();
        var firewall = new LinuxUfwFirewallService(transport, NullLogger<LinuxUfwFirewallService>.Instance);
        var unavailable = false;
        try { await firewall.ListRulesAsync(CancellationToken.None); }
        catch (FirewallRulesUnavailableException error) { unavailable = error.ProblemCode == "firewall.privileged_proxy_required"; }
        TestAssert.Assert(unavailable, "Failed UFW status was reported as an authoritative empty list.");
        transport.Result = new(true, OutputBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("Status: active\n[ 1] 443/tcp ALLOW IN Anywhere\n[ 2] 443/tcp (v6) ALLOW IN Anywhere (v6)\n")));
        var rules = await firewall.ListRulesAsync(CancellationToken.None);
        TestAssert.Assert(rules.Count == 1 && rules[0].Number == 1 && rules[0].AddressFamily == "IPv4 + IPv6", "Logical paired rule projection changed.");
        transport.Result = new(true, OutputBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("Status: inactive\n")));
        TestAssert.Assert((await firewall.ListRulesAsync(CancellationToken.None)).Count == 0, "A successful empty status should remain a verified empty list.");
        var before = transport.Calls;
        var invalid = await firewall.CreateRuleAsync(new("allow", "in", "tcp", "any;reboot", "any", "443", null), CancellationToken.None);
        TestAssert.Assert(!invalid.Success && transport.Calls == before, "Invalid structured firewall rule reached the helper.");
        Console.WriteLine("Firewall failed-read, paired-rule and structured validation checks passed.");
    }
    private sealed class Transport : IPrivilegedOperationTransport
    {
        internal int Calls;
        internal PrivilegedOperationResult Result = new(false, ProblemCode: PrivilegedProblemCode.AccessDenied);
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
}
