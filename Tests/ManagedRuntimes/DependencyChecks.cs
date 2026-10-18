using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;

/// <summary>
/// Dependency preflight: the requirements one signed release implies, the three answers a Host can give,
/// and the rule that a report measured for one release never admits another. Measurement is faked here;
/// the real environment reads /proc, the drive table and the service host and is exercised by the
/// Debian run of the same executable.
/// </summary>
internal static class DependencyChecks
{
    private sealed class Host : IRuntimeDependencyEnvironment
    {
        internal string? PlatformValue { get; set; } = "linux/amd64";
        internal string? ServiceHostValue { get; set; } = "systemd";
        internal long? Free { get; set; } = 4L * 1024 * 1024 * 1024;
        internal bool? Private { get; set; } = true;
        internal bool? Ipv6 { get; set; } = true;
        internal List<string> Asked { get; } = [];

        public string? Platform => PlatformValue;
        public string? ServiceHost => ServiceHostValue;
        public long? FreeBytes(string path) { Asked.Add(path); return Free; }
        public bool? PrivateDirectory(string path) => Private;
        public bool? KernelFeature(string identity) => identity == "ipv6" ? Ipv6 : null;
    }

    internal static int Run()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var now = DateTimeOffset.Parse("2026-10-18T00:00:00Z");
        var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
        var artifact = new RuntimeArtifact("immutable-2", new string('a', 64), 64L * 1024 * 1024, "tar.gz", "sing-box",
            "signed-manifest:key-1", new(256L * 1024 * 1024, 8, [new("sing-box", 4, new string('b', 64), true)]));
        var release = new RuntimeRelease("opaque-2", "sing-box", "v2.0.0", "2.0.0", RuntimeChannel.Stable,
            "sing-box-official", target, artifact, "sing-box-v1", true, RuntimeTrust.Confirmed,
            RuntimeEvidence.Unverified, now);
        var environment = new Host();
        const string root = "/var/lib/relaxkonos/sing-box";

        var requirements = RuntimeArchiveDependencies.For(release);
        Check(requirements.Select(r => r.Kind).SequenceEqual([
            RuntimeDependencyKind.Platform, RuntimeDependencyKind.ServiceHost, RuntimeDependencyKind.DiskSpace,
            RuntimeDependencyKind.FileSystem]),
            "A release requires exactly the platform, service host, free space and private root the Host can measure");
        Check(requirements[0].Identity == "linux/amd64" && requirements[1].Identity == "systemd",
            "The platform and service host identities are the declared target, never a guess from a version string");
        Check(requirements[2].MinimumBytes == artifact.MaximumBytes + artifact.Layout.MaximumExpandedBytes
            + RuntimeArchiveDependencies.ReservedBytes,
            "Free space covers the download, the exact expanded layout and a reserve for the atomic switch");
        Check(requirements.All(r => !r.Identity.Contains('/') || r.Kind == RuntimeDependencyKind.Platform)
            && requirements.All(r => !r.Identity.Contains(root, StringComparison.Ordinal)),
            "Requirements name capabilities, never a filesystem path or a command");

        var satisfied = RuntimeDependencyProbe.For(requirements, environment, root);
        Check(satisfied.Satisfies(requirements) && satisfied.Unmet.Count == 0,
            "A Host where every measured fact matches satisfies the release");
        Check(satisfied.Observations.Single(o => o.Kind == RuntimeDependencyKind.DiskSpace).AvailableBytes
            == environment.Free,
            "The report carries the measured free bytes instead of only a verdict");

        environment.PlatformValue = "linux/arm64";
        var platform = RuntimeDependencyProbe.For(requirements, environment, root);
        Check(!platform.Satisfies(requirements)
            && platform.Unmet.Single().Kind == RuntimeDependencyKind.Platform
            && platform.Unmet.Single().Measured == "linux/arm64"
            && platform.Unmet.Single().State == RuntimeDependencyState.Missing,
            "A different Host architecture is Missing and names what the Host actually is");
        environment.PlatformValue = "linux/amd64";

        environment.ServiceHostValue = null;
        var noServiceHost = RuntimeDependencyProbe.For(requirements, environment, root);
        Check(noServiceHost.Unmet.Single().State == RuntimeDependencyState.Unknown
            && noServiceHost.Unmet.Single().Kind == RuntimeDependencyKind.ServiceHost,
            "An absent service host is Unknown, not a guessed match");
        environment.ServiceHostValue = "systemd";

        environment.Free = artifact.MaximumBytes;
        var full = RuntimeDependencyProbe.For(requirements, environment, root);
        Check(full.Unmet.Single().Kind == RuntimeDependencyKind.DiskSpace
            && full.Unmet.Single().AvailableBytes == artifact.MaximumBytes,
            "A disk that fits the download but not the expanded layout is refused before anything is fetched");
        environment.Free = null;
        var unmeasured = RuntimeDependencyProbe.For(requirements, environment, root);
        Check(unmeasured.Unmet.Single().State == RuntimeDependencyState.Unknown,
            "An unreadable drive is Unknown rather than a silent pass");
        environment.Free = 4L * 1024 * 1024 * 1024;

        environment.Private = false;
        Check(RuntimeDependencyProbe.For(requirements, environment, root).Unmet.Single().Kind
            == RuntimeDependencyKind.FileSystem, "A managed root that is not a private directory blocks the release");
        environment.Private = true;
        Check(environment.Asked.All(path => path == root),
            "The probe measures the managed root it was given, not an arbitrary path");

        // Exact coverage: a report for another release, or a manually shortened one, cannot admit this one.
        var other = RuntimeArchiveDependencies.For(release with { Target = target with { Architecture = "arm64" } });
        Check(!RuntimeDependencyProbe.For(other, environment, root).Satisfies(requirements),
            "A report measured for another platform never satisfies this release");
        Check(!new RuntimeDependencyReport([satisfied.Observations[0]]).Satisfies(requirements),
            "A partial report never satisfies the release it was not measured for");
        Check(new RuntimeDependencyReport([.. satisfied.Observations,
            new(RuntimeDependencyKind.KernelFeature, "ipv6", RuntimeDependencyState.Satisfied)]).Satisfies(requirements)
            == false,
            "An extra observation breaks exact coverage instead of widening the requirement");

        // Only an IPv6 listener adds a kernel requirement; an IPv4-only configuration adds nothing.
        var ipv4 = new ProxyServerConfiguration(1, [Inbound("0.0.0.0")]);
        var ipv6 = new ProxyServerConfiguration(1, [Inbound("::")]);
        Check(RuntimeArchiveDependencies.ForConfiguration(ipv4).Count == 0,
            "An IPv4-only configuration adds no kernel requirement");
        var kernel = RuntimeArchiveDependencies.ForConfiguration(ipv6);
        Check(kernel.Count == 1 && kernel[0].Kind == RuntimeDependencyKind.KernelFeature && kernel[0].Identity == "ipv6",
            "An IPv6 listener requires the Host to have IPv6");
        environment.Ipv6 = false;
        Check(RuntimeDependencyProbe.For(kernel, environment, root).Unmet.Single().State == RuntimeDependencyState.Missing,
            "A Host with IPv6 disabled refuses a configuration that binds an IPv6 address");
        environment.Ipv6 = null;
        Check(RuntimeDependencyProbe.For(kernel, environment, root).Unmet.Single().State == RuntimeDependencyState.Unknown,
            "An unreadable kernel setting is Unknown, never assumed enabled");
        environment.Ipv6 = true;

        // Planning refuses without a measurement, and reports the failing fact with the refusal.
        var installation = new RuntimeInstallation(Guid.NewGuid(), "host-1", "sing-box", 5,
            RuntimeOwnership.Managed, target, "opaque-1", false);
        var source = new RuntimeSourceIdentity("sing-box-official", "SagerNet/sing-box", true, RuntimeTrust.Confirmed, 3);
        var catalog = new RuntimeCatalog(source.SourceId, 7, 3, now.AddHours(1), true, [release]);
        var request = new RuntimePlanRequest(installation.InstallationId, installation.HostId, RuntimeChange.Update,
            5, 7, 3, release.ReleaseId, "dependency-operation");
        environment.Free = artifact.MaximumBytes;
        var blocked = Plan(request, installation, catalog, RuntimeDependencyProbe.For(requirements, environment, root));
        environment.Free = 4L * 1024 * 1024 * 1024;
        Check(blocked.Plan is null && blocked.Problems.SequenceEqual(["runtime.dependencies_unsatisfied"])
            && blocked.Dependencies!.Unmet.Single().Kind == RuntimeDependencyKind.DiskSpace,
            "A refused plan says the dependencies were not met and returns the measurement that failed");
        Check(Plan(request, installation, catalog, null).Problems.Contains("runtime.dependencies_unsatisfied"),
            "A plan without any measurement is refused; nothing is assumed to be fine");
        var allowed = Plan(request, installation, catalog, satisfied);
        Check(allowed.Allowed && allowed.Dependencies == satisfied,
            "A satisfied measurement lets the release through and travels with the frozen plan");

        // Uninstall may not need a source, and it does not need a measurement either.
        var removal = new RuntimePlanRequest(installation.InstallationId, installation.HostId, RuntimeChange.Uninstall,
            5, 0, 0, null, "dependency-removal");
        Check(Plan(removal, installation, catalog, null).Allowed,
            "Removal stays possible when nothing can be measured, because it needs no release");

        // The read-only snapshot states the environment without any release in hand.
        var facts = RuntimeDependencyProbe.Environment(environment, root);
        Check(facts.Observations.Select(o => o.Kind).SequenceEqual([
            RuntimeDependencyKind.Platform, RuntimeDependencyKind.ServiceHost, RuntimeDependencyKind.DiskSpace,
            RuntimeDependencyKind.FileSystem, RuntimeDependencyKind.KernelFeature]),
            "The snapshot reports every environment fact once, in a stable order");
        Check(facts.Observations.All(o => o.State == RuntimeDependencyState.Satisfied)
            && facts.Observations.Single(o => o.Kind == RuntimeDependencyKind.Platform).Identity == "linux/amd64",
            "A healthy Host shows present facts instead of requirements nobody asked for");
        environment.PlatformValue = null;
        Check(RuntimeDependencyProbe.Environment(environment, root).Observations[0] is
        { Identity: "unsupported", State: RuntimeDependencyState.Unknown },
            "An unsupported Host says so instead of displaying an empty platform");

        // The real environment must answer on the platforms this Helper ships for: a null everywhere would
        // silently turn every install into "dependencies unknown" instead of surfacing a broken probe.
        var scratch = Path.Combine(Path.GetTempPath(), "relaxkonos-runtime-environment");
        Directory.CreateDirectory(scratch);
        try
        {
            var real = new HostDependencyEnvironment();
            Check(real.Platform is { Length: > 0 }, "The real Host reports its own platform identity");
            Check(real.FreeBytes(scratch) is > 0, "The real Host can measure free space for a directory it owns");
            Check(real.PrivateDirectory(scratch) is true, "A plain directory is usable as the managed root");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(scratch, "link"), scratch);
                Check(real.PrivateDirectory(Path.Combine(scratch, "link")) is false,
                    "A linked managed root is refused instead of silently followed");
            }
            // Creating a link needs a privilege an unprivileged dev box may not hold; the rule itself is
            // proven above by the fake, so an unavailable link is not a failure of this probe.
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        finally { Directory.Delete(scratch, recursive: true); }

        return count;

        RuntimePlanResult Plan(RuntimePlanRequest ask, RuntimeInstallation state, RuntimeCatalog from,
            RuntimeDependencyReport? dependencies) => RuntimePlanning.Create(ask, state, source, from, 7, now, dependencies);
    }

    private static ProxyServerInbound Inbound(string address) => new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "Proxy", ProxyServerProtocol.VlessReality, address, 8443, "proxy.example.com", 8443, "proxy.example.com",
        Guid.NewGuid(), "www.example.com", 443);
}
