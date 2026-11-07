using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private static async Task<PrivilegedOperationResult> CheckNginxFirstWebsiteAsync()
    {
        if (!OperatingSystem.IsLinux()) return Fail(69, PrivilegedProblemCode.UnsupportedOperation, "Linux is required");
        if (!(await ConfirmNginxServiceIdleAsync()).Success) return UnknownReload();
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(listener => listener.Port is 80 or 443))
            return Fail(73, PrivilegedProblemCode.ResourceNotAllowed, "A web port is already in use");

        var configuration = await RunFixedCommandWithOutputAsync("/usr/sbin/nginx", ["-T"], "Nginx configuration could not be inspected");
        if (!configuration.Success || configuration.OutputBase64 is null) return UnknownReload();
        var dump = Encoding.UTF8.GetString(Convert.FromBase64String(configuration.OutputBase64));
        return HasOnlyExplicitNonWebListeners(dump) ? new(true)
            : Fail(73, PrivilegedProblemCode.ResourceNotAllowed, "The existing Nginx listeners could not be excluded");
    }

    // The bounded configuration dump stays in the Helper. Unknown syntax, omitted listeners and
    // a truncated dump all fail closed, including listeners from files that are not loaded yet.
    internal static bool HasOnlyExplicitNonWebListeners(string dump)
    {
        if (dump.EndsWith('…') || !dump.Contains("# configuration file /etc/nginx/nginx.conf:", StringComparison.Ordinal))
            return false;
        var configuration = string.Join('\n', dump.Split('\n').Where(line => !line.StartsWith("nginx:", StringComparison.Ordinal)));
        var blocks = new Stack<(bool Server, bool ExplicitListen)>();
        var words = new List<string>();
        var offset = 0;
        foreach (Match match in NginxConfigurationToken().Matches(configuration))
        {
            if (match.Index != offset) return false;
            offset += match.Length;
            var token = match.Value;
            if (char.IsWhiteSpace(token[0]) || token[0] == '#') continue;
            if (token == "{")
            {
                if (words.Count == 0) return false;
                blocks.Push((words[0] == "server", false));
                words.Clear();
            }
            else if (token == "}")
            {
                if (words.Count != 0 || !blocks.TryPop(out var block) || block is (true, false)) return false;
            }
            else if (token == ";")
            {
                if (words.Count == 0) return false;
                if (words[0] == "listen")
                {
                    if (words.Count < 2 || !blocks.TryPop(out var block) || !block.Server
                        || !int.TryParse(words[1].AsSpan(words[1].LastIndexOf(':') + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                        || port is < 1 or > 65535 or 80 or 443) return false;
                    blocks.Push((true, true));
                }
                words.Clear();
            }
            else words.Add(token.Trim('"', '\''));
        }
        return offset == configuration.Length && blocks.Count == 0 && words.Count == 0;
    }

    [GeneratedRegex("""\G(?:\s+|#[^\r\n]*|"[^"\\\r\n]*"|'[^'\\\r\n]*'|[{};]|[^#\s{};"'\\]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex NginxConfigurationToken();
}
