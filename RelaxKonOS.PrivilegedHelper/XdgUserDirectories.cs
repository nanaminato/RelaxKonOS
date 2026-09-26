namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Resolves the per-user directory names configured by xdg-user-dirs.</summary>
internal static class XdgUserDirectories
{
    public static IReadOnlyDictionary<string, string> Read(string home)
    {
        var configPath = Path.Combine(home, ".config", "user-dirs.dirs");
        if (!File.Exists(configPath)) return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(configPath))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;

                var key = line[..separator].Trim();
                if (!key.StartsWith("XDG_", StringComparison.Ordinal) || !key.EndsWith("_DIR", StringComparison.Ordinal))
                    continue;

                var rawValue = line[(separator + 1)..].Trim();
                if (rawValue.Length < 2 || rawValue[0] != '"' || rawValue[^1] != '"') continue;

                var value = rawValue[1..^1];
                string? path = value switch
                {
                    "$HOME" => home,
                    _ when value.StartsWith("$HOME/", StringComparison.Ordinal) => Path.Combine(home, value[6..]),
                    _ when Path.IsPathRooted(value) => value,
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(path)) result[key] = path;
            }
        }
        catch (IOException) { }
        return result;
    }
}
