using System.Runtime.Versioning;
using System.Security.Principal;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Resolves the extra Windows identities that the developer console host authorizes on its pipe.
/// The Helper must run elevated, which on a workstation usually means a different account than the
/// Server that connects to it. Naming the Server's identity in the debug configuration authorizes
/// exactly that caller, so the pipe stays usable without adding local administrator rights or
/// moving the Server onto the Helper's own account.
/// </summary>
/// <remarks>
/// Console-only. The production service never reads this list: it authorizes the installed Server
/// service SID instead, which the installer obtains from the local service account.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class DeveloperUserSidAllowList
{
    /// <summary>A pipe DACL is created once per connection; keep the explicit list small and auditable.</summary>
    internal const int MaximumEntries = 32;

    /// <summary>
    /// Returns the authoritative client SIDs for the pipe DACL: the identity running the Helper
    /// (so breakpoints keep working for the developer who started it) followed by every configured
    /// entry. Entries may be canonical SID strings (<c>S-1-5-21-…</c>) or resolvable account names
    /// (<c>MACHINE\user</c>, <c>DOMAIN\user</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an entry is empty, malformed, or unresolvable. A silently dropped entry would
    /// surface later as an unexplained access denial on the client, so resolution fails loudly here.
    /// </exception>
    public static IReadOnlyList<string> Resolve(string currentUserSid, IEnumerable<string>? configured)
    {
        var resolved = new List<string>();
        Add(resolved, NormalizeSid(currentUserSid, "The current Helper identity"));
        var entries = (configured ?? []).ToList();
        if (entries.Count > MaximumEntries)
            throw new InvalidOperationException($"developerUserSids must not contain more than {MaximumEntries} entries.");
        foreach (var entry in entries) Add(resolved, ResolveEntry(entry));
        return resolved;
    }

    private static string ResolveEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            throw new InvalidOperationException("developerUserSids must not contain an empty entry.");
        var value = entry.Trim();
        if (value.Length > 256)
            throw new InvalidOperationException($"developerUserSids entry '{value[..32]}…' is too long.");
        // "S-1-" is the canonical prefix of every SID string; anything else is treated as an
        // account name so the configuration stays readable as "WIN-S8T6HHR0L9U\codexdev".
        if (value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return NormalizeSid(value, "developerUserSids entry");
        try
        {
            return ((SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception exception) when (exception is SystemException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"developerUserSids entry '{value}' is neither a SID nor a resolvable Windows account name.", exception);
        }
    }

    private static string NormalizeSid(string value, string source)
    {
        try
        {
            return new SecurityIdentifier(value.Trim()).Value;
        }
        catch (Exception exception) when (exception is ArgumentException or SystemException)
        {
            throw new InvalidOperationException($"{source} '{value}' is not a valid security identifier.", exception);
        }
    }

    private static void Add(List<string> resolved, string sid)
    {
        // Windows SIDs are case-insensitive; the same account written two ways must not produce
        // two rules, and the Helper's own identity must not be listed twice.
        if (!resolved.Contains(sid, StringComparer.OrdinalIgnoreCase)) resolved.Add(sid);
    }
}
