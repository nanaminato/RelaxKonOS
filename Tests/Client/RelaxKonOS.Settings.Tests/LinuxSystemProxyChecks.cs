using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

internal static class LinuxSystemProxyChecks
{
    private static readonly LinuxSystemProxyConfiguration Enabled = new(true, "127.0.0.1", 7890, true, "example.test;10.0.0.0/8");
    private static readonly LinuxProxyTarget EnvironmentTarget = new("environment");
    private static readonly LinuxProxyTarget Gnome = new("gnome", 1000, "desktop", "/home/desktop");
    private static readonly LinuxProxyTarget Kde = new("kde", 1001, "plasma", "/home/plasma");

    public static void Run()
    {
        using (var fixture = new Fixture())
        {
            var originals = fixture.Snapshot();
            fixture.Transaction.Apply(Enabled);
            Check(fixture.Value(EnvironmentTarget, "HTTPS_PROXY") == "http://127.0.0.1:7890", "HTTPS must use the HTTP CONNECT endpoint.");
            Check(fixture.Value(EnvironmentTarget, "all_proxy") == "socks5://127.0.0.1:7890", "SOCKS endpoint missing.");
            Check(fixture.Value(Gnome, "http/port") == "7890", "GNOME requires a signed integer variant.");
            Check(fixture.Value(Gnome, "http/use-authentication") == "false", "Old proxy authentication must not reach Mihomo.");
            Check(fixture.Value(Kde, "ProxyType") == "1" && fixture.Value(Kde, "socksProxy") == "socks://127.0.0.1:7890", "KDE manual proxy is invalid.");
            fixture.Transaction.Apply(Enabled with { Port = 8888 });
            fixture.Backend.Targets = []; // Logout must not lose recovery ownership.
            fixture.Restart().Apply(Enabled with { Enabled = false });
            fixture.AssertSnapshot(originals);
            Check(!File.Exists(fixture.Journal), "Recovery journal survived a complete restore.");
            Check(File.ReadAllText(fixture.EnvironmentFile).Contains("# administrator comment\nPATH=\"/custom/bin\""), "Unrelated environment text was rewritten.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Transaction.Apply(Enabled);
            fixture.External(EnvironmentTarget, "HTTP_PROXY", "http://user.example:1234");
            fixture.External(Gnome, "ignore-hosts", "['user.test']");
            ExpectFailure(() => fixture.Transaction.Apply(Enabled with { Port = 8888 }));
            Check(fixture.Value(EnvironmentTarget, "HTTP_PROXY") == "http://user.example:1234", "Conflict overwrote the user's edit.");
            fixture.Restart().Apply(Enabled with { Enabled = false });
            Check(fixture.Value(EnvironmentTarget, "HTTP_PROXY") == "http://user.example:1234"
                && fixture.Value(Gnome, "ignore-hosts") == "['user.test']", "Disable erased later edits.");
        }
        using (var fixture = new Fixture())
        {
            var originals = fixture.Snapshot();
            fixture.Transaction.Apply(Enabled);
            fixture.External(Gnome, "mode", "'none'");
            fixture.Transaction.Apply(Enabled with { Enforce = true });
            Check(fixture.Value(Gnome, "mode") == "'manual'", "Explicit guard did not enforce desired state.");
            fixture.Transaction.Apply(Enabled with { Enabled = false });
            fixture.AssertSnapshot(originals);
        }
        using (var fixture = new Fixture())
        {
            var originals = fixture.Snapshot();
            fixture.Backend.OnWrite = (call, _, key) => { if (call == 2 && key == 1) throw new IOException("partial desktop write"); };
            ExpectFailure(() => fixture.Transaction.Apply(Enabled));
            fixture.AssertSnapshot(originals);
            Check(!File.Exists(fixture.Journal), "Compensated first enable left a recovery journal.");
        }
        using (var fixture = new Fixture())
        {
            var originals = fixture.Snapshot();
            fixture.Backend.OnWrite = (call, _, key) => { if (call is 2 or 3 && key == 1) throw new IOException("write and compensation failure"); };
            ExpectFailure(() => fixture.Transaction.Apply(Enabled));
            Check(File.Exists(fixture.Journal), "Failed compensation lost its original snapshot.");
            fixture.Backend.OnWrite = null;
            fixture.Restart().Apply(Enabled with { Enabled = false });
            fixture.AssertSnapshot(originals);
        }
        using (var fixture = new Fixture())
        {
            fixture.Transaction.Apply(Enabled);
            fixture.External(EnvironmentTarget, "HTTP_PROXY", "http://later.example:4444");
            fixture.Backend.ResetWriteCount();
            fixture.Backend.OnWrite = (call, _, key) => { if (call is 2 or 3 && key == 1) throw new IOException("restore interrupted"); };
            ExpectFailure(() => fixture.Transaction.Apply(Enabled with { Enabled = false }));
            fixture.Backend.OnWrite = null;
            fixture.Restart().Apply(Enabled with { Enabled = false });
            Check(fixture.Value(EnvironmentTarget, "HTTP_PROXY") == "http://later.example:4444", "Recovery acquired ownership of an external edit.");
        }
        using (var fixture = new Fixture())
        {
            var originals = fixture.Snapshot();
            fixture.Transaction.Apply(Enabled);
            fixture.Backend.ResetWriteCount();
            fixture.Backend.OnWrite = (call, _, key) => { if (call is 2 or 3 && key == 1) throw new IOException("update interrupted"); };
            ExpectFailure(() => fixture.Transaction.Apply(Enabled with { Port = 8888 }));
            fixture.Backend.OnWrite = null;
            fixture.Restart().Apply(Enabled with { Enabled = false });
            fixture.AssertSnapshot(originals);
        }
        var ipv6 = LinuxSystemProxyTransaction.Values("environment", Enabled with { Host = "::1" });
        Check(ipv6["HTTP_PROXY"] == "http://[::1]:7890", "IPv6 endpoint lacks brackets.");
        foreach (var bypass in new[] { "x\nHTTP_PROXY=evil", "<local>", "example.test'", "$(command)" })
            ExpectFailure(() => LinuxSystemProxyTransaction.Validate(Enabled with { BypassList = bypass }));
        ExpectFailure(() => LinuxSystemProxyTransaction.Validate(Enabled with { Host = "198.51.100.42" }));
        ExpectFailure(() => LinuxSystemProxyTransaction.Validate(Enabled with { Port = 0 }));
        Check(LinuxSystemProxyTransaction.Equivalent("@as []", "[]"), "Typed empty GNOME arrays failed comparison.");
        Console.WriteLine("Linux proxy environment/GNOME/KDE, partial failure, durable recovery, ownership, guard and validation checks passed.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void ExpectFailure(Action action)
    {
        try { action(); } catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { return; }
        throw new Exception("Expected proxy operation to fail.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "relaxkonos-linux-proxy-test-" + Guid.NewGuid().ToString("N"));
        public string Journal => Path.Combine(root, "recovery.json");
        public string EnvironmentFile => Path.Combine(root, "environment");
        public FakeBackend Backend { get; }
        public LinuxSystemProxyTransaction Transaction { get; }
        public Fixture()
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(EnvironmentFile, "# administrator comment\nPATH=\"/custom/bin\"\nHTTP_PROXY=\"http://old.example:80\"\n");
            Backend = new(EnvironmentFile);
            Backend.Values[Gnome] = new() { ["mode"] = "'auto'", ["ignore-hosts"] = "@as []", ["http/use-authentication"] = "true" };
            Backend.Values[Kde] = new() { ["ProxyType"] = "2", ["NoProxyFor"] = "old.test" };
            Transaction = Restart();
        }
        public LinuxSystemProxyTransaction Restart() => new(Journal, Backend);
        public Dictionary<LinuxProxyTarget, Dictionary<string, string?>> Snapshot() => new[] { EnvironmentTarget, Gnome, Kde }
            .ToDictionary(target => target, target => Backend.Read(target, LinuxSystemProxyTransaction.Values(target.Provider, Enabled).Keys));
        public string? Value(LinuxProxyTarget target, string key) => Backend.Read(target, [key])[key];
        public void External(LinuxProxyTarget target, string key, string value) => Backend.Write(target, new Dictionary<string, string?> { [key] = value });
        public void AssertSnapshot(Dictionary<LinuxProxyTarget, Dictionary<string, string?>> originals)
        {
            foreach (var target in originals)
                foreach (var pair in target.Value)
                    Check(LinuxSystemProxyTransaction.Equivalent(Value(target.Key, pair.Key), pair.Value), $"{target.Key.Provider}/{pair.Key} was not restored.");
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
    private sealed class FakeBackend(string environmentFile) : ILinuxSystemProxyBackend
    {
        private static readonly SettingsTarget Target = new("host/environment/machine", SettingsScope.HostMachine);
        public IReadOnlyList<LinuxProxyTarget> Targets = [Gnome, Kde];
        public readonly Dictionary<LinuxProxyTarget, Dictionary<string, string?>> Values = new();
        public Action<int, LinuxProxyTarget, int>? OnWrite;
        private int writes;
        public void ResetWriteCount() => writes = 0;
        public IReadOnlyList<LinuxProxyTarget> DesktopTargets() => Targets;
        public Dictionary<string, string?> Read(LinuxProxyTarget target, IEnumerable<string> keys)
        {
            var values = target.Provider == "environment"
                ? LinuxEnvironmentDocument.Parse(File.ReadAllText(environmentFile)).Values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)
                : Values[target];
            return keys.ToDictionary(key => key, key => values.GetValueOrDefault(key));
        }
        public void Write(LinuxProxyTarget target, IReadOnlyDictionary<string, string?> values)
        {
            var call = ++writes;
            var index = 0;
            foreach (var pair in values)
            {
                if (target.Provider == "environment")
                {
                    var read = LinuxEnvironmentOperations.ExecuteForPath(new(PrivilegedOperationKind.HostEnvironmentRead, EnvironmentTarget: Target), environmentFile, true);
                    var result = LinuxEnvironmentOperations.ExecuteForPath(new(PrivilegedOperationKind.HostEnvironmentApply, EnvironmentTarget: Target,
                        ExpectedRevision: read.HostEnvironment!.Revision, EnvironmentChange: new([new(pair.Key,
                            pair.Value is null ? EnvironmentMutationKind.Delete : EnvironmentMutationKind.Set, pair.Value)], true)), environmentFile, true);
                    Check(result.Success, "Fixture environment CAS failed.");
                }
                else Values[target][pair.Key] = pair.Value;
                OnWrite?.Invoke(call, target, ++index);
            }
        }
        public void Notify(LinuxProxyTarget target) { }
    }
}
