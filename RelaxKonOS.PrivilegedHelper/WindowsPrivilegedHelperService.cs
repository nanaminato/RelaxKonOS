using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Runtime.Versioning;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Production LocalSystem host. Transport and privileged operations live outside the service lifecycle.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrivilegedHelperService : ServiceBase
{
    private readonly WindowsHelperServiceConfiguration _configuration;
    private WindowsPrivilegedPipeServer? _pipeServer;

    private WindowsPrivilegedHelperService(WindowsHelperServiceConfiguration configuration)
    {
        ServiceName = "RemoteOSPrivilegedHelper";
        CanStop = true;
        AutoLog = true;
        _configuration = configuration;
    }

    public static void Run(string[] args)
    {
        var pathIndex = Array.FindIndex(args, argument => string.Equals(argument, "--config", StringComparison.Ordinal));
        if (pathIndex < 0 || pathIndex + 1 >= args.Length) throw new InvalidOperationException("--config is required for the Windows Helper service.");
        var path = Path.GetFullPath(args[pathIndex + 1]);
        var configuration = JsonSerializer.Deserialize<WindowsHelperServiceConfiguration>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Windows Helper configuration is invalid.");
        configuration.Validate();
        configuration.VerifyCurrentExecutable();
        ServiceBase.Run(new WindowsPrivilegedHelperService(configuration));
    }

    protected override void OnStart(string[] args)
    {
        _pipeServer = new WindowsPrivilegedPipeServer(_configuration.ToPipeConfiguration(), exception =>
            EventLog.WriteEntry(ServiceName, $"Privileged Helper pipe request failed: {exception.GetType().Name}", EventLogEntryType.Warning));
        _pipeServer.Start();
    }

    protected override void OnStop()
    {
        if (_pipeServer is not null) _pipeServer.StopAsync().GetAwaiter().GetResult();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _pipeServer is not null) _pipeServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
}

[SupportedOSPlatform("windows")]
public sealed record WindowsHelperServiceConfiguration(string PipeName, string SharedSecret, string ServerServiceSid,
    IReadOnlyList<string> FileAllowedRoots, IReadOnlyList<string> AllowedServiceIds, string HelperExecutableSha256)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PipeName) || PipeName.Length > 128 || string.IsNullOrWhiteSpace(ServerServiceSid)
            || FileAllowedRoots.Count == 0 || FileAllowedRoots.Any(root => string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            || AllowedServiceIds.Count == 0 || AllowedServiceIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256)
            || string.IsNullOrWhiteSpace(HelperExecutableSha256) || !System.Text.RegularExpressions.Regex.IsMatch(HelperExecutableSha256, "^[0-9a-fA-F]{64}$"))
            throw new InvalidOperationException("Windows Helper configuration is incomplete.");
        if (Convert.FromBase64String(SharedSecret).Length < 32) throw new InvalidOperationException("Windows Helper secret is too short.");
        _ = new SecurityIdentifier(ServerServiceSid);
    }

    internal WindowsHelperPipeConfiguration ToPipeConfiguration()
        => new(PipeName, SharedSecret, FileAllowedRoots, AllowedServiceIds, ServerServiceSid);

    public void VerifyCurrentExecutable()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException("Windows Helper executable is unavailable.");
        var expected = Convert.FromHexString(HelperExecutableSha256);
        using var stream = File.OpenRead(executable);
        var actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidOperationException("Windows Helper executable integrity verification failed.");
    }
}
