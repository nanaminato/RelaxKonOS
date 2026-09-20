using System.Security.Principal;
using System.Text.Json;
using System.Runtime.Versioning;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Developer-only Windows host. It speaks the exact production pipe protocol while running as
/// the interactive developer, so breakpoints reach the real dispatcher and operations.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsPrivilegedHelperConsoleHost
{
    public static async Task RunAsync(string[] args)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Administrator privileges are required. Open PowerShell or Windows Terminal with 'Run as administrator', then run the Helper command again.");

        var configPath = FindConfigPath(args);
        var configJson = File.ReadAllText(configPath);
        using var document = JsonDocument.Parse(configJson);
        if (document.RootElement.EnumerateObject().Any(property =>
                property.Name.Equals("serverServiceSid", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("helperExecutableSha256", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A deployed Helper service configuration cannot be used for console debugging.");
        var configuration = JsonSerializer.Deserialize<WindowsHelperConsoleConfiguration>(configJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Windows Helper console configuration is invalid.");
        configuration.Validate();

        var userSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        // The Helper is usually started elevated, which on a workstation is a different account than
        // the Server. Resolving the configured allowlist up front turns "wrong caller" into a
        // startup error with the offending entry named, instead of an unexplained denial later.
        var authorizedClientSids = DeveloperUserSidAllowList.Resolve(userSid, configuration.DeveloperUserSids);
        await using var pipeServer = new WindowsPrivilegedPipeServer(configuration.ToPipeConfiguration(authorizedClientSids), exception =>
            Console.Error.WriteLine($"Privileged Helper pipe request failed: {exception.GetType().Name}: {exception.Message}"));
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
        pipeServer.Start();
        // The kernel refuses an unauthorized caller before authentication, so the rejected client
        // learns only "access denied" while this process sees nothing at all. Publishing the
        // effective identities is what makes that denial diagnosable.
        Console.Error.WriteLine($"RelaxKonOS Privileged Helper console host is listening on '{configuration.PipeName}'. "
            + $"Authorized client SIDs: {string.Join(", ", authorizedClientSids)} (plus LocalSystem and local Administrators). Press Ctrl+C to stop.");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private static string FindConfigPath(string[] args)
    {
        var index = Array.FindIndex(args, argument => string.Equals(argument, "--config", StringComparison.Ordinal));
        if (index < 0 || index + 1 >= args.Length)
            throw new InvalidOperationException("--config is required for the Windows Helper console host.");
        return Path.GetFullPath(args[index + 1]);
    }
}

/// <summary>
/// Separate from the deployment configuration on purpose: production helper configuration does
/// not contain this opt-in flag and therefore cannot accidentally enable an interactive host.
/// </summary>
/// <param name="DeveloperUserSids">
/// Optional console-only allowlist of extra client identities (SID strings or account names) that
/// may connect to the pipe. The identity running the Helper is always authorized; list the Server's
/// account here when it runs under a different account than the elevated Helper.
/// </param>
[SupportedOSPlatform("windows")]
public sealed record WindowsHelperConsoleConfiguration(string PipeName, string SharedSecret,
    IReadOnlyList<string> FileAllowedRoots, IReadOnlyList<string> AllowedServiceIds, bool AllowConsoleDebug = false,
    IReadOnlyList<string>? DeveloperUserSids = null)
{
    public void Validate()
    {
        if (!AllowConsoleDebug)
            throw new InvalidOperationException("Console debugging is disabled by this configuration.");
        if (string.IsNullOrWhiteSpace(PipeName) || PipeName.Length > 128
            || FileAllowedRoots.Count == 0 || FileAllowedRoots.Any(root => string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            || AllowedServiceIds.Count == 0 || AllowedServiceIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256)
            || DeveloperUserSids is { Count: > DeveloperUserSidAllowList.MaximumEntries })
            throw new InvalidOperationException("Windows Helper console configuration is incomplete.");
        if (Convert.FromBase64String(SharedSecret).Length < 32)
            throw new InvalidOperationException("Windows Helper console secret is too short.");
    }

    internal WindowsHelperPipeConfiguration ToPipeConfiguration(IReadOnlyList<string> authorizedClientSids)
        => new(PipeName, SharedSecret, FileAllowedRoots, AllowedServiceIds, DeveloperUserSids: authorizedClientSids);
}
