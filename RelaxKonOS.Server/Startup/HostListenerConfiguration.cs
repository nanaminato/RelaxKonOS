using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Startup;

internal static class HostListenerConfiguration
{
    public static RelaxKonOS.Server.Certificate.KestrelCertificateRegistry ConfigureHostListeners(this WebApplicationBuilder builder, ServerModeResolver serverModeResolver)
    {
        // The deployment installer registers this executable with the Windows Service
        // Control Manager. Opt in to its lifetime protocol so SCM receives the start
        // acknowledgement instead of eventually treating a healthy HTTP process as a
        // timed-out service.
        builder.Host.UseWindowsService();
        // The host installer keeps any bootstrap PFX in its machine-protected configuration. Load it
        // before configuring Kestrel so a fresh HTTPS installation has a certificate even before a
        // certificate is later managed through the product UI.
        var installerCertificate = LoadInstallerCertificate(builder.Configuration);
        var kestrelCertificates = new RelaxKonOS.Server.Certificate.KestrelCertificateRegistry();
        var userModeControlSocket = builder.Configuration["UserMode:ControlSocketPath"]?.Trim();
        if (serverModeResolver.Mode == RelaxKonOS.Protocol.Common.ServerMode.User && OperatingSystem.IsLinux())
        {
            if (string.IsNullOrWhiteSpace(userModeControlSocket) || !Path.IsPathFullyQualified(userModeControlSocket))
                throw new InvalidOperationException("User Mode requires an absolute UserMode:ControlSocketPath.");
            var parent = Path.GetDirectoryName(userModeControlSocket);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                throw new InvalidOperationException("User Mode control socket directory is missing.");
        }
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureHttpsDefaults(https => https.ServerCertificateSelector = (_, hostName) => kestrelCertificates.Select(hostName) ?? installerCertificate);
            // This is a private same-UID HTTP control channel. It is not an alternative public listener:
            // lifecycle status probes /ready through the socket while clients continue using loopback TCP.
            if (userModeControlSocket is not null)
            {
                options.ListenUnixSocket(userModeControlSocket);
                // Explicit socket endpoints override ASPNETCORE_URLS. Keep the public
                // User Mode listener alongside the private lifecycle control channel.
                var urls = builder.Configuration["urls"] ?? "http://127.0.0.1:5000";
                foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" ||
                        endpoint.Port is < 1 or > 65535 || endpoint.AbsolutePath != "/" ||
                        endpoint.Query.Length != 0 || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 ||
                        endpoint.Host is not ("127.0.0.1" or "0.0.0.0" or "localhost"))
                        throw new InvalidOperationException("User Mode requires a supported HTTP listening address.");
                    if (endpoint.Host == "localhost") options.ListenLocalhost(endpoint.Port);
                    else options.Listen(System.Net.IPAddress.Parse(endpoint.Host), endpoint.Port);
                }
            }
        });
        return kestrelCertificates;
    }

    static X509Certificate2? LoadInstallerCertificate(IConfiguration configuration)
    {
        var path = configuration["Kestrel:Certificates:Default:Path"];
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException("The installer TLS certificate path is missing or invalid.");

        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, configuration["Kestrel:Certificates:Default:Password"],
                // Schannel requires an on-disk key container during the Windows TLS session.
                OperatingSystem.IsWindows() ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                throw new InvalidOperationException("The installer TLS certificate has no private key or is expired.");
            return certificate;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException("The installer TLS certificate could not be loaded.", exception);
        }
    }
}
