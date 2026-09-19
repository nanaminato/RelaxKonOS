using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Covers the developer console host's client allowlist. The Helper must run elevated, so on a
/// workstation it usually runs under a different account than the Server that connects to it; the
/// allowlist is what authorizes that caller. A mismatch is refused by the kernel before any
/// authentication happens, so it reads as a wrong shared secret instead of an access denial —
/// these checks pin the allowlist and the resulting DACL down, including the refusal path.
/// </summary>
internal static class DeveloperUserSidAllowListVerification
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Privileged Helper console client allowlist skipped: Windows pipe ACLs are unavailable on this platform.");
            return;
        }

        VerifyResolve();
        await VerifyPipeAccessAsync();
        Console.WriteLine("Privileged Helper console client allowlist passed: configured SIDs resolve and dedupe, invalid entries fail loudly, "
            + "and a real pipe accepts the configured identity while refusing an unlisted one.");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyResolve()
    {
        const string helperUser = "S-1-5-21-518898542-3752080965-3168045265-1005";
        const string serverUser = "S-1-5-21-518898542-3752080965-3168045265-1006";

        // Backward compatibility: a debug configuration written before this option existed must keep
        // working for the developer who started the Helper.
        var alone = DeveloperUserSidAllowList.Resolve(helperUser, null);
        Check(alone.Count == 1 && alone[0] == helperUser, "The Helper's own identity must always stay authorized.");

        // The point of the option: the Server runs as a different, non-elevated account.
        var both = DeveloperUserSidAllowList.Resolve(helperUser, [serverUser]);
        Check(both.SequenceEqual([helperUser, serverUser]), "A configured client SID must be authorized next to the Helper's own identity.");

        // Two spellings of one account would otherwise produce two rules in the DACL.
        var deduped = DeveloperUserSidAllowList.Resolve(helperUser, [serverUser, serverUser.ToLowerInvariant(), helperUser.ToLowerInvariant()]);
        Check(deduped.SequenceEqual([helperUser, serverUser]), "Duplicate or differently-cased entries must collapse into one SID.");

        // Account names are accepted for readability and must resolve to the real SID.
        using var current = WindowsIdentity.GetCurrent();
        var byName = DeveloperUserSidAllowList.Resolve("S-1-5-18", [current.Name]);
        Check(byName.Count == 2 && byName[1] == current.User?.Value, "An account name must resolve to that account's SID.");

        // A silently dropped entry would surface later as an unexplained denial on the client.
        RejectEntry(string.Empty, "an empty entry");
        RejectEntry("   ", "a whitespace entry");
        RejectEntry("S-1-5-21-not-a-sid", "a malformed SID");
        RejectEntry("relaxkonos-no-such-account-" + Guid.NewGuid().ToString("N"), "an unresolvable account name");
        Reject([.. Enumerable.Range(0, DeveloperUserSidAllowList.MaximumEntries + 1).Select(index => $"S-1-5-21-1-1-1-{2000 + index}")],
            "an oversized list");
    }

    [SupportedOSPlatform("windows")]
    private static async Task VerifyPipeAccessAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value ?? throw new InvalidOperationException("The test identity has no SID.");
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;

        var rules = WindowsPrivilegedPipeSecurity.Build(null, [userSid])
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).OfType<PipeAccessRule>().ToArray();
        // Read-back rights always carry additional bits (Synchronize), so the rights are compared by
        // mask. What matters is that the client can connect and exchange frames, which ReadWrite covers.
        Check(rules.Any(rule => rule.IdentityReference.Value == userSid
                && (rule.PipeAccessRights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite),
            "A configured client identity must receive ReadWrite on the pipe.");
        Check(rules.Any(rule => rule.IdentityReference.Value == system && HasFullControl(rule))
            && rules.Any(rule => rule.IdentityReference.Value == administrators && HasFullControl(rule)),
            "LocalSystem and local Administrators must keep full control of the pipe.");
        Check(WindowsPrivilegedPipeSecurity.Build(null, null).AreAccessRulesProtected,
            "The pipe DACL must not inherit rights from the creating process.");
        var repeated = WindowsPrivilegedPipeSecurity.Build(null, [administrators])
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).OfType<PipeAccessRule>().ToArray();
        Check(repeated.Length == 2 && repeated.Any(rule => rule.IdentityReference.Value == administrators && HasFullControl(rule)),
            "Naming a well-known identity as a client must neither duplicate nor weaken its rule.");

        // The DACL, not the protocol, is what refuses a caller, so it is asserted with real pipes.
        Check(await TryConnectAsync("relaxkonos-client-test-", WindowsPrivilegedPipeSecurity.Build(null, [userSid])),
            "The configured client identity must be able to connect to a pipe carrying this DACL.");
        Check(await TryConnectAsync("relaxkonos-server-sid-test-", WindowsPrivilegedPipeSecurity.Build(userSid, null)),
            "The deployed Server service SID rule must remain connectable.");
        var unlisted = await TryConnectAsync("relaxkonos-unlisted-test-", WindowsPrivilegedPipeSecurity.Build(null, null));
        // Administrators hold full control, so an elevated test identity connects through that rule;
        // an unelevated one reproduces the reported failure exactly.
        Check(unlisted == elevated, "An identity absent from the DACL must be refused unless local Administrators grants it access.");
    }

    private static bool HasFullControl(PipeAccessRule rule)
        => (rule.PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl;

    [SupportedOSPlatform("windows")]
    private static async Task<bool> TryConnectAsync(string pipePrefix, PipeSecurity security)
    {
        var pipeName = pipePrefix + Guid.NewGuid().ToString("N");
        await using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 512, 512, security);
        var accepting = server.WaitForConnectionAsync();
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            await accepting;
            await client.WriteAsync(new byte[] { 42 });
            var payload = new byte[1];
            await server.ReadExactlyAsync(payload);
            return payload[0] == 42;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            await server.DisposeAsync();
            // A refused connection never reaches the server, so the pending accept is released here
            // rather than awaited indefinitely.
            await Task.WhenAny(accepting, Task.Delay(TimeSpan.FromSeconds(2)));
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RejectEntry(string? entry, string description) => Reject([entry!], description);

    [SupportedOSPlatform("windows")]
    private static void Reject(IEnumerable<string> entries, string description)
    {
        try
        {
            DeveloperUserSidAllowList.Resolve("S-1-5-18", entries);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException($"The client allowlist must reject {description} instead of ignoring it.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
