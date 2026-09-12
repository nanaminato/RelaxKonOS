namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Owns placement of RelaxKonOS's single Samba include inside the global section.</summary>
internal static class SambaMainConfiguration
{
    internal static bool TryEnsureManagedInclude(string main, string marker, string include, out string candidate)
    {
        candidate = main;
        var lines = main.Split('\n').ToList();
        var markerCount = lines.Count(line => line.TrimEnd('\r') == marker);
        var includeCount = lines.Count(line => line.Trim().Equals(include, StringComparison.OrdinalIgnoreCase));
        if (markerCount > 1 || includeCount > 1 || markerCount != includeCount) return false;

        // Normalize our own pair even when it already exists. Older builds put it immediately
        // after [global]; an included share then made later global options belong to that share.
        lines.RemoveAll(line => line.TrimEnd('\r') == marker || line.Trim().Equals(include, StringComparison.OrdinalIgnoreCase));
        var global = lines.FindIndex(line => line.Trim().Equals("[global]", StringComparison.OrdinalIgnoreCase));
        if (global < 0) return false;
        var firstShare = lines.FindIndex(global + 1, line => IsSectionHeader(line));
        if (firstShare < 0) firstShare = lines.Count;
        lines.Insert(firstShare, marker);
        lines.Insert(firstShare + 1, include);
        candidate = string.Join("\n", lines);
        return true;
    }

    private static bool IsSectionHeader(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']';
    }
}
