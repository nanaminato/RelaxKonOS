using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;

namespace RelaxKonOS.Client.Apps.PortForwarding;

/// <summary>Starts and supervises loopback-only <c>ssh -L</c> processes on the Client host.</summary>
public sealed class PortForwardingService : IPortForwardingService
{
    private const int StartupWaitMilliseconds = 650;
    private readonly IAuthSession _session;
    private readonly PortForwardingSettingsStore _settingsStore;
    private readonly ConcurrentDictionary<Guid, RunningForward> _forwards = new();
    private PortForwardingSettings _settings;

    public PortForwardingService(IAuthSession session, PortForwardingSettingsStore settingsStore)
    {
        _session = session;
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();
    }

    public event EventHandler? ForwardsChanged;

    public IReadOnlyList<PortForwardInfo> List() => _forwards.Values
        .Select(forward => forward.Info)
        .OrderBy(forward => forward.LocalPort)
        .ToArray();

    public PortForwardingSettings GetSettings() => _settings;

    public void SaveSettings(PortForwardingSettings settings)
    {
        _settings = settings.Normalize();
        _settingsStore.Save(_settings);
    }

    public Task<PortForwardInfo> StartAsync(PortForwardRequest request, CancellationToken cancellationToken = default)
        => StartAsync(request, password: null, cancellationToken);

    public async Task<PortForwardInfo> StartAsync(PortForwardRequest request, string? password, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var existing = _forwards.Values.FirstOrDefault(forward =>
            forward.Info.RemotePort == request.RemotePort
            && forward.Info.RemoteHost.Equals(request.RemoteHost, StringComparison.OrdinalIgnoreCase)
            && forward.Info.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // A URL path does not alter the SSH process, but it is part of the URL returned to
            // callers. Keep the in-memory list in sync when a caller reuses the same forward.
            var updatedInfo = existing.Info with { PathAndQuery = request.PathAndQuery };
            if (!StringComparer.Ordinal.Equals(existing.Info.PathAndQuery, updatedInfo.PathAndQuery))
            {
                _forwards.TryUpdate(updatedInfo.Id, existing with { Info = updatedInfo }, existing);
                RaiseChanged();
            }
            return updatedInfo;
        }

        var server = ResolveSshServer();
        var localPort = FindAvailablePort(request.PreferredLocalPort ?? request.RemotePort);
        var launch = CreateSshProcess(server, request, localPort, password);
        var process = launch.Process;
        Task<string>? standardErrorRead = null;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException(LocalizedText.Get("port_forwarding.error.ssh_start_failed"));
            standardErrorRead = process.StandardError.ReadToEndAsync();
            // Password authentication must not fall back to an interactive terminal. The temporary
            // askpass program provides the password only to this SSH child process.
            process.StandardInput.Close();
        }
        catch
        {
            Stop(process);
            DeleteAskPassHelper(launch.AskPassHelperPath);
            throw;
        }
        try { await Task.Delay(StartupWaitMilliseconds, cancellationToken); }
        catch (OperationCanceledException)
        {
            Stop(process);
            DeleteAskPassHelper(launch.AskPassHelperPath);
            throw;
        }
        if (process.HasExited)
        {
            var standardError = standardErrorRead is null ? string.Empty : await standardErrorRead;
            process.Dispose();
            DeleteAskPassHelper(launch.AskPassHelperPath);
            throw new InvalidOperationException(DescribeSshStartupFailure(standardError));
        }

        var id = Guid.NewGuid();
        var info = new PortForwardInfo(id, request.RemoteHost, request.RemotePort, localPort,
            request.Scheme, request.PathAndQuery, DateTimeOffset.UtcNow, LocalizedText.Get("port_forwarding.status.running"));
        var running = new RunningForward(info, process, launch.AskPassHelperPath);
        if (!_forwards.TryAdd(id, running))
        {
            Stop(process);
            DeleteAskPassHelper(launch.AskPassHelperPath);
            throw new InvalidOperationException(LocalizedText.Get("port_forwarding.error.forward_register_failed"));
        }
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(id, process);
        RaiseChanged();
        return info;
    }

    public Task<PortForwardInfo> UpdateAsync(Guid id, PortForwardRequest request, CancellationToken cancellationToken = default)
        => UpdateAsync(id, request, password: null, cancellationToken);

    public async Task<PortForwardInfo> UpdateAsync(Guid id, PortForwardRequest request, string? password, CancellationToken cancellationToken = default)
    {
        // A modification replaces one owned SSH process. The requested local port is checked
        // again, so an occupied port receives the same predictable fallback as a new request.
        await RemoveAsync(id, cancellationToken);
        return await StartAsync(request, password, cancellationToken);
    }

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_forwards.TryRemove(id, out var running))
        {
            Stop(running.Process);
            DeleteAskPassHelper(running.AskPassHelperPath);
            RaiseChanged();
        }
        return Task.CompletedTask;
    }

    private (string Host, string? User, int Port) ResolveSshServer()
    {
        var host = _settings.SshHost;
        var user = _settings.SshUser;
        if (string.IsNullOrWhiteSpace(host))
        {
            if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } serverUrl }
                || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
                throw new InvalidOperationException(LocalizedText.Get("port_forwarding.error.ssh_host_required"));
            host = serverUri.Host;
        }
        user ??= _session.CurrentUser?.Username;
        if (host.StartsWith("-", StringComparison.Ordinal) || host.Any(char.IsWhiteSpace)
            || user?.StartsWith("-", StringComparison.Ordinal) == true
            || user?.Any(char.IsWhiteSpace) == true)
            throw new InvalidOperationException(LocalizedText.Get("port_forwarding.error.ssh_host_user_invalid"));
        return (host, user, _settings.SshPort);
    }

    private static SshLaunch CreateSshProcess((string Host, string? User, int Port) server, PortForwardRequest request, int localPort, string? password)
    {
        var start = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-N");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(string.IsNullOrEmpty(password) ? "BatchMode=yes" : "BatchMode=no");
        if (!string.IsNullOrEmpty(password))
        {
            var askPassHelperPath = CreateAskPassHelper();
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("NumberOfPasswordPrompts=1");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("PreferredAuthentications=password,keyboard-interactive");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("PubkeyAuthentication=no");
            start.Environment["SSH_ASKPASS"] = askPassHelperPath;
            start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            start.Environment["RELAXKONOS_SSH_ASKPASS_PASSWORD"] = password;
            start.Environment["DISPLAY"] = "relaxkonos-askpass";
            AddForwardArguments(start, server, request, localPort);
            return new SshLaunch(new Process { StartInfo = start }, askPassHelperPath);
        }
        AddForwardArguments(start, server, request, localPort);
        return new SshLaunch(new Process { StartInfo = start }, null);
    }

    private static void AddForwardArguments(ProcessStartInfo start, (string Host, string? User, int Port) server, PortForwardRequest request, int localPort)
    {
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ExitOnForwardFailure=yes");
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-L");
        start.ArgumentList.Add($"127.0.0.1:{localPort}:{request.RemoteHost}:{request.RemotePort}");
        start.ArgumentList.Add(string.IsNullOrWhiteSpace(server.User) ? server.Host : $"{server.User}@{server.Host}");
    }

    private static string CreateAskPassHelper()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RelaxKonOS", "ssh-askpass");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}{(OperatingSystem.IsWindows() ? ".cmd" : ".sh")}");
        var contents = OperatingSystem.IsWindows()
            ? "@echo off\r\npowershell -NoProfile -NonInteractive -Command \"[Console]::Out.Write($env:RELAXKONOS_SSH_ASKPASS_PASSWORD)\"\r\n"
            : "#!/bin/sh\nprintf '%s\\n' \"$RELAXKONOS_SSH_ASKPASS_PASSWORD\"\n";
        File.WriteAllText(path, contents);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (PlatformNotSupportedException) { }
        }
        return path;
    }

    private static void DeleteAskPassHelper(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int FindAvailablePort(int preferred)
    {
        var first = Math.Clamp(preferred, 1, 65535);
        for (var offset = 0; offset <= 65535 - first; offset++)
        {
            var port = first + offset;
            if (CanBind(port)) return port;
        }
        for (var port = 1; port < first; port++)
            if (CanBind(port)) return port;
        throw new InvalidOperationException(LocalizedText.Get("port_forwarding.error.no_local_port"));
    }

    private static bool CanBind(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException) { return false; }
        finally { listener?.Stop(); }
    }

    private static void Validate(PortForwardRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var isLoopbackTarget = request.RemoteHost?.Equals("localhost", StringComparison.OrdinalIgnoreCase) == true
                               || request.RemoteHost?.Equals("127.0.0.1", StringComparison.Ordinal) == true;
        if (!isLoopbackTarget)
            throw new ArgumentException(LocalizedText.Get("port_forwarding.error.remote_loopback_only"), nameof(request));
        if (request.RemotePort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(request), LocalizedText.Get("port_forwarding.error.remote_port_invalid"));
        if (request.PreferredLocalPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(request), LocalizedText.Get("port_forwarding.error.local_port_invalid"));
        var isWebScheme = request.Scheme?.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) == true
                          || request.Scheme?.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) == true;
        if (!isWebScheme)
            throw new ArgumentException(LocalizedText.Get("port_forwarding.error.web_scheme_only"), nameof(request));
        if (string.IsNullOrWhiteSpace(request.PathAndQuery)
            || !request.PathAndQuery.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException(LocalizedText.Get("port_forwarding.error.path_invalid"), nameof(request));
    }

    private static string DescribeSshStartupFailure(string standardError)
    {
        if (standardError.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
            || standardError.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase))
            return LocalizedText.Get("port_forwarding.error.ssh_host_key");
        if (standardError.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return LocalizedText.Get("port_forwarding.error.ssh_authentication");
        if (standardError.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            return LocalizedText.Get("port_forwarding.error.ssh_connection_refused");
        if (standardError.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase))
            return LocalizedText.Get("port_forwarding.error.ssh_host_unresolved");
        if (standardError.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase)
            || standardError.Contains("No route to host", StringComparison.OrdinalIgnoreCase))
            return LocalizedText.Get("port_forwarding.error.ssh_unreachable");
        if (standardError.Contains("ssh_askpass", StringComparison.OrdinalIgnoreCase))
            return LocalizedText.Get("port_forwarding.error.ssh_password_helper");
        return LocalizedText.Get("port_forwarding.error.ssh_startup_failed");
    }

    private void OnProcessExited(Guid id, Process process)
    {
        if (_forwards.TryRemove(id, out var running))
        {
            DeleteAskPassHelper(running.AskPassHelperPath);
            RaiseChanged();
        }
        process.Dispose();
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private void RaiseChanged() => ForwardsChanged?.Invoke(this, EventArgs.Empty);

    private sealed record SshLaunch(Process Process, string? AskPassHelperPath);
    private sealed record RunningForward(PortForwardInfo Info, Process Process, string? AskPassHelperPath);
}
