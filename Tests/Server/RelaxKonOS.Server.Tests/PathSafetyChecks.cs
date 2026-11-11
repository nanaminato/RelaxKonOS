using RelaxKonOS.Server;

/// <summary>
/// One containment rule backs archive extraction, file operations, Docker build contexts and Git text
/// paths. These checks pin the boundary itself: a sibling with a shared prefix is not inside, a
/// normalized <c>..</c> is resolved before the comparison, and case sensitivity follows the platform.
/// </summary>
internal static class PathSafetyChecks
{
    internal static void Run()
    {
        var temp = Path.Combine(Path.GetTempPath(), "relaxkonos-path-safety-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "repo");
        var separator = Path.DirectorySeparatorChar;

        TestAssert.True(PathSafety.IsWithin(root, root), "pathSafety: a root contains itself");
        TestAssert.True(PathSafety.IsWithin(root, Path.Combine(root, "src", "file.cs")),
            "pathSafety: a child path is inside the root");
        TestAssert.True(PathSafety.IsWithin(root, Path.Combine(root, "src", "..", "file.cs")),
            "pathSafety: parent traversal that stays inside is allowed");
        TestAssert.True(PathSafety.IsWithin(root, root + separator + "src"),
            "pathSafety: a trailing separator on the candidate does not change the answer");
        TestAssert.True(PathSafety.IsWithin(root + separator, Path.Combine(root, "file.cs")),
            "pathSafety: a trailing separator on the root does not change the answer");
        TestAssert.True(PathSafety.IsWithin(root, root.Replace(separator, '/') + "/src/file.cs"),
            "pathSafety: alternate separators are normalized before comparison");

        TestAssert.True(!PathSafety.IsWithin(root, root + "2"), "pathSafety: a shared prefix is not a child");
        TestAssert.True(!PathSafety.IsWithin(root, Path.Combine(root, "..", "other")),
            "pathSafety: parent traversal that leaves the root is rejected");
        TestAssert.True(!PathSafety.IsWithin(root, Path.Combine(root, "..", "repo", "..", "outside")),
            "pathSafety: a path that leaves and does not return is rejected");
        TestAssert.True(!PathSafety.IsWithin(root, Path.GetTempPath()),
            "pathSafety: the temp root is not inside the repository");

        TestAssert.True(!PathSafety.IsWithin("", root), "pathSafety: an empty root never contains anything");
        TestAssert.True(!PathSafety.IsWithin(root, ""), "pathSafety: an empty candidate is never inside");
        TestAssert.True(!PathSafety.IsWithin("  ", root), "pathSafety: a whitespace root never contains anything");

        TestAssert.Equal(root, PathSafety.Normalize(root + separator),
            "pathSafety: normalize trims the trailing separator");

        var differentCase = Path.Combine(temp, "REPO", "file.cs");
        if (OperatingSystem.IsWindows())
        {
            TestAssert.True(PathSafety.IsWithin(root, differentCase),
                "pathSafety: Windows compares paths case-insensitively");
            TestAssert.Equal(StringComparison.OrdinalIgnoreCase, PathSafety.Comparison,
                "pathSafety: Windows uses an ordinal ignore-case comparison");
        }
        else
        {
            TestAssert.True(!PathSafety.IsWithin(root, differentCase),
                "pathSafety: Unix compares paths case-sensitively");
            TestAssert.Equal(StringComparison.Ordinal, PathSafety.Comparison,
                "pathSafety: Unix uses an ordinal comparison");
        }

        Console.WriteLine("Path safety checks passed: 16");
    }
}
