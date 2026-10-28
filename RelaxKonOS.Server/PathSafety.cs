namespace RelaxKonOS.Server;

/// <summary>
/// The one containment rule for filesystem paths: "is this candidate inside that root?".
///
/// <para>
/// Containment checks are security-relevant (archive extraction, user-scoped file operations, Docker
/// build contexts, Git repository paths). Keeping one implementation means the comparison rules
/// cannot drift between domains: both sides are fully resolved, the comparison is case-insensitive
/// only where the platform is, and the boundary is a directory separator, so <c>/data/repo2</c> is not
/// inside <c>/data/repo</c>.
/// </para>
///
/// <para>
/// This deliberately does not resolve symbolic links or Windows reparse points. A caller that must
/// prevent a link from escaping its root has to resolve the real path first; this helper neither
/// performs nor pretends to perform that check.
/// </para>
/// </summary>
internal static class PathSafety
{
    internal static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Absolute path without a trailing separator, except for a filesystem root.</summary>
    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> itself or is located below it.
    /// Both values are resolved with <see cref="Path.GetFullPath(string)"/> first, so <c>..</c> and
    /// mixed separators are normalized before the comparison.
    /// </summary>
    internal static bool IsWithin(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate)) return false;

        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        if (string.Equals(normalizedRoot, normalizedCandidate, Comparison)) return true;

        // A filesystem root already ends with its separator ("/" or "C:\"); appending another one
        // would make every candidate look like it is outside the root.
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, Comparison);
    }
}
