using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.ServerCenter;

static class LoginTunnelChecks
{
    public static void Run()
    {
        var profile = SshLoginTunnelProfile.Create("EXAMPLE.COM", 22, " alice ", "http://127.0.0.1:5000/");
        if (profile.ServiceId != "ssh-tunnel:e464a6ec4a747c982d459dc9d7ec83753b59f5db781936ff6de3cb8809b83d65")
            throw new Exception("Desktop and Android must use the same normalized tunnel identity.");
        if (!ServerConnectionIdentityRules.PreservesIdentity(profile.Resolve(51000), profile.Resolve(52000)) ||
            profile.Resolve(51000).EffectiveBaseUrl == profile.Resolve(52000).EffectiveBaseUrl)
            throw new Exception("A local port change must only change the transport address.");
        foreach (var other in new[] { profile with { UserName = "bob" }, profile with { RemoteUrl = "http://127.0.0.1:5001" }, profile with { Host = "other.example.com" } })
            if (profile.ServiceId == other.ServiceId) throw new Exception("Different endpoints must not share credentials.");
        var saved = new SavedLoginProfile(profile.ServiceId, "server-user", null, DateTimeOffset.UtcNow);
        if (saved.DirectServerUrl != null || saved.ServiceIdKind != ServerServiceIdKind.SshTunnelProfile)
            throw new Exception("A tunnel identity must never refill a direct URL field.");
        foreach (var address in new[] { "http://example.com:5000", "ftp://127.0.0.1:5000", "http://alice@127.0.0.1:5000", "http://127.0.0.1:5000/?key=secret", "http://127.0.0.1:5000/app" })
        {
            try { SshLoginTunnelProfile.Create("example.com", 22, "alice", address); }
            catch (ArgumentException) { continue; }
            throw new Exception("An unsupported remote endpoint was accepted: " + address);
        }
        var directory = Path.Combine(Path.GetTempPath(), "relaxkonos-tunnel-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LoginTunnelStore(directory);
            store.Save(profile); store.Save(profile);
            store.Save(profile with { UserName = "bob" });
            var loaded = new LoginTunnelStore(directory).Load();
            if (loaded.Count != 2 || !loaded.Contains(profile)) throw new Exception("Tunnel configuration must survive restart without duplicate records.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("PASS: SSH login tunnel identity, endpoint validation and persistence");
    }
}
