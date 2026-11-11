using System.Text;
using RelaxKonOS.Server.SystemPerformance;

internal static class ProcessDesktopIndexChecks
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS DESKTOP INDEX {++count}: {text}"); }
        var temp = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(temp, "desktop-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ordinary = Directory.CreateDirectory(Path.Combine(root, "ordinary")).FullName;
            var icon = Path.Combine(root, "icon.svg"); File.WriteAllText(icon, "<svg/>");
            File.WriteAllText(Path.Combine(ordinary, "visible.desktop"), $"Exec=env NAME=value \"/usr/bin/my app\" --flag\nIcon={icon}\n", new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(ordinary, "hidden.desktop"), $"Exec=hidden\nIcon={icon}\nHidden=true\n");
            File.WriteAllText(Path.Combine(ordinary, "nodisplay.desktop"), $"Exec=nodisplay\nIcon={icon}\nNoDisplay=true\n");
            File.WriteAllText(Path.Combine(ordinary, "too-big.desktop"), $"Exec=too-big\nIcon={icon}\n".PadRight(ProcessIconProvider.MaximumDesktopFileBytes + 1));
            var map = ProcessIconProvider.BuildDesktopIcons([ordinary], out var normal);
            Check(map.Count == 1 && map["my app"] == icon, "BOM, quoted env command and real absolute icon preserve existing desktop lookup behavior");
            Check(!map.ContainsKey("hidden") && !map.ContainsKey("nodisplay") && !map.ContainsKey("too-big"), "Hidden entries and desktop files over 64 KiB are skipped");
            Check(normal.ReadBytes < ProcessIconProvider.MaximumDesktopFileBytes && normal.Mappings == 1, "Known oversize desktop files consume no read budget or index space");
            var deep = Directory.CreateDirectory(Path.Combine(root, "deep")).FullName;
            var current = deep;
            for (var i = 0; i < ProcessIconProvider.MaximumDesktopDepth; i++) current = Directory.CreateDirectory(Path.Combine(current, "nested")).FullName;
            File.WriteAllText(Path.Combine(current, "at-limit.desktop"), $"Exec=at-limit\nIcon={icon}\n");
            var beyond = Directory.CreateDirectory(Path.Combine(current, "beyond")).FullName;
            File.WriteAllText(Path.Combine(beyond, "beyond.desktop"), $"Exec=beyond\nIcon={icon}\n");
            map = ProcessIconProvider.BuildDesktopIcons([deep], out _);
            Check(map.ContainsKey("at-limit") && !map.ContainsKey("beyond"), "Eight-level traversal accepts the boundary and skips deeper trees");
            if (OperatingSystem.IsLinux())
            {
                Directory.CreateSymbolicLink(Path.Combine(ordinary, "cycle"), ordinary);
                File.CreateSymbolicLink(Path.Combine(ordinary, "linked.desktop"), Path.Combine(beyond, "beyond.desktop"));
                map = ProcessIconProvider.BuildDesktopIcons([ordinary], out var linked);
                Check(map.Count == 2 && map.ContainsKey("beyond") && linked.Entries == 6, "Desktop file links use bounded reads while a real directory cycle is counted but not followed");
            }
            var many = Directory.CreateDirectory(Path.Combine(root, "many")).FullName;
            var missing = "relaxkonos-missing-" + Guid.NewGuid().ToString("N");
            for (var i = 0; i <= ProcessIconProvider.MaximumDesktopMappings; i++)
                File.WriteAllText(Path.Combine(many, i + ".desktop"), $"Exec=app-{i}\nIcon={missing}\n");
            map = ProcessIconProvider.BuildDesktopIcons([many], out var mappings, _ => "/icons/icon.svg");
            Check(map.Count == ProcessIconProvider.MaximumDesktopMappings && mappings.Entries == ProcessIconProvider.MaximumDesktopMappings,
                "Mapping cap stops scanning at 2048 rather than retaining all desktop entries");
            map = ProcessIconProvider.BuildDesktopIcons([many], out var strings, _ => "/icons/" + new string('a', 2000) + ".svg");
            Check(map.Count is > 0 and < ProcessIconProvider.MaximumDesktopMappings && strings.TextBytes <= ProcessIconProvider.MaximumDesktopTextBytes,
                "Retained key and icon UTF-16 text is independently capped at 512 KiB");
            map = ProcessIconProvider.BuildDesktopIcons([many], out var probes);
            Check(map.Count == 0 && probes.PathChecks == ProcessIconProvider.MaximumDesktopPathChecks,
                "Unresolvable icon names stop at the shared 32768 path-probe budget");
            Check(probes.ThemeEntries <= 2 * ProcessIconProvider.MaximumThemeEntries,
                "The two theme-root listings are bounded snapshots reused for all desktop records");
            var bytes = Directory.CreateDirectory(Path.Combine(root, "bytes")).FullName;
            for (var i = 0; i < ProcessIconProvider.MaximumDesktopReadBytes / ProcessIconProvider.MaximumDesktopFileBytes + 1; i++)
                File.WriteAllText(Path.Combine(bytes, i + ".desktop"), $"Exec=byte-{i}\nIcon=fake\n#".PadRight(ProcessIconProvider.MaximumDesktopFileBytes));
            map = ProcessIconProvider.BuildDesktopIcons([bytes], out var reads, _ => "/icons/icon.svg");
            Check(reads.ReadBytes == ProcessIconProvider.MaximumDesktopReadBytes && reads.Entries == 128 && map.Count == 128,
                "Total actual desktop input stops at 8 MiB without scanning remaining files");
            var visited = Directory.CreateDirectory(Path.Combine(root, "visited")).FullName;
            for (var i = 0; i <= ProcessIconProvider.MaximumDesktopEntries; i++) File.WriteAllText(Path.Combine(visited, i + ".txt"), "");
            ProcessIconProvider.BuildDesktopIcons([visited], out var entries);
            Check(entries.Entries == ProcessIconProvider.MaximumDesktopEntries, "Non-desktop files also consume the 8192 traversal-entry budget");
            var yielded = 0;
            IEnumerable<string> Roots()
            {
                for (var i = 0; i < 1000; i++) { yielded++; yield return Path.Combine(root, "absent-" + i); }
            }
            ProcessIconProvider.BuildDesktopIcons(Roots(), out var roots);
            Check(roots.Roots == ProcessIconProvider.MaximumDesktopRoots && yielded == ProcessIconProvider.MaximumDesktopRoots,
                "Root enumeration itself stops after 64 candidates without materializing the remainder");
            Console.WriteLine($"Desktop icon index checks passed: {count}");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(temp), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("desktop-index-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture cleanup path");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
