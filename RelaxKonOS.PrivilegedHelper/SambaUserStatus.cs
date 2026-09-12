namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Extracts enabled Samba accounts from <c>pdbedit -L -v</c> output.</summary>
public static class SambaUserStatus
{
    public static IReadOnlySet<string> ParseEnabledUsers(string output)
    {
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        string? username = null;
        bool? disabled = null;

        void Commit()
        {
            // Do not infer state from the account merely existing: pdbedit lists disabled
            // accounts too. An unrecognizable record deliberately remains disabled.
            if (username is not null && disabled == false)
                enabled.Add(username);
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Unix username:", StringComparison.OrdinalIgnoreCase))
            {
                Commit();
                username = line["Unix username:".Length..].Trim();
                disabled = null;
                continue;
            }

            if (!line.StartsWith("Account Flags:", StringComparison.OrdinalIgnoreCase)) continue;
            var flags = line["Account Flags:".Length..].Trim();
            var openingBracket = flags.IndexOf('[');
            var closingBracket = flags.IndexOf(']', openingBracket + 1);
            if (openingBracket >= 0 && closingBracket > openingBracket)
                disabled = flags[(openingBracket + 1)..closingBracket].Contains('D', StringComparison.OrdinalIgnoreCase);
        }

        Commit();
        return enabled;
    }
}
