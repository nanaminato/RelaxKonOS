using System.Net;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.Identity;

/// <summary>Explicit, development-only Windows Desktop authentication boundary.</summary>
public sealed class WindowsDesktopSessionOptions
{
    public const string ConfigurationKey = "Identity:WindowsDesktopSessionEnabled";

    public WindowsDesktopSessionOptions(IConfiguration configuration, IHostEnvironment environment,
        UserExecutionBackend userExecutionBackend)
    {
        Enabled = bool.TryParse(configuration[ConfigurationKey], out var enabled) && enabled;
        if (!Enabled) return;
        if (!OperatingSystem.IsWindows() || !environment.IsDevelopment() || userExecutionBackend != UserExecutionBackend.LocalIdentity)
            throw new InvalidOperationException($"{ConfigurationKey}=true requires Windows Development with PrivilegedHelper:UserExecutionBackend=local-identity.");
        if (!IsLoopbackOnly(configuration))
            throw new InvalidOperationException($"{ConfigurationKey}=true requires a loopback-only listener.");
        if (!Environment.UserInteractive || ServerProcessIdentity.CurrentStableIdentity() is null)
            throw new InvalidOperationException($"{ConfigurationKey}=true requires an interactive non-service Windows account.");
    }

    public bool Enabled { get; }

    public void RequireEnabled()
    {
        if (!Enabled) throw new AliasAuthenticationException(404, "windows-desktop-session-unavailable");
    }

    private static bool IsLoopbackOnly(IConfiguration configuration)
    {
        var urls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
        if (string.IsNullOrWhiteSpace(urls)) return true;
        return urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)));
    }
}
