using System.Net.Sockets;
using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// systemd entry point for the persistent Linux Helper. The process runs as root, owns the Unix
/// socket, and keeps the same closed operation dispatcher the one-shot worker used. It is not a
/// remote shell and exposes no TCP listener.
/// </summary>
internal static class LinuxPrivilegedHelperService
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Log("event=startup-failed reason=linux-required");
            return 70;
        }
        var configurationPath = ValueAfter(args, "--config");
        if (string.IsNullOrWhiteSpace(configurationPath) || !OperatingSystem.IsLinux())
        {
            Log("event=startup-failed reason=configuration-required");
            return 64;
        }
        try
        {
            var fileSystem = new LinuxUnixSocketFileSystem();
            configurationPath = Path.GetFullPath(configurationPath);
            VerifyConfigurationFile(fileSystem, configurationPath);
            var configuration = LinuxPrivilegedHelperConfiguration.Load(configurationPath);
            if (fileSystem.EffectiveUserId != 0)
            {
                Log("event=startup-failed reason=root-required");
                return 77;
            }

            // The operation policy is read from the same root-owned installation files as the
            // one-shot worker. A missing or empty list fails closed instead of allowing arbitrary
            // root file access.
            var policy = PrivilegedOperationExecutor.LoadHostPolicy();
            using var stopping = new CancellationTokenSource();
            using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                stopping.Cancel();
            });
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                stopping.Cancel();
            });
            await using var server = new LinuxUnixSocketPrivilegedServer(configuration,
                (request, progress) => PrivilegedOperationExecutor.ExecuteAsync(request, policy, progress),
                fileSystem, new LinuxPeerAuthorizer(configuration.AllowedUserIds), Log);
            server.Start();
            try { await Task.Delay(Timeout.Infinite, stopping.Token); }
            catch (OperationCanceledException) { }
            await server.StopAsync();
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Text.Json.JsonException or SocketException or FormatException)
        {
            // Startup failures are deliberately short and non-secret. systemd records the exit code
            // and restarts according to the unit's Restart policy.
            Log($"event=startup-failed error={exception.GetType().Name}");
            return 69;
        }
    }

    private static string? ValueAfter(string[] args, string option)
    {
        var index = Array.FindIndex(args, argument => string.Equals(argument, option, StringComparison.Ordinal));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void VerifyConfigurationFile(IUnixSocketFileSystem fileSystem, string path)
    {
        var status = fileSystem.Stat(path)
            ?? throw new IOException("The privileged helper configuration is missing.");
        if (status.IsDirectory || status.IsSymbolicLink || status.IsSocket)
            throw new InvalidOperationException("The privileged helper configuration is not a regular file.");
        if (status.OwnerId != 0)
            throw new UnauthorizedAccessException("The privileged helper configuration is not owned by root.");
        if ((status.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new UnauthorizedAccessException("The privileged helper configuration is writable by another account.");
    }

    private static void Log(string message) => Console.Error.WriteLine("relaxkonos-privileged-helper: " + message);
}
