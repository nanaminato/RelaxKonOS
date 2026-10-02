using System.Diagnostics;
using System.Text;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.PrivilegedHelper;

internal static class LinuxSystemProxyOperations
{
    private const string Journal = "/var/lib/relaxkonos-system-proxy/recovery.json";
    internal static readonly SettingsTarget EnvironmentTarget = new("host/environment/machine", SettingsScope.HostMachine);

    public static PrivilegedOperationResult Execute(PrivilegedOperationRequest request)
    {
        var clean = new PrivilegedOperationRequest(request.Operation, LinuxSystemProxy: request.LinuxSystemProxy,
            OperationId: request.OperationId, Correlation: request.Correlation);
        var read = request.Operation == PrivilegedOperationKind.LinuxSystemProxyRead;
        if (clean != request || read && request.LinuxSystemProxy is not null || !read && request.LinuxSystemProxy is null)
            return Failure(PrivilegedProblemCode.InvalidRequest);
        // Share the environment provider's mutex across the entire synchronous transaction.
        using var mutex = new Mutex(false, "RelaxKonOS.Environment." + SettingsRevisions.Hash(EnvironmentTarget.ResourceId));
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return Failure(PrivilegedProblemCode.TimedOut);
            var backend = new LinuxSystemProxyBackend();
            if (read || request.LinuxSystemProxy!.Enabled)
            {
                var environment = LinuxEnvironmentOperations.Execute(new(PrivilegedOperationKind.HostEnvironmentRead, EnvironmentTarget: EnvironmentTarget));
                var desktops = backend.DesktopTargets();
                var supported = environment.Success;
                var capabilities = new ProxySystemProxyCapabilities(supported, false, environment.Success, desktops.Count > 0);
                if (read) return new(true, SystemProxyCapabilities: capabilities);
                if (!supported) return Failure(PrivilegedProblemCode.UnsupportedOperation);
            }
            new LinuxSystemProxyTransaction(Journal, backend).Apply(request.LinuxSystemProxy!);
            return new(true);
        }
        catch (ArgumentException) { return Failure(PrivilegedProblemCode.InvalidRequest); }
        catch (UnauthorizedAccessException) { return Failure(PrivilegedProblemCode.AccessDenied); }
        catch (NotSupportedException) { return Failure(PrivilegedProblemCode.UnsupportedOperation); }
        catch (TimeoutException) { return Failure(PrivilegedProblemCode.TimedOut); }
        catch (System.ComponentModel.Win32Exception) { return Failure(PrivilegedProblemCode.HelperUnavailable); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        { return Failure(PrivilegedProblemCode.Conflict); }
        finally { if (held) mutex.ReleaseMutex(); }
    }
    private static PrivilegedOperationResult Failure(PrivilegedProblemCode code) => new(false, 1, ProblemCode: code);
}

/// <summary>Only fixed OS tools and closed proxy keys. Desktop commands run as the owning user, never as root.</summary>
internal sealed class LinuxSystemProxyBackend : ILinuxSystemProxyBackend
{
    private static readonly LinuxSystemProxyConfiguration Sample = new(true, "127.0.0.1", 7890, true, "");
    public IReadOnlyList<LinuxProxyTarget> DesktopTargets()
    {
        if (!File.Exists("/usr/bin/loginctl")) return [];
        var sessions = Run("/usr/bin/loginctl", ["list-sessions", "--no-legend", "--no-pager"], allowFailure: true);
        if (sessions is null) return [];
        var targets = new List<LinuxProxyTarget>();
        foreach (var line in sessions.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var session = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (session is null || session.Length > 64 || session.Any(c => !char.IsAsciiLetterOrDigit(c))) continue;
            var output = Run("/usr/bin/loginctl", ["show-session", session, "--no-pager", "--property=Type", "--property=Class",
                "--property=Remote", "--property=Desktop", "--property=User", "--property=State"], allowFailure: true);
            if (output is null) continue;
            var properties = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => value.Split('=', 2))
                .Where(parts => parts.Length == 2).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            if (properties.GetValueOrDefault("Type") is not ("x11" or "wayland") || properties.GetValueOrDefault("Class") != "user"
                || properties.GetValueOrDefault("Remote") != "no" || properties.GetValueOrDefault("State") == "closing"
                || !uint.TryParse(properties.GetValueOrDefault("User"), out var uid) || uid == 0) continue;
            var desktop = properties.GetValueOrDefault("Desktop", "").ToLowerInvariant();
            var provider = desktop.Contains("gnome") || desktop.Contains("ubuntu") ? "gnome"
                : desktop.Contains("kde") || desktop.Contains("plasma") ? "kde" : null;
            if (provider is null) continue;
            RequireTools(provider);
            var user = User(uid, provider);
            if (!targets.Contains(user)) targets.Add(user);
            if (targets.Count > 16) throw new NotSupportedException("Too many local desktop users.");
        }
        return targets;
    }

    public Dictionary<string, string?> Read(LinuxProxyTarget target, IEnumerable<string> keys)
    {
        ValidateTarget(target, keys);
        if (target.Provider == "environment")
        {
            var state = EnvironmentRead();
            var values = state.Values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
            return keys.ToDictionary(key => key, key => values.GetValueOrDefault(key), StringComparer.Ordinal);
        }
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (target.Provider == "gnome")
            {
                var value = UserRun(target, "/usr/bin/dconf", ["read", "/system/proxy/" + key]);
                result[key] = value.Length == 0 ? null : value;
            }
            else
            {
                const string absent = "__RelaxKonOS_Absent_Proxy_Key__";
                var value = UserRun(target, KdeTool("kreadconfig"), ["--file", "kioslaverc", "--group", "Proxy Settings", "--key", key, "--default", absent]);
                result[key] = value == absent ? null : value;
            }
        }
        return result;
    }

    public void Write(LinuxProxyTarget target, IReadOnlyDictionary<string, string?> values)
    {
        ValidateTarget(target, values.Keys);
        if (values.Count == 0) return;
        if (target.Provider == "environment")
        {
            var state = EnvironmentRead();
            var changes = values.Select(pair => new EnvironmentMutation(pair.Key,
                pair.Value is null ? EnvironmentMutationKind.Delete : EnvironmentMutationKind.Set, pair.Value)).ToArray();
            var result = LinuxEnvironmentOperations.ExecuteForPath(new(PrivilegedOperationKind.HostEnvironmentApply,
                EnvironmentTarget: LinuxSystemProxyOperations.EnvironmentTarget, ExpectedRevision: state.Revision,
                EnvironmentChange: new(changes, ConfirmHighImpact: true)), LinuxEnvironmentOperations.EnvironmentPath, true);
            if (!result.Success) throw new IOException("Conditional proxy environment write failed.");
            return;
        }
        // Activate a new manual proxy only after its addresses and bypasses are written; restore
        // the previous mode first so teardown does not temporarily direct traffic to stale values.
        var activation = target.Provider == "gnome" ? "mode" : "ProxyType";
        var enabling = values.GetValueOrDefault(activation) is "'manual'" or "1";
        foreach (var pair in values.OrderBy(pair => pair.Key == activation ? enabling ? 1 : -1 : 0))
        {
            if (target.Provider == "gnome")
            {
                if (pair.Value is null) UserRun(target, "/usr/bin/dconf", ["reset", "/system/proxy/" + pair.Key]);
                else UserRun(target, "/usr/bin/dconf", ["write", "/system/proxy/" + pair.Key, pair.Value]);
            }
            else
            {
                var arguments = new List<string> { "--file", "kioslaverc", "--group", "Proxy Settings", "--key", pair.Key };
                if (pair.Value is null) arguments.Add("--delete"); else arguments.Add(pair.Value);
                UserRun(target, KdeTool("kwriteconfig"), arguments);
            }
        }
    }

    public void Notify(LinuxProxyTarget target)
    {
        if (target.Provider == "kde" && File.Exists($"/run/user/{target.UserId}/bus"))
            UserRun(target, "/usr/bin/dbus-send", ["--session", "--type=signal", "/KIO/Scheduler",
                "org.kde.KIO.Scheduler.reparseSlaveConfiguration", "string:"]);
        // dconf writes notify GNOME listeners through the user's session bus.
    }

    private static PrivilegedEnvironmentState EnvironmentRead()
    {
        // Restoring our own snapshot must remain possible after PAM readenv has been disabled.
        var result = LinuxEnvironmentOperations.ExecuteForPath(new(PrivilegedOperationKind.HostEnvironmentRead,
            EnvironmentTarget: LinuxSystemProxyOperations.EnvironmentTarget), LinuxEnvironmentOperations.EnvironmentPath, true);
        return result.Success && result.HostEnvironment is { } state ? state : throw new IOException("Proxy environment read failed.");
    }

    private static void ValidateTarget(LinuxProxyTarget target, IEnumerable<string> keys)
    {
        var allowed = LinuxSystemProxyTransaction.Values(target.Provider, Sample).Keys.ToHashSet(StringComparer.Ordinal);
        if (keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("Unknown proxy key.");
        if (target.Provider == "environment")
        {
            if (target != new LinuxProxyTarget("environment")) throw new ArgumentException("Invalid environment target.");
            return;
        }
        RequireTools(target.Provider);
        if (target.UserId == 0 || User(target.UserId, target.Provider) != target) throw new UnauthorizedAccessException();
    }

    private static LinuxProxyTarget User(uint uid, string provider)
    {
        var fields = Run("/usr/bin/getent", ["passwd", uid.ToString()])!.Split(':');
        if (fields.Length != 7 || fields[2] != uid.ToString() || fields[0].Length == 0 || !Path.IsPathFullyQualified(fields[5]))
            throw new UnauthorizedAccessException();
        return new(provider, uid, fields[0], fields[5]);
    }

    private static void RequireTools(string provider)
    {
        var tools = provider == "gnome" ? new[] { "/usr/bin/dconf", "/usr/bin/dbus-run-session" }
            : new[] { KdeTool("kreadconfig"), KdeTool("kwriteconfig"), "/usr/bin/dbus-send", "/usr/bin/dbus-run-session" };
        if (!tools.All(File.Exists) || !File.Exists(RunUser)) throw new NotSupportedException("Desktop proxy tools are unavailable.");
    }
    private static string RunUser => File.Exists("/usr/sbin/runuser") ? "/usr/sbin/runuser" : "/usr/bin/runuser";
    private static string KdeTool(string name) => File.Exists("/usr/bin/kreadconfig6") && File.Exists("/usr/bin/kwriteconfig6")
        ? "/usr/bin/" + name + "6" : "/usr/bin/" + name + "5";

    private static string UserRun(LinuxProxyTarget user, string executable, IEnumerable<string> arguments)
    {
        var command = new List<string> { "-u", user.UserName, "--", "/usr/bin/env", "-i", "HOME=" + user.Home,
            "USER=" + user.UserName, "LOGNAME=" + user.UserName, "PATH=/usr/bin:/bin", "XDG_CONFIG_HOME=" + Path.Combine(user.Home, ".config") };
        var runtime = $"/run/user/{user.UserId}";
        if (Directory.Exists(runtime)) command.Add("XDG_RUNTIME_DIR=" + runtime);
        if (File.Exists(runtime + "/bus")) command.Add("DBUS_SESSION_BUS_ADDRESS=unix:path=" + runtime + "/bus");
        else { command.Add("/usr/bin/dbus-run-session"); command.Add("--"); }
        command.Add(executable); command.AddRange(arguments);
        return Run(RunUser, command)!;
    }

    private static string? Run(string executable, IEnumerable<string> arguments, bool allowFailure = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        TrustedProcessEnvironment.Apply(start);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Proxy tool could not start.");
        var output = BoundedRead(process.StandardOutput);
        var error = BoundedRead(process.StandardError);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { process.Kill(true); process.WaitForExit(); throw new TimeoutException(); }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        if (process.ExitCode != 0) return allowFailure ? null : throw new IOException("Proxy tool failed.");
        return output.Result.TrimEnd('\r', '\n');
    }
    private static async Task<string> BoundedRead(StreamReader reader)
    {
        var buffer = new char[4096]; var result = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            if (result.Length + count > 65536) throw new IOException("Proxy tool output was too large.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
