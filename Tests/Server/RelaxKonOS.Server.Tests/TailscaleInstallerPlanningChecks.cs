using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Installations;

internal static class TailscaleInstallerPlanningChecks
{
    internal static void Run()
    {
        var state = new ComponentState("tailscale", "windows-host", 42, RuntimeOwnership.Absent, ComponentSourceMode.NativeInstaller,
            new("windows", "x64", null, null, RuntimeInstallMode.NativeInstaller, "scm"), null, false);
        var release = new ComponentDownloadRelease("1.104.4", "win-x64", "https://pkgs.tailscale.com/stable/tailscale-setup-1.104.4-amd64.msi",
            "tailscale-setup-1.104.4-amd64.msi", "msi", new('a', 64));
        var request = new ComponentPlanRequest("tailscale", "windows-host", RuntimeChange.Install, 42,
            ComponentSourceMode.NativeInstaller, null, 0, 0, null, null, true, "tailscale-install-key");
        var plan = TailscaleInstallerPlan.Evaluate(request, state, release);
        Check(plan.Allowed && plan.Target is { Mode: ComponentSourceMode.NativeInstaller, Installer: not null, Package: null, Archive: null, Pin: null },
            "Windows Tailscale freezes a native MSI without representing it as an archive");
        foreach (var changed in new[] { request with { HostId = "another-host" }, request with { ExpectedRevision = 43 },
            request with { KeepData = false }, request with { SourceId = "unapproved" }, request with { Mode = ComponentSourceMode.ManagedArchive },
            request with { IdempotencyKey = "" }, request with { Change = RuntimeChange.Rollback }, request with { ExpectedTrustRevision = 1 } })
            Check(!TailscaleInstallerPlan.Evaluate(changed, state, release).Allowed, "Native Tailscale rejects changed identity, trust, retention or unsupported action");
        foreach (var candidate in new[] { release with { Rid = "win-arm64" }, release with { Format = "zip" },
            release with { Sha256 = "unknown" }, release with { DownloadUrl = "https://evil.example/tailscale.msi" },
            release with { Version = "1.105.0" } })
            Check(!TailscaleInstallerPlan.Evaluate(request, state, candidate).Allowed, "Native Tailscale rejects unapproved MSI artifact selectors");
        var managed = state with { Ownership = RuntimeOwnership.Managed, ActiveVersion = "1.104.2" };
        Check(TailscaleInstallerPlan.Evaluate(request with { Change = RuntimeChange.Update }, managed, release).Allowed,
            "Native Tailscale permits a real newer version for an owned installation");
        Check(!TailscaleInstallerPlan.Evaluate(request with { Change = RuntimeChange.Update }, managed with { ActiveVersion = release.Version }, release).Allowed,
            "Same-version reinstall cannot be claimed as an update");
        Check(TailscaleInstallerPlan.Evaluate(request with { Change = RuntimeChange.Uninstall }, managed with { ActiveVersion = release.Version }, release).Allowed,
            "Native Tailscale removal freezes the installed owned version and retains data");
        foreach (var change in new[] { RuntimeChange.Install, RuntimeChange.Update, RuntimeChange.Uninstall })
            Check(!TailscaleInstallerPlan.Evaluate(request with { Change = change }, managed with { Ownership = RuntimeOwnership.External }, release).Allowed,
                "External Tailscale instances cannot be taken over, updated or uninstalled implicitly");
        Check(!TailscaleInstallerPlan.Evaluate(request, state with { RecoveryRequired = true }, release).Allowed,
            "A pending native installer transaction prevents another mutation");
        Check(HostPackageDefinitions.Find("tailscale") is { PackageName: "tailscale", Service: "tailscaled.service", Executable: "/usr/bin/tailscale" },
            "Linux Tailscale has a closed host-owned package and service mapping");
        var target = new ComponentPackageTarget("tailscale", "1.104.4", "amd64", "official|resolute|main|amd64", "https://pkgs.tailscale.com/stable/ubuntu",
            "resolute", "main", "https://pkgs.tailscale.com/stable/ubuntu/pool/tailscale_1.104.4_amd64.deb", new('b', 64), 1024, []);
        var linuxRequest = request with { HostId = "linux-host", Mode = ComponentSourceMode.SystemPackage };
        ComponentPackageInstallations.Validate("tailscale", InstallationOperationKind.Install,
            new("tailscale", linuxRequest, ComponentFrozenTarget.FromPackage(target)), linuxRequest.IdempotencyKey);
        Check(true, "Linux Tailscale installation uses the existing frozen package ledger");
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
}
