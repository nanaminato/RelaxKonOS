using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Settings;

internal static class EnvironmentChecks
{
    public static void Run()
    {
        var source = "# managed by administrator\r\n  KEEP=literal\r\nEMPTY=\"\"\r\nREMOVE=old";
        var document = LinuxEnvironmentDocument.Parse(source);
        var result = document.Apply(new([
            new("EMPTY", EnvironmentMutationKind.Set, ""),
            new("REMOVE", EnvironmentMutationKind.Delete),
            new("ADDED", EnvironmentMutationKind.Set, "$(touch /tmp/not-executed); `whoami`")
        ]));
        Check(result.StartsWith("# managed by administrator\r\n  KEEP=literal\r\n", StringComparison.Ordinal), "unrelated bytes/CRLF");
        var values = LinuxEnvironmentDocument.Parse(result).Values;
        Check(values["EMPTY"] == "" && !values.ContainsKey("REMOVE"), "empty versus delete");
        Check(values["ADDED"] == "$(touch /tmp/not-executed); `whoami`", "shell text remains data");
        foreach (var bad in new[] { "export X=y", "X=y\nX=z", "X=\"abc#def\"", "X =value", "X= value", "X=\\value", "X=\"multi\nline\"" })
            Reject(() => LinuxEnvironmentDocument.Parse(bad));
        Reject(() => document.Apply(new([new("NEW", EnvironmentMutationKind.Set, "line\nbreak")])));
        Check(document.Values["REMOVE"] == "old", "failed edit retained source");
        var duplicate = new EnvironmentChangeSet([new("Name", EnvironmentMutationKind.Set, "a"), new("NAME", EnvironmentMutationKind.Set, "b")]);
        Check(EnvironmentValidation.Validate(duplicate, true) == "settings.environment.duplicate_name", "Windows names");
        Check(EnvironmentValidation.Validate(duplicate, false) is null, "Linux names");
        Check(EnvironmentValidation.Validate(new([new("PATH", EnvironmentMutationKind.Set, "")]), false)
            == "settings.environment.high_impact_confirmation_required", "high impact confirmation");
        Check(EnvironmentValidation.Validate(new([new("VALUE", EnvironmentMutationKind.Set, "\0")]), false) is not null, "NUL rejection");
        Check(EnvironmentValidation.Validate(new([new("VALUE", EnvironmentMutationKind.Delete, "")]), false) is not null, "delete payload rejection");
        var expansion = EnvironmentExpansion.Expand("%A%", new Dictionary<string, string> { ["a"] = "%B%", ["B"] = "%A%" }, true);
        Check(expansion.Warnings.Contains("settings.environment.expansion_cycle"), "cycle detection");
        Check(EnvironmentExpansion.Expand("$(whoami); `id` $HOME", new Dictionary<string, string> { ["HOME"] = "/home/remote" }, false).Value
            == "$(whoami); `id` /home/remote", "bounded display substitution");
        var bomb = Enumerable.Range(0, 18).ToDictionary(i => "V" + i, i => "%V" + (i + 1) + "%%V" + (i + 1) + "%");
        var bounded = EnvironmentExpansion.Expand("%V0%", bomb, true);
        Check(bounded.Value.Length <= EnvironmentValidation.MaximumExpandedLength && bounded.Warnings.Count > 0, "expansion bounds");
        Check(EnvironmentExpansion.SplitPath(":/bin:/bin:", false).SequenceEqual(new[] { "", "/bin", "/bin", "" }), "PATH lossless ordering");
        Check(EnvironmentExpansion.PathWarnings(":/bin:/bin:", false).Count == 2, "PATH duplicate and empty warnings");
        Console.WriteLine("Environment checks passed: lossless subset, unsupported syntax, empty/delete, platform names, confirmation, literal shell data, expansion bounds, PATH order. No OS configuration accessed.");
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("Environment check failed: " + name); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Unsupported environment syntax was accepted.");
    }
}
