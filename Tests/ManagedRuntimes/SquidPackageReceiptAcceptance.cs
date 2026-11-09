using System.Runtime.Versioning;

[SupportedOSPlatform("linux")]
internal static class SquidPackageReceiptAcceptance
{
    public static async Task RunAsync(string root, string socket, string phase)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost();
        if (root != "/mnt/rk-squid-package/work" || !File.Exists("/etc/relaxkonos-squid-package-fixture"))
            throw new InvalidOperationException("The Squid package test requires the dedicated writable root.");
        if (phase is not ("cut" or "install-cut" or "recover" or "recovery-cut" or "native-cut" or "native-recover"))
            throw new ArgumentException("Unknown Squid package test phase.", nameof(phase));
        var recovering = phase is "recover" or "recovery-cut" or "native-recover";
        var recoveryCut = phase == "recovery-cut";
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = budget.Token;
        var heldOperation = phase switch
        {
            "cut" => "ComponentPackageChange",
            "install-cut" => "ComponentPackageInstall",
            "recovery-cut" => "ComponentPackageQuery",
            _ => null
        };
        var originalId = recoveryCut ? Guid.Parse(File.ReadAllText(Path.Combine(root, "package-reply-held"))) : Guid.Empty;
        await using var relay = heldOperation is null ? null : new ProxyAuthenticatedPublicationChecks.ReplyRelay(
            "/run/rk-squid-package-reply.sock", socket, ct,
            (operation, id) => operation == heldOperation && (!recoveryCut || id == originalId),
            observeTerminal: result =>
            {
                if (!result.GetProperty("success").GetBoolean() && result.TryGetProperty("error", out var error))
                    File.WriteAllText(Path.Combine(root, "package-refusal"), error.GetString());
                if (recoveryCut)
                    File.WriteAllText(Path.Combine(root, "package-query-result"), result.GetRawText());
            });
        var assembly = Path.Combine(AppContext.BaseDirectory, "RelaxKonOS.Server.dll");
        var start = await ManagedServiceChecks.PrepareFullServerAsync(assembly, relay?.Path ?? socket, root,
            reuse: recovering, seedProxyServer: false);
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:18972";
        if (phase.StartsWith("native-", StringComparison.Ordinal) || File.ReadAllText(Path.Combine(root, "phase")).Trim() == "recovery-cut")
            start.Environment["Modules__Proxy__Enabled"] = "true";
        start.Environment["Identity__LinuxPamTransport"] = "helper";
        start.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";
        start.Environment["Logging__LogLevel__Microsoft.EntityFrameworkCore"] = "Warning";
        using var server = ProxyAuthenticatedPublicationChecks.Start(start, Console.Error.WriteLine, Console.WriteLine);
        await MarkerAsync("server-pid", server.Id.ToString());
        var exited = server.WaitForExitAsync(ct);
        if (relay is not null)
        {
            if (await Task.WhenAny(relay.Committed.Task, exited) == exited)
                throw new InvalidOperationException("Server stopped before the original Squid package reply was held.");
            await MarkerAsync(recoveryCut ? "package-query-held" : "package-reply-held",
                (await relay.Committed.Task.WaitAsync(ct)).ToString("D"));
        }
        await exited;
        if (server.ExitCode != (recovering && !recoveryCut ? 0 : 137))
            throw new InvalidOperationException($"Unexpected Server exit code {server.ExitCode}.");

        async Task MarkerAsync(string name, string value)
        {
            var path = Path.Combine(root, name);
            await File.WriteAllTextAsync(path + ".new", value, ct);
            File.Move(path + ".new", path, overwrite: true);
        }
    }
}
