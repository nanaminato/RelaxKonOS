internal static class ComponentCenterHostAcceptanceChecks
{
    public static void Run()
    {
        var valid = new[] { "--component-center-host", "https://test.invalid/", "https://test.invalid/", "acceptance-user",
            "sing-box-acceptance", "older", "newer", Path.GetFullPath("artifacts/component-center-host") };
        var options = ComponentCenterHostAcceptance.Parse(valid);
        Check(options.Server.Scheme == "https" && options.Older != options.Newer, "explicit HTTPS test target and distinct release identities are required");
        foreach (var changed in new[] {
            (1, "http://test.invalid/"), (2, "https://another.invalid/"), (1, "https://user:secret@test.invalid/"),
            (1, "https://test.invalid/?token=x"), (1, "https://test.invalid/#fragment"), (1, "https://test.invalid/other/"),
            (4, "source/../other"), (4, ""), (5, "newer") })
        {
            var candidate = valid.ToArray(); candidate[changed.Item1] = changed.Item2;
            if (changed.Item1 == 1) candidate[2] = candidate[1];
            try { ComponentCenterHostAcceptance.Parse(candidate); throw new Exception("Unsafe acceptance arguments admitted"); }
            catch (ArgumentException) { Check(true, "unsafe target, confirmation, source or lifecycle selection is rejected before network access"); }
        }
        try { ComponentCenterHostAcceptance.Parse(valid[..^1]); throw new Exception("Incomplete invocation admitted"); }
        catch (ArgumentException) { Check(true, "incomplete invocation cannot log in or create a host operation"); }
        var observe = new[] { "--component-center-observe-host", "https://test.invalid/", "https://test.invalid/", "test-user", "previous-evidence", "new-observation" };
        Check(ComponentCenterHostObservation.Parse(observe).Server.Scheme == "https", "historical observation binds an explicit HTTPS target");
        foreach (var changed in new[] { (1, "http://test.invalid/"), (2, "https://other.invalid/"), (1, "https://user:secret@test.invalid/"),
            (1, "https://test.invalid/path/"), (1, "https://test.invalid/?token=x"), (4, "") })
        {
            var candidate = observe.ToArray(); candidate[changed.Item1] = changed.Item2;
            if (changed.Item1 == 1) candidate[2] = candidate[1];
            try { ComponentCenterHostObservation.Parse(candidate); throw new Exception("Unsafe observation admitted"); }
            catch (ArgumentException) { Check(true, "unsafe historical target or incomplete evidence location is rejected"); }
        }
        var sends = 0;
        using var transport = new ComponentCenterHostObservation.ReadOnlyTransport(new Handler(_ =>
        { sends++; return new(System.Net.HttpStatusCode.OK); }));
        using var http = new HttpClient(transport);
        using var login = http.PostAsync("https://test.invalid" + RelaxKonOS.Protocol.Identity.AuthApiRoutes.Login, null).GetAwaiter().GetResult();
        transport.AllowLogin = false;
        using var read = http.GetAsync("https://test.invalid/evidence").GetAwaiter().GetResult();
        Check(sends == 2 && transport.Reads == 1, "observation transport permits one login phase and historical GET");
        foreach (var path in new[] { "/advance", "/approve", "/recover", RelaxKonOS.Protocol.Identity.AuthApiRoutes.Login })
        {
            try { using var result = http.PostAsync("https://test.invalid" + path, null).GetAwaiter().GetResult(); throw new Exception("Observation write admitted"); }
            catch (InvalidOperationException ex) when (ex.Message == "acceptance.write_forbidden")
            { Check(sends == 2, "observation blocks lifecycle writes and repeated login before transport: " + path); }
        }
        var installationId = Guid.NewGuid();
        var plan = TaskPreviewClientChecks.ChangePlan(new("app", "relaxkonos.component-center", 1,
            [new("change", "runtime.uninstall", 1, "sing-box-change-1", RelaxKonOS.Protocol.TaskOrchestration.TaskPlanJson.FreezeInputs(
                new RelaxKonOS.Protocol.TaskOrchestration.RuntimeTaskChangeInput(installationId, "host", 7, 0, 0, null, null, false, true)), [])]), Guid.NewGuid());
        var reference = new RelaxKonOS.Client.Apps.ComponentCenter.ComponentTaskReference(plan, Guid.NewGuid(), "original-start", Guid.NewGuid());
        var state = new RelaxKonOS.Protocol.ManagedRuntimes.RuntimeInstallation(installationId, "host", "sing-box", 9,
            RelaxKonOS.Protocol.ManagedRuntimes.RuntimeOwnership.Absent,
            new("linux", "amd64", null, null, RelaxKonOS.Protocol.ManagedRuntimes.RuntimeInstallMode.Archive, "systemd"), null, false);
        var snapshot = new RelaxKonOS.Protocol.ManagedRuntimes.RuntimeInstallationSnapshot(state, null, null, null, null, null, null);
        Check(ComponentCenterHostObservation.MatchesFinalInstallation(snapshot, [reference], 9), "fresh final state remains bound to original installation");
        foreach (var replaced in new[] { state with { HostId = "other-host" }, state with { InstallationId = Guid.NewGuid() }, state with { RuntimeId = "mihomo" }, state with { StateRevision = 8 } })
            Check(!ComponentCenterHostObservation.MatchesFinalInstallation(snapshot with { Installation = replaced }, [reference], 9),
                "final observation rejects replacement host, installation, runtime or older state");
        var cancellation = new[] { "--component-center-cancel-host", "https://test.invalid/", "https://test.invalid/", "test-user", "source", "release", "cancellation-evidence" };
        Check(ComponentCenterHostCancellation.Parse(cancellation).Release == "release", "real cancellation requires an explicit target release");
        foreach (var changed in new[] { (1, "http://test.invalid/"), (2, "https://other.invalid/"), (1, "https://user:secret@test.invalid/"),
            (1, "https://test.invalid/path/"), (1, "https://test.invalid/?token=x"), (4, "../source"), (5, ""), (6, "") })
        {
            var candidate = cancellation.ToArray(); candidate[changed.Item1] = changed.Item2;
            if (changed.Item1 == 1) candidate[2] = candidate[1];
            try { ComponentCenterHostCancellation.Parse(candidate); throw new Exception("Unsafe cancellation target admitted"); }
            catch (ArgumentException) { Check(true, "unsafe cancellation test target, source or incomplete selection is rejected"); }
        }
    }
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS: Host acceptance gate " + label); }
}
