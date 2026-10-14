using Microsoft.Extensions.Configuration;

namespace RelaxKonOS.Server.ProcessGuardian;

/// <summary>Host-local deployment check; never prints configuration or IPC credentials.</summary>
public static class GuardianHealthCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var configuration = new ConfigurationBuilder();
            if (args.Length == 3 && args[1] == "--host-config")
                configuration.AddJsonFile(Path.GetFullPath(args[2]), optional: false);
            else if (args.Length == 3 && args[1] == "--environment-file")
                configuration.AddInMemoryCollection(File.ReadLines(args[2])
                    .Where(line => line.StartsWith("GuardianAgent__", StringComparison.Ordinal))
                    .Select(line => line.Split('=', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0].Replace("__", ":"), parts => (string?)parts[1]));
            else if (args.Length != 1) return 64;
            configuration.AddEnvironmentVariables();
            var options = configuration.Build().GetSection("GuardianAgent").Get<GuardianAgentOptions>() ?? new();
            if (!options.Enabled) return 0;
            var service = new NamedPipeProcessGuardianService(options);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var status = await service.GetStatusAsync(timeout.Token);
            return status is { IsInstalled: true, IsRunning: true } ? 0 : 70;
        }
        catch { return 70; }
    }
}
