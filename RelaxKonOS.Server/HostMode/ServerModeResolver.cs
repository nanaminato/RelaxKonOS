using System.Net;
using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.HostMode;

public interface IServerModeResolver
{
    ServerMode Mode { get; }
    ServerCapabilitiesDto Describe();
    bool Supports(ServerHostFeature feature);
}

public enum ServerHostFeature
{
    Docker, Firewall, FileServices, WebServer, Certificates, Tunnels, Proxy, NativeServices, AgentInstallation,
    PrivilegedOperations, ApplicationDeployments
}

/// <summary>
/// Owns the deployment boundary.  No endpoint may infer User Mode from Development, EUID, or a
/// missing helper: the configured mode is validated once and then used for every decision.
/// </summary>
public sealed class ServerModeResolver : IServerModeResolver
{
    private readonly string _pamTransport;
    private readonly string _listenerScope;
    private readonly ServerExecutionIdentityDto _identity;
    private readonly UserExecutionBackend _userExecutionBackend;

    public ServerModeResolver(IConfiguration configuration, UserExecutionBackend userExecutionBackend)
    {
        _userExecutionBackend = userExecutionBackend;
        var configured = configuration["Server:Mode"]?.Trim().ToLowerInvariant() ?? "system";
        Mode = configured switch
        {
            "user" => ServerMode.User,
            "system" => ServerMode.System,
            _ => throw new InvalidOperationException("Server:Mode must be 'user' or 'system'.")
        };
        _pamTransport = configuration["Identity:LinuxPamTransport"]?.Trim().ToLowerInvariant() ?? "helper";
        _listenerScope = ResolveListenerScope(configuration);
        _identity = ResolveIdentity();

        if (Mode != ServerMode.User) return;
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("Server:Mode=user is supported only on Linux.");
        if (_identity.Uid == 0)
            throw new InvalidOperationException("Server:Mode=user must not run as root.");
        if (_pamTransport != "in-process")
            throw new InvalidOperationException("User Mode requires Identity:LinuxPamTransport=in-process.");
        var pamService = configuration["Identity:LinuxPamService"]?.Trim() ?? "login";
        if (!RelaxKonOS.Server.Identity.LinuxPamProvider.IsValidPamServiceName(pamService) || !File.Exists(Path.Combine("/etc/pam.d", pamService)))
            throw new InvalidOperationException("User Mode requires an existing host PAM service.");
        if (!string.Equals(configuration["Privileges:Backend"]?.Trim(), "disabled", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("User Mode requires Privileges:Backend=disabled.");
        if (_listenerScope != "loopback")
            throw new InvalidOperationException("User Mode requires a loopback-only listener.");
    }

    public ServerMode Mode { get; }

    public bool Supports(ServerHostFeature feature) => Mode != ServerMode.User || feature switch
    {
        // Containerised deployment composes the Docker engine, so it inherits the Docker boundary:
        // a no-sudo User Mode host cannot reach the engine and must not be offered the feature.
        ServerHostFeature.Docker or ServerHostFeature.ApplicationDeployments or ServerHostFeature.Firewall or
        ServerHostFeature.FileServices or ServerHostFeature.WebServer or ServerHostFeature.Certificates or
        ServerHostFeature.Tunnels or ServerHostFeature.Proxy or ServerHostFeature.NativeServices or
        ServerHostFeature.AgentInstallation or ServerHostFeature.PrivilegedOperations => false,
        _ => true,
    };

    public ServerCapabilitiesDto Describe()
    {
        var user = Mode == ServerMode.User;
        var capabilities = new ServerHostCapabilitiesDto(
            Files: true, Terminal: true, Git: true, Metrics: true, Processes: true, Guardian: true,
            Docker: !user, Firewall: !user, FileServices: !user, WebServer: !user, Certificates: !user,
            Tunnels: !user, Proxy: !user, PrivilegedOperations: !user, ApplicationDeployments: !user);
        var limitations = new List<string>();
        if (user)
            limitations.AddRange(["user-mode-loopback-required", "privileged-feature-unavailable", "root-equivalent-docker-access", "guardian.cross_user_unavailable"]);
        else if (_userExecutionBackend == UserExecutionBackend.LocalIdentity)
            limitations.Add("user-execution-local-identity");
        return new ServerCapabilitiesDto(Mode, _identity, new ServerListenerDto(_listenerScope),
            new ServerAuthenticationDto(user ? "currentUnixUser" : "hostAccount", _pamTransport), capabilities, limitations);
    }

    private static string ResolveListenerScope(IConfiguration configuration)
    {
        var urls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
        if (string.IsNullOrWhiteSpace(urls)) return "loopback";
        var values = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values.Length > 0 && values.All(IsLoopbackUrl) ? "loopback" : "lan";
    }

    private static bool IsLoopbackUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)));

    private static ServerExecutionIdentityDto ResolveIdentity()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!OperatingSystem.IsLinux()) return new ServerExecutionIdentityDto(-1, Environment.UserName, home);
        return new ServerExecutionIdentityDto(checked((int)geteuid()), Environment.UserName, home);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}
