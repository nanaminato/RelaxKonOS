using RelaxKonOS.PrivilegedHelper;
using System.Runtime.InteropServices;

if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP: native Linux ownership checks require Linux."); return; }
var count = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine($"PASS {++count}: {message}"); }
void Rejected(Action action, string message)
{
    try { action(); throw new Exception("Not rejected: " + message); }
    catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException) { Check(true, message); }
}
Check(LinuxFileOwnership.Resolve("root", false) == 0 && LinuxFileOwnership.Resolve("root", true) == 0, "NSS resolves owner and group names");
Check(LinuxFileOwnership.Resolve("1000", false) == 1000, "numeric IDs are supported");
Rejected(() => LinuxFileOwnership.Resolve("4294967295", false), "the unchanged-ID sentinel is rejected");
Rejected(() => LinuxFileOwnership.Resolve("-1", false), "negative IDs are rejected");
Rejected(() => LinuxFileOwnership.Resolve("relaxkonos-no-such-owner-20261024", false), "unknown accounts are rejected");
var root = Path.Combine(Path.GetTempPath(), "relaxkonos-ownership-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
    var child = Path.Combine(project, "child"); File.WriteAllText(child, "unchanged content");
    var outside = Path.Combine(root, "outside"); File.WriteAllText(outside, "outside");
    File.CreateSymbolicLink(Path.Combine(project, "link"), outside);
    var before = LinuxFileOwnership.Read(outside);
    using (LinuxUserFileOperations.AnchorAllowedRoots([project]))
    {
        Rejected(() => LinuxUserFileOperations.SetUnixOwnership(outside, "0", "0", false), "scope escape is rejected");
        Rejected(() => LinuxUserFileOperations.SetUnixOwnership(Path.Combine(project, "link"), "0", "0", false), "selected symlink is rejected");
    }
    if (geteuid() == 0)
    {
        using var scope = LinuxUserFileOperations.AnchorAllowedRoots([root]);
        var metadata = LinuxUserFileOperations.SetUnixOwnership(project, "1000", "1000", false);
        Check(metadata.Owner == 1000 && metadata.Group == 1000, "selected directory ownership changes");
        Check(LinuxFileOwnership.Read(child).Owner == 0, "nonrecursive change leaves child unchanged");
        LinuxUserFileOperations.SetUnixOwnership(project, "1000", "1000", true);
        Check(LinuxFileOwnership.Read(child) == (1000u, 1000u), "recursive change reaches descendants");
        Check(LinuxFileOwnership.Read(outside) == before, "recursive change skips symlink target");
        Check(File.ReadAllText(child) == "unchanged content", "ownership change preserves file contents");
        var bad = LinuxFileOwnership.Read(project);
        Rejected(() => LinuxUserFileOperations.SetUnixOwnership(project, "0", "no-such-group-relaxkonos", true), "invalid group is rejected before changes");
        Check(LinuxFileOwnership.Read(project) == bad, "invalid group leaves ownership unchanged");
        Rejected(() => LinuxUserFileOperations.SetUnixOwnership("/", "0", "0", true), "filesystem root cannot be changed");
    }
    else Console.WriteLine("SKIP: mutations require root in an isolated test directory.");
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"{count} ownership checks passed.");

[DllImport("libc.so.6")] static extern uint geteuid();
