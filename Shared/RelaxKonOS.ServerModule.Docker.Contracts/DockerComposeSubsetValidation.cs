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
    public const string UnsupportedFeature = DockerStackProblem.FeatureUnsupported;

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

            // Compose interpolates the document before it parses it, and this server exposes no way to
            // supply a value: `up` would substitute an empty string and still exit successfully, so the
            // project would run on values the operator never saw.  A reference that the server cannot
            // resolve is therefore a refused document, not a blank one.
            if (ReferencesVariable(line))
            {
                problemCode = DockerStackProblem.VariableUnresolved;
                return false;
            }
        }

        problemCode = string.Empty;
        return true;
    }

    /// <summary>
    /// Removes a YAML comment.  Compose interpolates the parsed document rather than the raw text, so a
    /// comment can never carry a variable reference — but this test only sees text, which means a '#'
    /// inside a quoted scalar must not be mistaken for the start of one.
    /// </summary>
    private static string StripComment(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote == '\0')
            {
                if (character is '\'' or '"') quote = character;
                else if (character == '#') return value[..index];
                continue;
            }
            if (quote == '"' && character == '\\') { index++; continue; }
            if (character != quote) continue;
            // A single-quoted scalar escapes its own quote by doubling it.
            if (quote == '\'' && index + 1 < value.Length && value[index + 1] == '\'') { index++; continue; }
            quote = '\0';
        }
        return value;
    }

    /// <summary>
    /// Whether the line carries a variable the server has no value for.  <c>$$</c> is Compose's escape for
    /// a literal dollar, so it is removed first; every remaining <c>$</c> that starts a name or a brace is
    /// a substitution.
    /// </summary>
    private static bool ReferencesVariable(string line) => VariableReference().IsMatch(line.Replace("$$", string.Empty));

    [GeneratedRegex(@"^\s*(?:build|privileged|external|network_mode|pid|ipc|userns_mode|devices|device_cgroup_rules|cap_add|cap_drop)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsupportedKey();

    // Bare `$NAME`, braced `${NAME}`, and the whole `${NAME:-default}` / `${NAME:?error}` guard family
    // all read from an environment the server does not provide.
    [GeneratedRegex(@"\$(?:\{[^}]*\}|[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex VariableReference();

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
