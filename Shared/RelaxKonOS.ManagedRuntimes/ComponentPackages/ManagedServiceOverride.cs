namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>
/// The systemd unit override this product writes for a distribution package, and the rule that decides
/// whether any other override still leaves the packaged unit verifiable.
/// </summary>
/// <remarks>
/// Docker Desktop ignores <c>daemon.json</c> proxies, so a native Linux daemon is configured through the
/// unit's start-up environment: RelaxKonOS writes
/// <c>/etc/systemd/system/docker.service.d/http-proxy.conf</c>. Package ownership verification refuses
/// unit overrides on purpose, and that refusal used to cover every drop-in. Configuring a proxy — with
/// this product or by hand, which is the documented Docker way — therefore made the installed engine
/// permanently unverifiable: no verified identity means no "纳入管理", no update and no uninstall in
/// Component Center, even when RelaxKonOS installed the package itself.
///
/// The accepted shape is what actually matters for ownership: a drop-in that only assigns proxy
/// environment variables changes neither the packaged binary nor the unit's start command, so the
/// package identity still describes what runs. Every other directive — <c>ExecStart=</c>, <c>User=</c>,
/// <c>Type=</c>, another section — keeps the administrator-override refusal.
/// </remarks>
public static class ManagedServiceOverride
{
    public const string DockerDropInDirectory = "/etc/systemd/system/docker.service.d";
    public const string DockerDropInFile = "http-proxy.conf";
    public const string DockerProxyMarker = "# RelaxKonOS managed Docker daemon proxy - do not edit";
    public const int MaximumProxyValueLength = 512;
    public const int MaximumProxyBypassLength = 1024;

    /// <summary>Proxy variables a daemon reads from its start-up environment.</summary>
    private static readonly string[] ProxyVariables =
        ["HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "no_proxy", "all_proxy"];

    /// <summary>Serializes the docker proxy drop-in written by the Helper.</summary>
    public static string SerializeDockerProxy(string httpProxy, string httpsProxy, string noProxy)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(DockerProxyMarker).Append('\n');
        builder.Append("[Service]\n");
        builder.Append("Environment=\"HTTP_PROXY=").Append(EscapeUnitValue(httpProxy)).Append("\"\n");
        builder.Append("Environment=\"HTTPS_PROXY=").Append(EscapeUnitValue(httpsProxy)).Append("\"\n");
        if (noProxy.Length > 0) builder.Append("Environment=\"NO_PROXY=").Append(EscapeUnitValue(noProxy)).Append("\"\n");
        return builder.ToString();
    }

    /// <summary>systemd treats '%' as a specifier introducer, so a literal percent must be doubled.</summary>
    public static string EscapeUnitValue(string value) => value.Replace("%", "%%", StringComparison.Ordinal);

    /// <summary>
    /// True when a drop-in file only assigns proxy environment variables inside <c>[Service]</c>.
    /// Comments are free text. Anything that can change what systemd executes — another section,
    /// another directive, a variable that is not a proxy setting — is not this shape.
    /// </summary>
    public static bool IsProxyOnlyUnitOverride(string? content)
    {
        if (content is null) return false;
        var inService = false;
        foreach (var raw in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                if (!string.Equals(line, "[Service]", StringComparison.Ordinal) || inService) return false;
                inService = true;
                continue;
            }
            if (!inService || !line.StartsWith("Environment=", StringComparison.Ordinal)) return false;
            var value = line["Environment=".Length..].Trim();
            if (value.Length == 0) return false;
            if (value.StartsWith('"'))
            {
                if (value.Length < 3 || !value.EndsWith('"')) return false;
                value = value[1..^1];
            }
            var assignments = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (assignments.Length == 0) return false;
            if (!assignments.All(IsProxyAssignment)) return false;
        }
        // A comment-only or empty file loads nothing at all; it cannot carry an override either.
        return true;
    }

    private static bool IsProxyAssignment(string token)
    {
        var separator = token.IndexOf('=');
        return separator > 0 && ProxyVariables.Contains(token[..separator], StringComparer.Ordinal);
    }

    /// <summary>
    /// True when a drop-in directory holds nothing that changes the unit beyond proxy environment
    /// values. Only <c>*.conf</c> entries are loaded by systemd, so anything else is refused rather
    /// than silently ignored: it would be an unexplained administrator artefact in a verified unit.
    /// </summary>
    public static bool IsProxyOnlyDropInSet(IEnumerable<string> fileNames, Func<string, string?> readContent)
    {
        foreach (var name in fileNames)
        {
            if (!name.EndsWith(".conf", StringComparison.Ordinal)) return false;
            if (!IsProxyOnlyUnitOverride(readContent(name))) return false;
        }
        return true;
    }
}
