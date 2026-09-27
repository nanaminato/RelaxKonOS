using System.Text.RegularExpressions;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Conservatively rejects Compose features that AD04 deliberately does not import yet.
///
/// This is an admission check, not a second Compose implementation: after this check the Docker
/// CLI still parses and validates the document.  Keeping the check deliberately conservative is
/// important.  A definition we do not understand must be refused, never silently changed before
/// it reaches <c>docker compose</c>.
/// </summary>
public static partial class DockerComposeSubsetValidation
{
    public const string UnsupportedFeature = "docker.compose_feature_unsupported";

    public static bool IsSupported(string composeYaml, out string problemCode)
    {
        if (string.IsNullOrWhiteSpace(composeYaml))
        {
            problemCode = "docker.stack_invalid_compose";
            return false;
        }

        // AD04-M2 only admits image services, named volumes and project-managed networks.  These
        // patterns are key-oriented so ordinary image names, commands and comments do not get
        // mistaken for an unsupported feature.
        foreach (var rawLine in composeYaml.Split(['\r', '\n']))
        {
            var line = StripComment(rawLine);
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (UnsupportedKey().IsMatch(line) || BindMount().IsMatch(line) || DangerousDockerSocket().IsMatch(line)
                || RelativeHostPath().IsMatch(line) || AbsoluteBindMount().IsMatch(line))
            {
                problemCode = UnsupportedFeature;
                return false;
            }
        }

        problemCode = string.Empty;
        return true;
    }

    // YAML comments are not meaningful to Compose.  Quoted # characters are intentionally kept:
    // they can only make the admission test more conservative, which is safe for this boundary.
    private static string StripComment(string value)
    {
        var index = value.IndexOf('#');
        return index >= 0 ? value[..index] : value;
    }

    [GeneratedRegex(@"^\s*(?:build|privileged|external|network_mode|pid|ipc|userns_mode|devices|device_cgroup_rules|cap_add|cap_drop)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsupportedKey();

    [GeneratedRegex(@"^\s*(?:-\s*)?type\s*:\s*bind\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BindMount();

    [GeneratedRegex(@"/var/run/docker\.sock|//\./pipe/docker_engine", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousDockerSocket();

    // Relative source paths are a build-context/host-filesystem feature.  They are not a named
    // volume and cannot be made portable by resolving them from a phone.
    [GeneratedRegex(@"(?:\./|\.\./)", RegexOptions.CultureInvariant)]
    private static partial Regex RelativeHostPath();

    // A short-syntax mount whose source begins with '/' is a host bind mount.  Long syntax is
    // caught by its `type: bind` key above; Windows drive mounts are also rejected.
    [GeneratedRegex("^\\s*-\\s*(?:['\\\"])?(?:/[^\\s:'\\\"]+|[A-Za-z]:[\\\\/])[^\\r\\n]*:", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteBindMount();
}
