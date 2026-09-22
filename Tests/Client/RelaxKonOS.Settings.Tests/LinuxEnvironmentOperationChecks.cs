using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

internal static class LinuxEnvironmentOperationChecks
{
    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "relaxkonos-environment-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "KEEP=old\nEMPTY=\"\"\n");
            var target = new SettingsTarget("host/environment/machine", SettingsScope.HostMachine);
            var read = LinuxEnvironmentOperations.ExecuteForPath(new(PrivilegedOperationKind.HostEnvironmentRead,
                EnvironmentTarget: target, OperationId: Guid.NewGuid()), path, pamEnvironmentEnabled: true);
            Check(read.Success && read.HostEnvironment is { Provider: "linux-pam-environment" }, "machine PAM provider reads fixed store");
            var revision = read.HostEnvironment!.Revision;
            var change = new EnvironmentChangeSet([
                new("KEEP", EnvironmentMutationKind.Set, "new"),
                new("EMPTY", EnvironmentMutationKind.Delete),
                new("ADDED", EnvironmentMutationKind.Set, "$(never-executed)")
            ], ConfirmHighImpact: true);
            var applied = LinuxEnvironmentOperations.ExecuteForPath(new(PrivilegedOperationKind.HostEnvironmentApply,
                EnvironmentTarget: target, EnvironmentChange: change, ExpectedRevision: revision, OperationId: Guid.NewGuid()), path, true);
            Check(applied.Success && applied.HostEnvironment!.Values.Single(value => value.Name == "ADDED").Value == "$(never-executed)",
                "atomic apply and literal shell text");

            File.AppendAllText(path, "EXTERNAL=change\n");
            var conflict = LinuxEnvironmentOperations.ExecuteForPath(new(PrivilegedOperationKind.HostEnvironmentApply,
                EnvironmentTarget: target, EnvironmentChange: change, ExpectedRevision: applied.HostEnvironment!.Revision, OperationId: Guid.NewGuid()), path, true);
            Check(!conflict.Success && conflict.ProblemCode == PrivilegedProblemCode.Conflict, "external edit revision conflict");

            var userTarget = new SettingsTarget("host/environment/user/1000", SettingsScope.HostUser, "1000");
            var user = LinuxEnvironmentOperations.ExecuteForPath(new(PrivilegedOperationKind.HostEnvironmentRead,
                EnvironmentTarget: userTarget, OperationId: Guid.NewGuid()), path, true);
            Check(!user.Success && user.ProblemCode == PrivilegedProblemCode.ResourceNotAllowed, "Linux user store is not fabricated");
            var disabled = LinuxEnvironmentOperations.ExecuteForPath(new(PrivilegedOperationKind.HostEnvironmentRead,
                EnvironmentTarget: target, OperationId: Guid.NewGuid()), path, pamEnvironmentEnabled: false);
            Check(!disabled.Success && disabled.ProblemCode == PrivilegedProblemCode.UnsupportedOperation, "PAM configuration is required");
            Console.WriteLine("Linux environment provider: PAM machine store, atomic apply, revision conflict, literal values and unsupported user scope passed.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("Linux environment provider check failed: " + name);
    }
}
