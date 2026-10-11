namespace RelaxKonOS.Server.SystemMonitor;

/// <summary>Shared Linux process metadata reader. The passwd map is loaded once, while volatile /proc data stays per-process.</summary>
internal static class LinuxProcessMetadata
{
    private static readonly Lazy<IReadOnlyDictionary<uint, string>> UserNames = new(LoadPasswd, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string? GetUserName(int pid)
    {
        try
        {
            return ReadDetails(pid).UserName;
        }
        catch { return null; }
    }

    public static int CountThreads(int pid)
    {
        try { return ReadStatus(pid).ThreadCount; }
        catch { return 0; }
    }

    public static LinuxProcessDetails ReadDetails(int pid)
    {
        try
        {
            var status = ReadStatus(pid);
            if (status.Uid is null) return new(null, status.ThreadCount);
            var userName = UserNames.Value.TryGetValue(status.Uid.Value, out var name) ? name : status.Uid.Value.ToString();
            return new(userName, status.ThreadCount);
        }
        catch { return new(null, 0); }
    }

    public static uint? ReadUid(int pid)
    {
        try { return ReadStatus(pid).Uid; }
        catch { return null; }
    }

    private static LinuxProcessStatus ReadStatus(int pid) => ParseStatus(File.ReadLines($"/proc/{pid}/status"));

    internal static LinuxProcessStatus ParseStatus(IEnumerable<string> lines)
    {
        uint? uid = null;
        var threads = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
                uid = ParseFirstUInt(line.AsSpan(4));
            else if (line.StartsWith("Threads:", StringComparison.Ordinal))
            {
                var count = ParseFirstUInt(line.AsSpan(8));
                threads = count is { } value && value <= (uint)int.MaxValue ? (int)value : 0;
            }
            if (uid is not null && threads > 0) break;
        }
        return new(uid, threads);
    }

    private static uint? ParseFirstUInt(ReadOnlySpan<char> fields)
    {
        fields = fields.Trim();
        var separator = fields.IndexOfAny(' ', '\t');
        var value = separator < 0 ? fields : fields[..separator];
        return uint.TryParse(value, out var parsed) ? parsed : null;
    }

    private static IReadOnlyDictionary<uint, string> LoadPasswd()
    {
        var map = new Dictionary<uint, string>();
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                // name:x:uid:gid:gecos:home:shell
                var parts = line.Split(':');
                if (parts.Length >= 3 && uint.TryParse(parts[2], out var uid))
                    map[uid] = parts[0];
            }
        }
        catch { /* /etc/passwd 不可读——退化为 uid 数字 */ }
        return map;
    }

    internal readonly record struct LinuxProcessStatus(uint? Uid, int ThreadCount);
}

internal readonly record struct LinuxProcessDetails(string? UserName, int ThreadCount);
