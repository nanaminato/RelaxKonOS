using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using P = RelaxKonOS.PrivilegedHelper.TailscaleWindowsInstallation.Presence;
using K = RelaxKonOS.PrivilegedHelper.TailscaleWindowsInstallation.Kind;

internal static class TailscaleInstallationObservationChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Tailscale installation observation assertion "+count); count++; }
        TailscaleWindowsInventory.Product item = new("{54939609-8ED7-550F-95F0-CDE77EC4499A}",4,false,"1.104.4","Tailscale Inc.");
        TailscaleWindowsInstallation Model(P files = P.Absent,P data = P.Absent,P service = P.Absent,
            IReadOnlyList<TailscaleWindowsInventory.Product>? items = null) => new(_ => items ?? [],() => files,() => data,() => service);
        Check(Model().Read(default).Kind == K.NoKnownArtifacts);
        Check(Model(files:P.Present).Read(default).Kind == K.ExistingUnowned);
        Check(Model(data:P.Present).Read(default).Kind == K.ExistingUnowned);
        Check(Model(service:P.Present).Read(default).Kind == K.ExistingUnowned);
        Check(Model(items:[item]).Read(default).Kind == K.ExistingUnowned);
        Check(Model(items:[item with { Advertised = true,Version = null,Publisher = null }]).Read(default).Kind == K.ExistingUnowned);
        Check(Model(items:[item with { Context = 1 }]).Read(default).Kind == K.ExistingUnowned); // MSI managed != RelaxKonOS owned
        foreach (var unknown in new[] { Model(files:P.Unavailable),Model(data:P.Unavailable),Model(service:P.Unavailable),
            Model(files:P.Present,data:P.Unavailable),Model(service:P.Unavailable,items:[item]),Model(files:(P)999) })
        {
            var value = unknown.Read(default); Check(value.Kind == K.Unavailable && value.ProblemCode is not null);
        }
        var denied = new TailscaleWindowsInstallation(_ => throw new RuntimePolicyException("component.tailscale_inventory_access_denied"),
            () => P.Present,() => P.Absent,() => P.Absent).Read(default);
        Check(denied.Kind == K.Unavailable && denied.Programs == P.Present && denied.Registrations is null
            && denied.ProblemCode == "component.tailscale_inventory_access_denied");
        var unreadable = new TailscaleWindowsInstallation(_ => [],() => throw new UnauthorizedAccessException(),() => P.Absent,() => P.Absent).Read(default);
        Check(unreadable.Kind == K.Unavailable && unreadable.Programs == P.Unavailable);
        var originals = new List<TailscaleWindowsInventory.Product> { item };
        var snapshot = Model(items:originals).Read(default); originals.Clear();
        Check(snapshot.Registrations?.Count == 1 && snapshot.Kind == K.ExistingUnowned);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var calls = 0;
        var pending = new TailscaleWindowsInstallation(_ => { calls++; return []; },() => P.Absent,() => P.Absent,() => P.Absent);
        try { pending.Read(cancelled.Token); throw new Exception("Cancelled observation accepted"); }
        catch (OperationCanceledException) { Check(calls == 0); }
        using var late = new CancellationTokenSource(); var laterCalls = 0;
        var interrupted = new TailscaleWindowsInstallation(_ => [],() => { late.Cancel(); return P.Present; },
            () => { laterCalls++; return P.Absent; },() => { laterCalls++; return P.Absent; });
        try { interrupted.Read(late.Token); throw new Exception("Late observation accepted"); }
        catch (OperationCanceledException) { Check(laterCalls == 0); }
        Console.WriteLine($"Tailscale installation observation checks passed: {count}");
    }
}
