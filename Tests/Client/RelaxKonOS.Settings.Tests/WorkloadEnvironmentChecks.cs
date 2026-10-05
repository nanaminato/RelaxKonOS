using RelaxKonOS.Protocol.Settings;

internal static class WorkloadEnvironmentChecks
{
    public static void Run()
    {
        EnvironmentVariable V(string name, string value, SettingsScope scope, EnvironmentValueKind kind = EnvironmentValueKind.String)
            => new(name, value, null, kind, scope, false, false, []);
        var machine = new[] { V("Path", "system;;system", SettingsScope.HostMachine), V("ROOT", "C:/tools", SettingsScope.HostMachine), V("HOME", "machine", SettingsScope.HostMachine) };
        var user = new[] { V("PATH", "user;", SettingsScope.HostUser), V("HOME", "user", SettingsScope.HostUser) };
        var workspace = new[] { V("path", "%ROOT%/bin", SettingsScope.Workspace, EnvironmentValueKind.ExpandString), V("EMPTY", "", SettingsScope.Workspace) };
        var appended = WorkloadEnvironmentBuilder.Build(machine, user, workspace, EnvironmentPathMode.Append, true);
        Check(appended["PATH"] == "system;;system;user;;C:/tools/bin" && appended["home"] == "user" && appended["EMPTY"] == "", "Windows user PATH appends to machine PATH; Workspace append, empty values, expansion and case semantics preserve data.");
        Check(WorkloadEnvironmentBuilder.Build(machine, user, workspace, EnvironmentPathMode.Replace, true)["PATH"] == "C:/tools/bin", "Workspace replacement is explicit.");
        var literal = "$(touch /tmp/no);`command`;${HOME}";
        var linux = WorkloadEnvironmentBuilder.Build([V("PATH", "/bin:", SettingsScope.HostMachine), V("Var", "one", SettingsScope.HostMachine)], [],
            [V("PATH", ":/tools:", SettingsScope.Workspace), V("VAR", literal, SettingsScope.Workspace)], EnvironmentPathMode.Append, false);
        Check(linux["PATH"] == "/bin:::/tools:" && linux["VAR"] == literal && linux["Var"] == "one", "Linux preserves literals, case, duplicates and empty PATH segments.");
        Reject(() => WorkloadEnvironmentBuilder.Build([], [], [V("A", "%B%", SettingsScope.Workspace, EnvironmentValueKind.ExpandString), V("B", "%A%", SettingsScope.Workspace, EnvironmentValueKind.ExpandString)], EnvironmentPathMode.Append, true));
        Reject(() => WorkloadEnvironmentBuilder.Build([], user, [], EnvironmentPathMode.Append, false));
        Reject(() => WorkloadEnvironmentBuilder.Build([], [], [V("X", "value", SettingsScope.HostUser)], EnvironmentPathMode.Append, true));
        Console.WriteLine("PASS: Workload environment platform casing, machine/user PATH composition, Workspace append/replace, empty/literal values, source checks and expansion cycle refusal.");
    }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected invalid workload environment refusal."); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
