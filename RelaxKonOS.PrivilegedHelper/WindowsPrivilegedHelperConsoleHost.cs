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

        var userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        await using var pipeServer = new WindowsPrivilegedPipeServer(configuration.ToPipeConfiguration(userSid), exception =>
            Console.Error.WriteLine($"Privileged Helper pipe request failed: {exception.GetType().Name}"));
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
        pipeServer.Start();
        Console.Error.WriteLine($"RelaxKonOS Privileged Helper console host is listening on '{configuration.PipeName}'. Press Ctrl+C to stop.");
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
[SupportedOSPlatform("windows")]
public sealed record WindowsHelperConsoleConfiguration(string PipeName, string SharedSecret,
    IReadOnlyList<string> FileAllowedRoots, IReadOnlyList<string> AllowedServiceIds, bool AllowConsoleDebug = false)
{
    public void Validate()
    {
        if (!AllowConsoleDebug)
            throw new InvalidOperationException("Console debugging is disabled by this configuration.");
        if (string.IsNullOrWhiteSpace(PipeName) || PipeName.Length > 128
            || FileAllowedRoots.Count == 0 || FileAllowedRoots.Any(root => string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            || AllowedServiceIds.Count == 0 || AllowedServiceIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256))
            throw new InvalidOperationException("Windows Helper console configuration is incomplete.");
        if (Convert.FromBase64String(SharedSecret).Length < 32)
            throw new InvalidOperationException("Windows Helper console secret is too short.");
    }

    internal WindowsHelperPipeConfiguration ToPipeConfiguration(string developerUserSid)
        => new(PipeName, SharedSecret, FileAllowedRoots, AllowedServiceIds, DeveloperUserSid: developerUserSid);
}
