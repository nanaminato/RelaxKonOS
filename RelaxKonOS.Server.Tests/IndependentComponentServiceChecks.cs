using RelaxKonOS.PrivilegedHelper;

static class IndependentComponentServiceChecks
{
    public static void Run(string root)
    {
        var identity = new string('a', 64);
        var profile = Guid.NewGuid();
        var request = new ManagedRuntimeRequest(ManagedRuntime.Frpc, ManagedRuntimeAction.Start, "v0.71.0", profile,
            Client: new("example.test", 7000, TunnelTlsMode.Force, "private-token", []), AppliedIdentity: identity);
        TestAssert.Assert(WindowsRuntimeConfigurationWriter.IsValidRequest(request), "Structured service start was rejected.");
        TestAssert.Assert(!WindowsRuntimeConfigurationWriter.IsValidRequest(request with { AppliedIdentity = null })
            && !WindowsRuntimeConfigurationWriter.IsValidRequest(request with { AppliedIdentity = identity + "\n" })
            && !WindowsRuntimeConfigurationWriter.IsValidRequest(request with { AppliedIdentity = "secret\ncommand" })
            && !WindowsRuntimeConfigurationWriter.IsValidRequest(request with { ProfileId = Guid.Empty })
            && !WindowsRuntimeConfigurationWriter.IsValidRequest(request with { Action = ManagedRuntimeAction.Status }),
            "Service operation accepted a missing identity, malformed proof, empty profile, or unrelated configuration fields.");
        var snapshot = new ManagedProcessSnapshot(true, true, false, DateTimeOffset.UtcNow, [], identity);
        var serialized = JsonSerializer.Serialize(new PrivilegedOperationResult(true, ComponentProcess: snapshot));
        var restored = JsonSerializer.Deserialize<PrivilegedOperationResult>(serialized)!;
        TestAssert.Assert(restored.ComponentProcess?.AppliedIdentity == identity && !serialized.Contains("private-token"),
            "Service observation lost applied proof or disclosed runtime credentials.");
        if (OperatingSystem.IsWindows())
        {
            var job = WindowsComponentJob.Create();
            var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("-n"); info.ArgumentList.Add("30"); info.ArgumentList.Add("127.0.0.1");
            using var child = System.Diagnostics.Process.Start(info) ?? throw new IOException("Job test process did not start.");
            try
            {
                WindowsComponentJob.Attach(job, child);
                job.Dispose();
                TestAssert.Assert(child.WaitForExit(3000), "Closing the independent host job left its child process alive.");
            }
            finally { job.Dispose(); if (!child.HasExited) child.Kill(true); }
            var first = WindowsComponentService.Name(ComponentKind.Frpc, root, profile.ToString("N"));
            TestAssert.Assert(first != WindowsComponentService.Name(ComponentKind.Frpc, root, Guid.NewGuid().ToString("N"))
                && first != WindowsComponentService.Name(ComponentKind.Frpc, root + "-personal", profile.ToString("N"))
                && first == WindowsComponentService.Name(ComponentKind.Frpc, root.ToUpperInvariant(), profile.ToString("N")),
                "SCM identities collided across profiles/installations or drifted with Windows path casing.");
            var config = new ComponentServiceConfiguration(ComponentKind.Nginx, WindowsComponentService.Name(ComponentKind.Nginx, root),
                root, Path.Combine(root, "nginx.exe"), Path.Combine(root, "conf", "nginx.conf"), null, identity, null);
            WindowsComponentService.Validate(config);
            var denied = false;
            try { WindowsComponentService.Validate(config with { Executable = Path.Combine(Path.GetDirectoryName(root)!, "outside.exe") }); }
            catch (InvalidDataException) { denied = true; }
            TestAssert.Assert(denied, "Component host accepted an executable outside its managed scope.");
        }
    }
}
