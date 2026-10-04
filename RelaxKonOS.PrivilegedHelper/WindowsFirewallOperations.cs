using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using RelaxKonOS.Protocol.Firewall;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Fixed Windows Firewall COM operations; only this application's rule group is editable.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFirewallOperations
{
    private const string Prefix = "RelaxKonOS.Firewall.";
    private const string Group = "RelaxKonOS Firewall";
    private static readonly int[] Profiles = [1, 2, 4];

    public static PrivilegedOperationResult Execute(PrivilegedOperationRequest request)
    {
        if (!ValidRequest(request)) return Failure(PrivilegedProblemCode.InvalidRequest);
        using var mutex = new Mutex(false, @"Global\RelaxKonOS.Firewall");
        var held = false;
        object? policyObject = null;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return Failure(PrivilegedProblemCode.TimedOut);
            policyObject = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!);
            return Apply(policyObject!, request, () => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!);
        }
        catch (UnauthorizedAccessException) { return Failure(PrivilegedProblemCode.AccessDenied); }
        catch (COMException error) { return Failure(error.HResult == unchecked((int)0x80070005) ? PrivilegedProblemCode.AccessDenied : PrivilegedProblemCode.InternalError); }
        catch { return Failure(PrivilegedProblemCode.InternalError); }
        finally { Release(policyObject); if (held) mutex.ReleaseMutex(); }
    }

    internal static PrivilegedOperationResult Apply(object policyObject, PrivilegedOperationRequest request, Func<object> createRule)
    {
        if (!ValidRequest(request)) return Failure(PrivilegedProblemCode.InvalidRequest);
        dynamic policy = policyObject;
        if (request.Operation == PrivilegedOperationKind.FirewallWindowsStatus)
        {
            var incoming = Profiles.Select(p => (int)policy.DefaultInboundAction[p]).Distinct().ToArray();
            var outgoing = Profiles.Select(p => (int)policy.DefaultOutboundAction[p]).Distinct().ToArray();
            return Output(new FirewallStatusDto(true, Profiles.All(p => (bool)policy.FirewallEnabled[p]), "windows-defender", null,
                incoming.Length == 1 ? PolicyName(incoming[0]) : null, outgoing.Length == 1 ? PolicyName(outgoing[0]) : null));
        }
        if (request.Operation == PrivilegedOperationKind.FirewallWindowsRules) return Output(ReadRules((object)policy));
        if ((int)policy.LocalPolicyModifyState != 0) return Failure(PrivilegedProblemCode.AccessDenied);
        if (request.Operation == PrivilegedOperationKind.FirewallWindowsSetEnabled)
        {
            var before = Profiles.Select(p => (bool)policy.FirewallEnabled[p]).ToArray();
            try { foreach (var profile in Profiles) policy.FirewallEnabled[profile] = request.FirewallEnabled!.Value; }
            catch { for (var i = 0; i < Profiles.Length; i++) policy.FirewallEnabled[Profiles[i]] = before[i]; throw; }
            return Profiles.All(p => (bool)policy.FirewallEnabled[p] == request.FirewallEnabled!.Value)
                ? new(true) : Failure(PrivilegedProblemCode.Conflict);
        }
        if (request.Operation == PrivilegedOperationKind.FirewallWindowsSetDefaults)
        {
            var incoming = Profiles.Select(p => (int)policy.DefaultInboundAction[p]).ToArray();
            var outgoing = Profiles.Select(p => (int)policy.DefaultOutboundAction[p]).ToArray();
            var nextIn = request.FirewallIncomingPolicy == FirewallDefaultPolicy.Allow ? 1 : 0;
            var nextOut = request.FirewallOutgoingPolicy == FirewallDefaultPolicy.Allow ? 1 : 0;
            try
            {
                foreach (var profile in Profiles) { policy.DefaultInboundAction[profile] = nextIn; policy.DefaultOutboundAction[profile] = nextOut; }
            }
            catch
            {
                for (var i = 0; i < Profiles.Length; i++) { policy.DefaultInboundAction[Profiles[i]] = incoming[i]; policy.DefaultOutboundAction[Profiles[i]] = outgoing[i]; }
                throw;
            }
            return Profiles.All(p => (int)policy.DefaultInboundAction[p] == nextIn && (int)policy.DefaultOutboundAction[p] == nextOut)
                ? new(true) : Failure(PrivilegedProblemCode.Conflict);
        }
        return ChangeRule((object)policy, request, createRule);
    }

    internal static bool ValidRequest(PrivilegedOperationRequest r)
    {
        var clean = new PrivilegedOperationRequest(r.Operation, OperationId: r.OperationId, Correlation: r.Correlation, Version: r.Version);
        clean = r.Operation switch
        {
            PrivilegedOperationKind.FirewallWindowsStatus or PrivilegedOperationKind.FirewallWindowsRules => clean,
            PrivilegedOperationKind.FirewallWindowsSetEnabled when r.FirewallEnabled is not null => clean with { FirewallEnabled = r.FirewallEnabled },
            PrivilegedOperationKind.FirewallWindowsSetDefaults when WindowsFirewallValidation.IsPolicy(r.FirewallIncomingPolicy) && WindowsFirewallValidation.IsPolicy(r.FirewallOutgoingPolicy)
                => clean with { FirewallIncomingPolicy = r.FirewallIncomingPolicy, FirewallOutgoingPolicy = r.FirewallOutgoingPolicy },
            PrivilegedOperationKind.FirewallWindowsDeleteRule when r.FirewallRuleNumber is > 0 and <= 10_000 => clean with { FirewallRuleNumber = r.FirewallRuleNumber },
            PrivilegedOperationKind.FirewallWindowsCreateRule or PrivilegedOperationKind.FirewallWindowsReplaceRule when WindowsFirewallValidation.IsRule(r)
                && (r.Operation == PrivilegedOperationKind.FirewallWindowsCreateRule ? r.FirewallRuleNumber is null : r.FirewallRuleNumber is > 0 and <= 10_000)
                => clean with { FirewallRuleNumber = r.FirewallRuleNumber, FirewallRuleAction = r.FirewallRuleAction, FirewallRuleDirection = r.FirewallRuleDirection,
                    FirewallRuleProtocol = r.FirewallRuleProtocol, FirewallSource = r.FirewallSource, FirewallDestination = r.FirewallDestination, FirewallPort = r.FirewallPort },
            _ => null,
        };
        return r == clean;
    }

    private static IReadOnlyList<FirewallRuleDto> ReadRules(object policyObject)
    {
        dynamic policy = policyObject;
        object rulesObject = policy.Rules;
        try
        {
            var result = new List<FirewallRuleDto>();
            foreach (object item in (IEnumerable)rulesObject)
            {
                try
                {
                    dynamic rule = item;
                    if (!Owned(item, out var number)) continue;
                    var inbound = (int)rule.Direction == 1;
                    var protocol = (int)rule.Protocol;
                    result.Add(new(number, (int)rule.Action == 1 ? "allow" : "deny", inbound ? "in" : "out",
                        protocol == 6 ? "tcp" : protocol == 17 ? "udp" : "any",
                        Address(inbound ? (string)rule.RemoteAddresses : (string)rule.LocalAddresses),
                        Address(inbound ? (string)rule.LocalAddresses : (string)rule.RemoteAddresses),
                        protocol == 256 ? "any" : Address(inbound ? (string)rule.LocalPorts : (string)rule.RemotePorts).Replace('-', ':'))
                        { AddressFamily = Family((string)rule.LocalAddresses, (string)rule.RemoteAddresses) });
                }
                finally { Release(item); }
            }
            return result.OrderBy(r => r.Number).ToArray();
        }
        finally { Release(rulesObject); }
    }

    private static PrivilegedOperationResult ChangeRule(object policyObject, PrivilegedOperationRequest request, Func<object> createRule)
    {
        dynamic policy = policyObject;
        object rulesObject = policy.Rules;
        object? oldObject = null;
        object? nextObject = null;
        try
        {
            dynamic rules = rulesObject;
            var number = request.FirewallRuleNumber ?? Random.Shared.Next(1, 10_001);
            var name = Prefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (request.Operation != PrivilegedOperationKind.FirewallWindowsCreateRule)
            {
                try { oldObject = rules.Item(name); }
                catch (COMException) { return Failure(PrivilegedProblemCode.NotFound); }
                if (!Owned(oldObject!, out var found) || found != number) return Failure(PrivilegedProblemCode.ResourceNotAllowed);
            }
            else
            {
                object? existing = null;
                try { existing = rules.Item(name); }
                catch (COMException error) when (error.HResult == unchecked((int)0x80070002)) { }
                if (existing is not null) { Release(existing); return Failure(PrivilegedProblemCode.Conflict); }
            }
            if (request.Operation == PrivilegedOperationKind.FirewallWindowsDeleteRule) { rules.Remove(name); return new(true); }
            nextObject = createRule();
            dynamic next = nextObject!;
            var inbound = request.FirewallRuleDirection == FirewallRuleDirection.In;
            next.Name = name; next.Grouping = Group; next.Description = "Managed by RelaxKonOS";
            next.Direction = inbound ? 1 : 2;
            next.Protocol = request.FirewallRuleProtocol == FirewallRuleProtocol.Tcp ? 6 : request.FirewallRuleProtocol == FirewallRuleProtocol.Udp ? 17 : 256;
            next.LocalAddresses = NativeAddress(inbound ? request.FirewallDestination! : request.FirewallSource!);
            next.RemoteAddresses = NativeAddress(inbound ? request.FirewallSource! : request.FirewallDestination!);
            if (request.FirewallRuleProtocol != FirewallRuleProtocol.Any)
            {
                if (inbound) next.LocalPorts = NativeAddress(request.FirewallPort!).Replace(':', '-');
                else next.RemotePorts = NativeAddress(request.FirewallPort!).Replace(':', '-');
            }
            next.Profiles = 7; next.Action = request.FirewallRuleAction == FirewallRuleAction.Allow ? 1 : 0; next.Enabled = true;
            if (oldObject is not null) rules.Remove(name);
            try { rules.Add(next); }
            catch { if (oldObject is not null) rules.Add(oldObject); throw; }
            return new(true);
        }
        finally { Release(nextObject); Release(oldObject); Release(rulesObject); }
    }
    private static bool Owned(dynamic rule, out int number)
    {
        number = 0;
        string name = rule.Name;
        return (string)rule.Grouping == Group && name.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(name[Prefix.Length..], out number) && number > 0;
    }
    private static string Family(string local, string remote)
    {
        var addresses = new[] { local, remote }.Where(a => a != "*" && !string.IsNullOrEmpty(a)).ToArray();
        return addresses.Length == 0 ? "IPv4 + IPv6" : addresses.Any(a => a.Contains(':')) ? "IPv6" : "IPv4";
    }
    private static string NativeAddress(string value) => value == "any" ? "*" : value;
    private static string Address(string value) => string.IsNullOrEmpty(value) || value == "*" ? "any" : value;
    private static string PolicyName(int value) => value == 1 ? "allow" : "deny";
    private static PrivilegedOperationResult Output<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static PrivilegedOperationResult Failure(PrivilegedProblemCode code) => new(false, ProblemCode: code);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
