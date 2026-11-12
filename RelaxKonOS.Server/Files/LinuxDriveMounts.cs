namespace RelaxKonOS.Server.Files;

/// <summary>Presentation-only selection of local volumes from this process's mount namespace.
/// This never mounts devices or grants access; file operations retain their user/Helper checks.</summary>
internal static class LinuxDriveMounts
{
    internal static IReadOnlyList<string> ReadPaths()
    {
        try { return SelectPaths(File.ReadLines("/proc/self/mountinfo")); }
        catch (IOException) { return ["/"]; }
        catch (UnauthorizedAccessException) { return ["/"]; }
    }

    internal static IReadOnlyList<string> SelectPaths(IEnumerable<string> lines)
    {
        var mounts = new List<(string Path, string Identity)>();
        foreach (var line in lines)
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;
            var fields = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var filesystem = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6 || filesystem.Length < 3) continue;
            var path = Unescape(fields[4]);
            var source = Unescape(filesystem[1]);
            var identity = fields[2] + ":" + fields[3]; // device plus subtree: suppress duplicate bind aliases
            if (path == "/") { mounts.Add((path, identity)); continue; }
            // Only block-backed volumes belong in this local-disk list. Network filesystems,
            // pseudo filesystems, loop/snap images and arbitrary FUSE services are not local disks.
            if (!source.StartsWith("/dev/", StringComparison.Ordinal)
                || source.StartsWith("/dev/loop", StringComparison.Ordinal)
                || filesystem[0] == "squashfs"
                || !IsVolumePath(path)) continue;
            mounts.Add((path, identity));
        }
        var result = new List<string> { "/" };
        var identities = new HashSet<string>(mounts.Where(m => m.Path == "/").Select(m => m.Identity), StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal) { "/" };
        // Stable, shallow-first ordering chooses a useful primary mount over a nested bind alias.
        foreach (var mount in mounts.Where(m => m.Path != "/").OrderBy(m => m.Path.Count(c => c == '/')).ThenBy(m => m.Path, StringComparer.Ordinal))
            if (paths.Add(mount.Path) && identities.Add(mount.Identity)) result.Add(mount.Path);
        return result;
    }

    private static bool IsVolumePath(string path)
    {
        if (!path.StartsWith('/') || path.Contains('\0')) return false;
        // Mountinfo also contains container file binds and OS implementation details. Do not
        // advertise those as disks. /run/media is the desktop automounter's legitimate exception.
        foreach (var prefix in new[] { "/dev", "/proc", "/sys", "/etc", "/boot", "/snap", "/var/lib/docker", "/var/lib/containers" })
            if (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal)) return false;
        return !path.StartsWith("/run/", StringComparison.Ordinal) || path.StartsWith("/run/media/", StringComparison.Ordinal);
    }

    private static string Unescape(string value) => value.Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);
}
