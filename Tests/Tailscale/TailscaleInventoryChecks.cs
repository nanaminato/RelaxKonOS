using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;

internal static class TailscaleInventoryChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Tailscale inventory assertion "+count); count++; }
        void Reject(Fake fake)
        {
            try { new TailscaleWindowsInventory(fake).Read(default); throw new Exception("Invalid inventory accepted"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "component.tailscale_inventory_unavailable"); }
        }
        var machine = new TailscaleWindowsInventory.Instance("{54939609-8ED7-550F-95F0-CDE77EC4499A}",4,null);
        var user = machine with { Context = 2,UserSid = "S-1-5-21-100-200-300-1001" };
        var fake = new Fake { Items = [machine,user] };
        var result = new TailscaleWindowsInventory(fake).Read(default);
        Check(result.Count == 2 && result[0].Context == 4 && result[1].Context == 2);
        Check(result.All(p => p.Version == "1.104.4" && !p.Advertised));
        Check(!JsonSerializer.Serialize(user).Contains(user.UserSid!) && !user.ToString().Contains(user.UserSid!));
        Check(!JsonSerializer.Serialize(result).Contains(user.UserSid!));
        Check(new TailscaleWindowsInventory(new Fake()).Read(default).Count == 0);
        Check(new TailscaleWindowsInventory(new Fake { Items = [machine],Name = "Other" }).Read(default).Count == 0);
        var advertised = new TailscaleWindowsInventory(new Fake { Items = [user],State = "1" }).Read(default).Single();
        Check(advertised.Advertised && advertised.Version is null && advertised.Publisher is null);
        foreach (var invalid in new[] { machine with { ProductCode = "invalid" },machine with { ProductCode = Guid.Empty.ToString("B") },
            machine with { Context = 7 },machine with { UserSid = user.UserSid },user with { UserSid = null } }) Reject(new Fake { Items = [invalid] });
        Reject(new Fake { Items = [machine,machine] });
        Reject(new Fake { Items = [machine],State = "0" });
        Reject(new Fake { Items = [machine],Name = "Tailscale\n" });
        Reject(new Fake { Items = [machine],Name = new string('x',513) });
        Reject(new Fake { Items = [machine],FailProperties = true });
        Reject(new Fake { Items = [machine],FailEnumeration = true });
        var many = Enumerable.Range(0,TailscaleWindowsInventory.MaximumProducts+1).Select(_ => machine with { ProductCode = Guid.NewGuid().ToString("B").ToUpperInvariant() }).ToArray();
        Reject(new Fake { Items = many });
        Check(new TailscaleWindowsInventory(new Fake { Items = many[..TailscaleWindowsInventory.MaximumProducts],Name = "Other" }).Read(default).Count == 0);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { new TailscaleWindowsInventory(fake).Read(cancelled.Token); throw new Exception("Cancelled inventory accepted"); }
        catch (OperationCanceledException) { Check(true); }
        using var late = new CancellationTokenSource();
        try { new TailscaleWindowsInventory(new Fake { Items = [machine],OnProperty = () => late.Cancel() }).Read(late.Token); throw new Exception("Late inventory accepted"); }
        catch (OperationCanceledException) { Check(true); }
        Console.WriteLine($"Tailscale Windows inventory checks passed: {count}");
    }
    private sealed class Fake : TailscaleWindowsInventory.IBackend
    {
        internal TailscaleWindowsInventory.Instance[] Items = [];
        internal string State = "5",Name = "Tailscale";
        internal bool FailEnumeration,FailProperties;
        internal Action? OnProperty;
        public TailscaleWindowsInventory.Instance? Enumerate(uint index)
        { if (FailEnumeration) throw new RuntimePolicyException("component.tailscale_inventory_unavailable"); return index < Items.Length ? Items[index] : null; }
        public string Property(TailscaleWindowsInventory.Instance item,string name)
        {
            if (FailProperties) throw new RuntimePolicyException("component.tailscale_inventory_unavailable");
            OnProperty?.Invoke();
            return name switch { "State" => State,"InstalledProductName" or "ProductName" => Name,"VersionString" => "1.104.4","Publisher" => "Tailscale Inc.",_ => throw new Exception("Unexpected property") };
        }
    }
}
