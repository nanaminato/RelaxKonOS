using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Installations;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.WebServer;


internal sealed partial class NginxWebServerManager
{
    private static IEnumerable<string> FindNginxExecutables()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nginx", "nginx.exe"), @"C:\nginx\nginx.exe" }
            : new[] { "/usr/sbin/nginx", "/usr/bin/nginx", "/usr/local/sbin/nginx", "/usr/local/bin/nginx", "/usr/local/nginx/sbin/nginx", "/usr/local/openresty/nginx/sbin/nginx" };
        return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipManagedExecutable(bool managedInstallation, string executable, string managedExecutable) =>
        managedInstallation && string.Equals(executable, managedExecutable, StringComparison.OrdinalIgnoreCase);

    private static string InstanceId(string executable) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executable))))[..32].ToLowerInvariant();

    private static string? ParseConfigPath(string output, string executable)
    {
        var match = ConfigurationPathPattern().Match(output);
        if (!match.Success) return null;
        var value = match.Groups["path"].Value.Trim('"', '\'');
        return Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executable)!, value));
    }

    private static string? FindOwnedIncludeDirectory(string configPath)
    {
        if (!File.Exists(configPath) || IsSymbolicLink(configPath)) return null;
        try
        {
            var config = File.ReadAllText(configPath);
            var depth = 0;
            var httpDepth = -1;
            foreach (var rawLine in config.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (httpDepth < 0 && HttpBlockPattern().IsMatch(line))
                {
                    depth += line.Count(character => character == '{') - line.Count(character => character == '}');
                    httpDepth = depth;
                    continue;
                }
                if (httpDepth >= 0 && depth == httpDepth)
                {
                    var match = IncludePattern().Match(line);
                    if (match.Success)
                    {
                        var value = match.Groups["path"].Value.Trim().Trim('"', '\'');
                        var directory = Path.GetDirectoryName(value);
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            if (!Path.IsPathFullyQualified(directory)) directory = Path.Combine(Path.GetDirectoryName(configPath)!, directory);
                            directory = Path.GetFullPath(directory);
                            if (Path.GetFileName(directory).Equals("conf.d", StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory)) return directory;
                        }
                    }
                }
                depth += line.Count(character => character == '{') - line.Count(character => character == '}');
                if (httpDepth >= 0 && depth < httpDepth) httpDepth = -1;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static bool IsOwnedFile(string path)
    {
        try
        {
            if (!File.Exists(path) || IsSymbolicLink(path)) return false;
            var content = File.ReadAllText(path);
            var expected = AnchorContent(Path.Combine(Path.GetDirectoryName(path)!, "relaxkonos.d"));
            return content == expected || content == LegacyOwnedContent || content == PreviousOwnedContent;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsSymbolicLink(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path) ? File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) : false; }
        catch (IOException) { return true; }
    }

    private static string AnchorContent(string sitesDirectory)
        => $"{OwnershipMarker}\n# RelaxKonOS-owned Nginx integration anchor.\ninclude {NginxConfigPath(sitesDirectory)}/*.conf;\n";

    private static string NginxConfigPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    /// <summary>Allows a normal DNS name, the explicit local-development name <c>localhost</c>,
    /// or a literal IP address for LAN and pre-DNS use.
    /// The value is later emitted into Nginx's server_name directive, so never accept an
    /// arbitrary host string here.</summary>
    private static bool IsValidServerName(string value) => value.Length <= 253 &&
        (value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(value, out _)
            || Uri.CheckHostName(value) == UriHostNameType.Dns && DomainPattern().IsMatch(value));

    /// <summary>Checks the process image for the selected instance instead of treating any
    /// Nginx process on the host as this instance. Linux Nginx changes the master and worker
    /// process names to values such as "nginx: master process", so Process.GetProcessesByName
    /// cannot be used here.</summary>
}
