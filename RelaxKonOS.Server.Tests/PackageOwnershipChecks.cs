using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Installations;

internal static class PackageOwnershipChecks
{
    public static void Run()
    {
        var old = new ComponentInstalledPackage("nginx", "1.26.3-1", "amd64", new('a', 64));
        var newer = old with { Version = "1.26.4-1", ExecutableSha256 = new('b', 64) };
        Check(PackageIdentityPolicy.Assess(old, newer, false) == (true, true, false), "Verified external APT upgrade retains authorization and reports a change");
        Check(!PackageIdentityPolicy.ProvesOriginalCommit(old, newer), "An external APT upgrade is never evidence for the original install commit");
        Check(PackageIdentityPolicy.Assess(old, null, false) == (false, false, true), "Unverified package drift requires inspection");
        Check(PackageIdentityPolicy.Assess(old, newer with { Architecture = "arm64" }, false) == (false, false, true), "Architecture substitution does not inherit authorization");
        Check(PackageIdentityPolicy.Assess(null, newer, false) == (false, false, false), "Verifying an external package does not implicitly register it");
        Check(PackageIdentityPolicy.Assess(old, newer, true).RecoveryRequired, "A verified version cannot erase an unfinished transaction");
        Check(PackageIdentityPolicy.OnlyConfigurationChanges("??5?????? c /etc/nginx/nginx.conf\n")
            && !PackageIdentityPolicy.OnlyConfigurationChanges("??5??????   /usr/sbin/nginx\n")
            && !PackageIdentityPolicy.OnlyConfigurationChanges(null), "Configuration changes are allowed; binary drift and missing verification are rejected");

        var authorization = Guid.NewGuid();
        var registration = ComponentPackageRegistration.Authorize(authorization, "nginx", old);
        Check(registration.OperationId == authorization && registration.Identity == old && registration.Valid("nginx", "nginx"),
            "Explicit registration records the first authorization and the verified identity");
        var acknowledged = registration.Acknowledge(newer);
        Check(acknowledged.OperationId == authorization && acknowledged.Identity == newer,
            "Acknowledging a verified external change keeps the first authorization and advances only the identity");
        Check(registration.Identity == old, "Acknowledging returns a new record instead of mutating the original authorization");
        var substitutionRefused = false;
        try { registration.Acknowledge(newer with { Architecture = "arm64" }); }
        catch (InvalidOperationException) { substitutionRefused = true; }
        Check(substitutionRefused, "An acknowledgement cannot substitute another architecture");
        var packageRefused = false;
        try { ComponentPackageRegistration.Authorize(authorization, "nginx", old with { PackageName = "squid" }); }
        catch (InvalidOperationException) { packageRefused = true; }
        Check(packageRefused, "An authorization cannot name another component's package");
        Check(!ComponentPackageRegistration.Authorize(authorization, "nginx", old).Valid("squid", "nginx"),
            "A registration is scoped to the component that granted it");

        var target = new ComponentPackageTarget("nginx", newer.Version, "amd64", "repo|stable|main|amd64", "https://repo.example", "stable", "main", "https://repo.example/nginx.deb", new('c', 64), 100, []);
        var resolution = new ComponentPackageResolution("nginx", old.Version, target, InstalledPackage: old, UpdateAvailable: true, CandidateVersion: newer.Version);
        var state = NginxPackagePlan.BuildState(resolution, false, "host", "amd64");
        var request = new ComponentPlanRequest("nginx", "host", RuntimeChange.Update, state.Revision, ComponentSourceMode.SystemPackage, null, 0, 0, null, null, true, "external-update");
        var update = NginxPackagePlan.Evaluate(request, state, resolution, true);
        Check(update.Allowed && update.Target?.InstalledPackage == old, "External package updates freeze the exact observed installation");
        var removalRequest = request with { Change = RuntimeChange.Uninstall };
        var removal = NginxPackagePlan.Evaluate(removalRequest, state, resolution with { Candidate = null, CandidateVersion = null }, true);
        Check(removal.Allowed && removal.Target is { Package: null, InstalledPackage: not null }, "External package removal requires no repository candidate or old download URL");
        Check(!NginxPackagePlan.Evaluate(request, state, resolution with { InstalledPackage = null }, true).Allowed,
            "External changes cannot proceed without independently verified installed identity");
        var missingIdentity = NginxPackagePlan.Evaluate(removalRequest, state, resolution with { InstalledPackage = null }, true);
        Check(missingIdentity.Problems.Contains(ComponentProblemCodes.PackageIdentityUnverified),
            "Removal without a verified installed identity is refused before any host access");
        ComponentPackageInstallations.Validate("nginx", InstallationOperationKind.Uninstall,
            new("nginx", removalRequest, removal.Target), removalRequest.IdempotencyKey);
        Check(true, "Installed identity alone is a valid durable uninstall target");
        var rejected = false;
        try
        {
            ComponentPackageInstallations.Validate("nginx", InstallationOperationKind.Uninstall,
                new("nginx", removalRequest, removal.Target! with { InstalledPackage = null }), removalRequest.IdempotencyKey);
        }
        catch (InstallationException ex) { rejected = ex.ProblemCode == InstallationProblemCodes.InvalidRequest; }
        Check(rejected, "The ledger refuses an uninstall without the frozen installed identity");
        var installIdentityRefused = false;
        var installRequest = request with { Change = RuntimeChange.Install, IdempotencyKey = "install-identity" };
        try
        {
            ComponentPackageInstallations.Validate("nginx", InstallationOperationKind.Install,
                new("nginx", installRequest, new ComponentFrozenTarget(ComponentSourceMode.SystemPackage, null, target, InstalledPackage: old)),
                installRequest.IdempotencyKey);
        }
        catch (InstallationException ex) { installIdentityRefused = ex.ProblemCode == InstallationProblemCodes.InvalidRequest; }
        Check(installIdentityRefused, "A first install cannot inherit an existing installed identity");
        ComponentPackageInstallations.ValidateRecovery("nginx", InstallationOperationKind.Repair,
            new("nginx", Guid.Empty, ComponentRecoveryAction.RegisterPackage, old));
        Check(true, "Explicit registration carries a frozen installed identity into the existing ledger");
        var registrationSubstitution = false;
        try
        {
            ComponentPackageInstallations.ValidateRecovery("nginx", InstallationOperationKind.Repair,
                new("nginx", Guid.Empty, ComponentRecoveryAction.RegisterPackage, old with { PackageName = "squid" }));
        }
        catch (InstallationException ex) { registrationSubstitution = ex.ProblemCode == InstallationProblemCodes.InvalidRequest; }
        Check(registrationSubstitution, "Registration cannot name a package outside the component's fixed mapping");

        var uninstallState = NginxPackagePlan.BuildState(new("nginx", old.Version, null, InstalledPackage: old), false, "host", "amd64");
        var uninstallResolution = new ComponentPackageResolution("nginx", old.Version, null, InstalledPackage: old);
        var uninstallRequest = removalRequest with { ExpectedRevision = uninstallState.Revision };
        var frozenRemoval = new ComponentFrozenTarget(ComponentSourceMode.SystemPackage, null, null, InstalledPackage: old);
        var executeRemoval = new ComponentExecuteRequest("nginx", uninstallRequest, frozenRemoval);
        Check(NginxPackagePlan.PrecheckExecute(executeRemoval, true) is null,
            "A frozen removal carrying only the verified identity passes the no-host guards");
        Check(NginxPackagePlan.PrecheckExecute(executeRemoval with { Expected = frozenRemoval with { InstalledPackage = null } }, true) is
            { Problems: [ComponentProblemCodes.ExecutionNotRequested] },
            "A frozen removal without the installed identity is refused");
        Check(NginxPackagePlan.PrecheckFrozen(executeRemoval, uninstallState, uninstallResolution) is null,
            "An unchanged host accepts the exact installed identity it froze");
        Check(NginxPackagePlan.PrecheckFrozen(executeRemoval, uninstallState, uninstallResolution with { InstalledPackage = newer }) is
            { Problems: [ComponentProblemCodes.PackageExpectationChanged] },
            "A moved installed identity refuses the removal instead of removing the new version");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
