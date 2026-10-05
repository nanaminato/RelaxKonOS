using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Helper-side validation and closed configuration generation. No plugins or environment templates.</summary>
internal static class WindowsRuntimeConfigurationWriter
{
    private static bool Port(int value) => value is > 0 and <= 65535;
    private static bool Text(string? value, int maximum = 1024) => value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl) && !value.Contains("{{", StringComparison.Ordinal);
    private static bool Host(string? value) => Text(value, 253) && (IPAddress.TryParse(value, out _) || Uri.CheckHostName(value) == UriHostNameType.Dns);
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static bool IsValidRequest(ManagedRuntimeRequest request)
    {
        if (!Enum.IsDefined(request.Runtime) || !Enum.IsDefined(request.Action)) return false;
        ManagedRuntimeRequest? expected = request.Runtime switch
        {
            ManagedRuntime.Nginx => request.Action == ManagedRuntimeAction.Install
                ? new(request.Runtime, request.Action, Version: request.Version, ArchivePath: request.ArchivePath)
                : new(request.Runtime, request.Action),
            ManagedRuntime.Frpc or ManagedRuntime.Frps => request.Action switch
            {
                ManagedRuntimeAction.Install when request.Runtime == ManagedRuntime.Frpc
                    => new(request.Runtime, request.Action, Version: request.Version, ArchivePath: request.ArchivePath),
                ManagedRuntimeAction.Uninstall when request.Runtime == ManagedRuntime.Frpc => new(request.Runtime, request.Action),
                ManagedRuntimeAction.Status or ManagedRuntimeAction.Stop
                    => new(request.Runtime, request.Action, ProfileId: request.Runtime == ManagedRuntime.Frpc ? request.ProfileId : null),
                ManagedRuntimeAction.Start or ManagedRuntimeAction.Test
                    => new(request.Runtime, request.Action, Version: request.Version,
                        ProfileId: request.Runtime == ManagedRuntime.Frpc ? request.ProfileId : null,
                        Client: request.Runtime == ManagedRuntime.Frpc ? request.Client : null,
                        Server: request.Runtime == ManagedRuntime.Frps ? request.Server : null,
                        AppliedIdentity: request.Action == ManagedRuntimeAction.Start ? request.AppliedIdentity : null),
                _ => null,
            },
            _ => null,
        };
        return request == expected && (request.Action != ManagedRuntimeAction.Start || request.Runtime == ManagedRuntime.Nginx
            || request.AppliedIdentity is { Length: > 0 and <= 128 } && Regex.IsMatch(request.AppliedIdentity, "\\A[a-fA-F0-9]+\\z")) && (request.Runtime != ManagedRuntime.Frpc
            || request.Action is ManagedRuntimeAction.Install or ManagedRuntimeAction.Uninstall
            || request.ProfileId is { } id && id != Guid.Empty);
    }

    public static string Client(FrpcServiceConfiguration? config, FrpsServiceConfiguration? unrelated)
    {
        if (config is null || unrelated is not null || !Host(config.Host) || !Port(config.Port) || !Enum.IsDefined(config.TlsMode)
            || config.Token is not null && !Text(config.Token) || config.Proxies is null || config.Proxies.Count > 256) throw new ArgumentException("Invalid managed FRP client configuration.");
        var text = new StringBuilder().AppendLine($"serverAddr = {Quote(config.Host)}").AppendLine($"serverPort = {config.Port}");
        if (config.Token is { } token) text.AppendLine("[auth]").AppendLine("method = \"token\"").AppendLine($"token = {Quote(token)}");
        if (config.TlsMode != TunnelTlsMode.Default) text.AppendLine("[transport.tls]").AppendLine($"enable = {(config.TlsMode == TunnelTlsMode.Force ? "true" : "false")}");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proxy in config.Proxies)
        {
            if (!Text(proxy.Name, 128) || !names.Add(proxy.Name) || !Enum.IsDefined(proxy.Protocol) || !Host(proxy.LocalHost) || !Port(proxy.LocalPort)
                || proxy.RemotePort is { } remote && !Port(remote) || proxy.Domain is not null && !Host(proxy.Domain)) throw new ArgumentException("Invalid managed FRP proxy.");
            text.AppendLine("[[proxies]]").AppendLine($"name = {Quote(proxy.Name)}").AppendLine($"type = {Quote(proxy.Protocol.ToString().ToLowerInvariant())}")
                .AppendLine($"localIP = {Quote(proxy.LocalHost)}").AppendLine($"localPort = {proxy.LocalPort}");
            if (proxy.RemotePort is { } port) text.AppendLine($"remotePort = {port}");
            if (proxy.Domain is { } domain) text.AppendLine($"customDomains = [{Quote(domain)}]");
            if (proxy.Encryption || proxy.Compression)
            {
                text.AppendLine("[proxies.transport]");
                if (proxy.Encryption) text.AppendLine("useEncryption = true");
                if (proxy.Compression) text.AppendLine("useCompression = true");
            }
        }
        return text.ToString();
    }

    public static string Server(FrpsServiceConfiguration? config, FrpcServiceConfiguration? unrelated)
    {
        if (config is null || unrelated is not null || !IPAddress.TryParse(config.BindAddress, out _) || !Port(config.BindPort)
            || !Text(config.Token) || config.AllowPorts is not { Count: > 0 and <= 64 }
            || config.AllowPorts.Any(x => !Port(x.Start) || !Port(x.End) || x.Start > x.End)
            || config.HttpPort is { } http && !Port(http) || config.HttpsPort is { } https && !Port(https)
            || config.DashboardEnabled && (!IPAddress.TryParse(config.DashboardAddress, out _) || config.DashboardPort is not { } dashboard || !Port(dashboard)
                || !Text(config.DashboardUser) || !Text(config.DashboardPassword))) throw new ArgumentException("Invalid managed FRP server configuration.");
        var ports = new[] { config.BindPort, config.HttpPort, config.HttpsPort, config.DashboardEnabled ? config.DashboardPort : null }.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        if (ports.Distinct().Count() != ports.Length) throw new ArgumentException("Duplicate FRP listeners.");
        var text = new StringBuilder().AppendLine($"bindAddr = {Quote(config.BindAddress)}").AppendLine($"bindPort = {config.BindPort}")
            .AppendLine($"auth.token = {Quote(config.Token)}")
            .AppendLine("allowPorts = [" + string.Join(", ", config.AllowPorts.Select(x => $"{{ start = {x.Start}, end = {x.End} }}")) + "]");
        if (config.HttpPort is { } httpPort) text.AppendLine($"vhostHTTPPort = {httpPort}");
        if (config.HttpsPort is { } httpsPort) text.AppendLine($"vhostHTTPSPort = {httpsPort}");
        if (config.ForceTls) text.AppendLine("transport.tls.force = true");
        if (config.DashboardEnabled) text.AppendLine($"webServer.addr = {Quote(config.DashboardAddress)}").AppendLine($"webServer.port = {config.DashboardPort}")
            .AppendLine($"webServer.user = {Quote(config.DashboardUser!)}").AppendLine($"webServer.password = {Quote(config.DashboardPassword!)}");
        return text.ToString();
    }

    public static void ValidateNginx(string text, string root)
    {
        if (text.Length > 1024 * 1024 || text.Contains('\0')) throw new UnauthorizedAccessException();
        var directive = new List<string>(); var token = new StringBuilder(); char quote = '\0'; bool comment = false, escape = false;
        void Flush() { if (token.Length > 0) { directive.Add(token.ToString()); token.Clear(); } }
        void Check()
        {
            Flush();
            if (directive.Count == 0) return;
            var name = directive[0];
            if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$")
                || name is "load_module" or "env" or "ssl_engine" or "ssl_conf_command" or "dav_methods"
                    or "proxy_store" or "fastcgi_store" or "uwsgi_store" or "scgi_store" or "working_directory"
                || name.Contains("lua", StringComparison.OrdinalIgnoreCase) || name.Contains("perl", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("js_", StringComparison.Ordinal)) throw new UnauthorizedAccessException();
            if (name == "include")
            {
                if (directive.Count != 2) throw new UnauthorizedAccessException();
                var include = directive[1];
                // A basename or a wildcard is resolved under the immutable, Helper-owned conf tree.
                var path = Path.IsPathFullyQualified(include) ? include : Path.Combine(root, "conf", include);
                var directory = Path.GetDirectoryName(path)!;
                if (!WindowsManagedRuntimePolicy.Contains(Path.Combine(root, "conf"), directory)) throw new UnauthorizedAccessException();
                WindowsManagedRuntimePolicy.RequireNoLinks(directory);
                var pattern = Path.GetFileName(path);
                if (pattern != "mime.types" && !pattern.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
                if (pattern.Contains('?') || pattern is "*" or "*.*" || pattern.Contains('*') && pattern != "*.conf") throw new UnauthorizedAccessException();
                if (Directory.Exists(directory))
                    foreach (var file in Directory.EnumerateFiles(directory, pattern)) WindowsManagedRuntimePolicy.RequireNoLinks(file);
            }
            if (name is "pid" or "error_log" or "access_log" || name.EndsWith("_temp_path", StringComparison.Ordinal))
            {
                if (directive.Count < 2) throw new UnauthorizedAccessException();
                var value = directive[1];
                if (name == "pid")
                {
                    var pid = Path.IsPathFullyQualified(value) ? value : Path.Combine(root, value);
                    if (!Path.GetFullPath(pid).Equals(Path.Combine(root, "logs", "nginx.pid"), StringComparison.OrdinalIgnoreCase))
                        throw new UnauthorizedAccessException();
                }
                if (value is not ("off" or "stderr"))
                {
                    if (value.Contains('$')) throw new UnauthorizedAccessException();
                    var path = Path.IsPathFullyQualified(value) ? value : Path.Combine(root, value);
                    if (!WindowsManagedRuntimePolicy.Contains(Path.Combine(root, "logs"), path)
                        && !WindowsManagedRuntimePolicy.Contains(Path.Combine(root, "temp"), path)) throw new UnauthorizedAccessException();
                    WindowsManagedRuntimePolicy.RequireNoLinks(path);
                }
            }
            directive.Clear();
        }
        foreach (var value in text)
        {
            if (comment) { if (value == '\n') comment = false; continue; }
            if (escape) { token.Append(value); escape = false; continue; }
            if (value == '\\') { escape = true; continue; }
            if (quote != '\0') { if (value == quote) quote = '\0'; else token.Append(value); continue; }
            if (value is '\'' or '"') { quote = value; continue; }
            if (value == '#') { Flush(); comment = true; continue; }
            if (value is ';' or '{' or '}') { Check(); continue; }
            if (char.IsWhiteSpace(value)) Flush(); else token.Append(value);
        }
        if (escape || quote != '\0' || token.Length > 0 || directive.Count > 0) throw new UnauthorizedAccessException();
    }
}
