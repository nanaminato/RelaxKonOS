using System.Net;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Guardian.Agent;

/// <summary>Machine-owned configuration. Environment variables override this file for container and service deployments.</summary>
internal sealed record GuardianAgentOptions(
    string PipeName,
    string SharedSecret,
    string DataDirectory,
    ProtectedServerMonitorOptions ProtectedServerMonitor)
{
    public static GuardianAgentOptions Load(string[] args)
    {
        var config = LoadMachineConfiguration(args);
        var dataDirectory = Environment.GetEnvironmentVariable("REMOTEOS_GUARDIAN_DATA_DIR")
            ?? config.DataDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        var monitor = config.ProtectedServerMonitor ?? new ProtectedServerMonitorOptions();
        monitor = monitor with
        {
            ServiceName = Environment.GetEnvironmentVariable("REMOTEOS_GUARDIAN_SERVER_SERVICE") ?? monitor.ServiceName,
            HealthUrl = Environment.GetEnvironmentVariable("REMOTEOS_GUARDIAN_SERVER_HEALTH_URL") ?? monitor.HealthUrl,
        };
        return new GuardianAgentOptions(
            Environment.GetEnvironmentVariable("REMOTEOS_GUARDIAN_PIPE") ?? config.PipeName ?? "remoteos-guardian",
            Environment.GetEnvironmentVariable("REMOTEOS_GUARDIAN_SHARED_SECRET") ?? config.SharedSecret ?? string.Empty,
            dataDirectory, monitor);
    }

    private static GuardianMachineConfiguration LoadMachineConfiguration(string[] args)
    {
        var configuredPath = TryGetArgument(args, "--config") ?? Environment.GetEnvironmentVariable("REMOTEOS_GUARDIAN_CONFIG");
        var path = configuredPath ?? Path.Combine(AppContext.BaseDirectory, "guardian.json");
        if (!File.Exists(path)) return new GuardianMachineConfiguration();
        try
        {
            return JsonSerializer.Deserialize<GuardianMachineConfiguration>(File.ReadAllText(path), RemoteOsJsonOptions.Default)
                   ?? new GuardianMachineConfiguration();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Guardian configuration '{path}' is invalid.", exception);
        }
    }

    private static string? TryGetArgument(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }
}

internal sealed record GuardianMachineConfiguration(
    string? PipeName = null,
    string? SharedSecret = null,
    string? DataDirectory = null,
    ProtectedServerMonitorOptions? ProtectedServerMonitor = null);

/// <summary>
/// Installer-owned monitor for the RelaxKonOS Server service. It is deliberately separate
/// from user workloads and only accepts a loopback health endpoint plus a service-safe name.
/// </summary>
internal sealed record ProtectedServerMonitorOptions(
    string? ServiceName = null,
    string? HealthUrl = null,
    int IntervalSeconds = 15,
    int TimeoutSeconds = 5,
    int FailureThreshold = 3)
{
    public bool IsEnabled => IsServiceName(ServiceName) && IsLoopbackHttpUrl(HealthUrl)
        && IntervalSeconds is >= 1 and <= 3600
        && TimeoutSeconds is >= 1 and <= 60
        && FailureThreshold is >= 1 and <= 100;

    private static bool IsServiceName(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsLoopbackHttpUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address));
    }
}
