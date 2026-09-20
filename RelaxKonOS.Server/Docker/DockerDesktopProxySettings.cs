using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Docker Desktop ignores proxies configured in <c>daemon.json</c> and reads its own per-user JSON
/// settings file instead, so that file is the only supported way to route pulls through a proxy on
/// Windows. The document is edited in place: unknown keys, including settings the operator changed
/// in the Docker Desktop UI, must survive.
/// </summary>
internal static class DockerDesktopProxySettings
{
    /// <summary>Docker Desktop 4.34 renamed the file; older installations still use the old name.</summary>
    private static readonly string[] FileNames = ["settings-store.json", "settings.json"];
    private const string ProxyModeKey = "ProxyHTTPMode";
    private const string HttpProxyKey = "OverrideProxyHTTP";
    private const string HttpsProxyKey = "OverrideProxyHTTPS";
    private const string ExcludeKey = "OverrideProxyExclude";
    internal const string ManualMode = "manual";
    internal const string SystemMode = "system";

    /// <summary>
    /// Finds the settings file of the Docker Desktop installation in use. The Server may run as a
    /// service account, so the interactive user's profile is resolved from the machine profile list
    /// when the process's own profile has no Docker Desktop file.
    /// </summary>
    internal static string? ResolveSettingsPath()
    {
        var candidates = new List<string>();
        var current = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(current))
            candidates.AddRange(FileNames.Select(name => Path.Combine(current, "Docker", name)));
        candidates.AddRange(InteractiveUserCandidates());

        var existing = candidates.Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(SafeLastWriteTime)
            .ToArray();
        if (existing.Length > 0) return existing[0];

        // No installation was found, but a Docker Desktop install may still be pending. Only the
        // process's own profile is a safe guess; a profile we do not own must never be created.
        var primary = string.IsNullOrWhiteSpace(current) ? null : Path.Combine(current, "Docker", FileNames[0]);
        return primary is not null && Directory.Exists(Path.GetDirectoryName(primary)) ? primary : null;
    }

    private static IEnumerable<string> InteractiveUserCandidates()
    {
        foreach (var profile in ReadProfileImagePaths())
            foreach (var name in FileNames)
            {
                // A profile path the process cannot combine is skipped rather than aborting the
                // whole probe; one malformed entry must not hide a usable installation.
                string? candidate = null;
                try { candidate = Path.Combine(profile, "AppData", "Roaming", "Docker", name); }
                catch (ArgumentException) { /* A malformed profile path is not a candidate. */ }
                if (candidate is not null) yield return candidate;
            }
    }

    /// <summary>
    /// Profiles of interactive users, read from the machine's profile list. The Server may run as a
    /// service account whose own profile holds no Docker Desktop settings, so the installation of
    /// the signed-in operator has to be located through this list instead.
    /// </summary>
    private static string[] ReadProfileImagePaths()
    {
        // The analyzer cannot see that Registry is Windows-only, and this assembly also runs on
        // Linux, so the platform check has to be explicit here rather than in the caller.
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (key is null) return [];
            // An explicit loop rather than a query: the platform analyzer loses the Windows guard
            // once the body is captured in a lambda, and would flag the registry access.
            var names = key.GetSubKeyNames();
            var paths = new List<string>(names.Length);
            foreach (var name in names)
            {
                using var profile = key.OpenSubKey(name);
                if (profile?.GetValue("ProfileImagePath") is string path && path.Length > 0) paths.Add(path);
            }
            return [.. paths];
        }
        catch (System.Security.SecurityException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static DateTime SafeLastWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>
    /// Writes the proxy the daemon should use. A file that cannot be parsed is left untouched:
    /// overwriting it would destroy settings this feature does not understand.
    /// </summary>
    internal static bool TryWrite(string path, string httpProxy, string httpsProxy, string noProxy, bool enabled, out string problemCode)
    {
        problemCode = string.Empty;
        JsonObject document;
        try
        {
            var content = File.Exists(path) ? File.ReadAllText(path) : "{}";
            document = string.IsNullOrWhiteSpace(content)
                ? new JsonObject()
                : JsonNode.Parse(content) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException) { problemCode = DockerProxyProblem.DesktopSettingsInvalid; return false; }
        catch (IOException) { problemCode = DockerProxyProblem.DesktopSettingsUnreadable; return false; }
        catch (UnauthorizedAccessException) { problemCode = DockerProxyProblem.DesktopSettingsUnreadable; return false; }

        if (enabled)
        {
            document[ProxyModeKey] = ManualMode;
            document[HttpProxyKey] = httpProxy;
            document[HttpsProxyKey] = httpsProxy;
            if (noProxy.Length > 0) document[ExcludeKey] = noProxy;
            else document.Remove(ExcludeKey);
        }
        else
        {
            // Restoring system mode removes the overrides rather than pointing them at an empty
            // string, which Docker Desktop would treat as a configured but unusable proxy.
            document[ProxyModeKey] = SystemMode;
            document.Remove(HttpProxyKey);
            document.Remove(HttpsProxyKey);
            document.Remove(ExcludeKey);
        }

        var temporary = path + ".relaxkonos-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (IOException) { problemCode = DockerProxyProblem.DesktopSettingsLocked; return false; }
        catch (UnauthorizedAccessException) { problemCode = DockerProxyProblem.DesktopSettingsDenied; return false; }
        finally { if (File.Exists(temporary)) TryDelete(temporary); }
    }

    /// <summary>
    /// Reads the proxy Docker Desktop currently has stored, or null when the file is absent,
    /// unreadable, not JSON, or holds no proxy keys. A read failure is reported as "nothing to
    /// show" rather than as an error: this is a diagnostic view of another application's file.
    /// </summary>
    internal static DockerDesktopProxyDto? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var content = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(content) || JsonNode.Parse(content) is not JsonObject document) return null;
            return new DockerDesktopProxyDto(
                ReadString(document, ProxyModeKey),
                ReadString(document, HttpProxyKey),
                ReadString(document, HttpsProxyKey),
                ReadString(document, ExcludeKey),
                path);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Docker Desktop stores these as strings; any other JSON type is not a proxy value.</summary>
    private static string ReadString(JsonObject document, string key) =>
        document.TryGetPropertyValue(key, out var node)
        && node is not null && node.GetValueKind() == JsonValueKind.String
            ? node.GetValue<string>()
            : string.Empty;

    /// <summary>
    /// Stops or starts Docker Desktop through its own CLI. Docker Desktop rewrites its settings file
    /// while shutting down, so the caller must stop it before writing and start it afterwards.
    /// </summary>
    internal static Task<bool> RunDesktopCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken) =>
        DockerDesktopCli.RunAsync(command, timeout, cancellationToken);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* A leftover temporary file is harmless. */ }
        catch (UnauthorizedAccessException) { /* A leftover temporary file is harmless. */ }
    }
}
