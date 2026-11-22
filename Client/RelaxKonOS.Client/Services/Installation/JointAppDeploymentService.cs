using System.Collections.Immutable;
using System.Security.Cryptography;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Runtime;

namespace RelaxKonOS.Client.Services.AppPackages;

internal sealed class JointAppDeploymentService(IHttpClientFactory http, IAuthSession session,
    AppPackageInstallerService installer, DeveloperPackageManager packages, ApplicationManager applications,
    FileDeploymentJournal journal)
{
    private static readonly DeploymentTarget LocalTarget = new("", "", "");
    internal sealed record Review(DeploymentPlan Plan, AppPackageCandidate Candidate);

    public async Task<Review> PrepareAsync(AppPackageCandidate candidate, IEnumerable<DeploymentRequirement> selectedOptional,
        CancellationToken cancellationToken)
    {
        var supported = ApplicationPlatformNames.Normalize(candidate.Manifest.ClientPlatforms);
        if (supported.Count > 0 && !supported.Contains(ApplicationCompatibilityService.DetectClientPlatform(), StringComparer.Ordinal))
            throw new InvalidOperationException("deployment-client-platform-unsupported");

        var digest = await ReadArtifactIdentityAsync(candidate, cancellationToken);
        var current = packages.FindInstalled(candidate.Manifest.Id);
        if (current?.Version != candidate.InstalledVersion
            || candidate.OfficialRelease is not null && current?.InstallationPath != candidate.ExpectedInstalledPath)
            throw new InvalidOperationException("component.state_revision_conflict");
        packages.EnsureReplacementVersionIsCompatible(candidate.Manifest.Id, candidate.Manifest.Version);
        candidate = candidate with { ExpectedSha256 = digest, ExpectedInstalledPath = current?.InstallationPath };

        var backend = new Backend(candidate, installer, packages, applications, session);
        var coordinator = new UnifiedDeploymentCoordinator(backend, journal, TimeProvider.System);
        var root = new DeploymentRelease(App(candidate.Manifest.Id), candidate.Manifest.Version, digest, false,
            [.. candidate.Manifest.Dependencies?.Required ?? []], ImmutableDictionary<string, string>.Empty)
        {
            CurrentVersion = candidate.InstalledVersion,
        };
        var runtimeManifest = RuntimeManifest(candidate.Manifest);
        var local = backend.InstalledReleases().ToArray();
        var optional = selectedOptional.ToArray();
        var localResolution = DeploymentResolver.Resolve(root, local, optional);
        if ((runtimeManifest.Server is { SupportedPlatforms.Count: 0, RequiredCapabilities.Count: 0 }
                || AllowsBundledLaunchRequirements(candidate, packages))
            && localResolution.ProblemPackage?.Kind != DeploymentPackageKind.ServerModule)
        {
            var plan = await coordinator.PlanAsync(LocalTarget, root, local, optional, cancellationToken);
            return new(plan with { ClientBinding = new(Guid.Empty, "", candidate.ExpectedInstalledPath) }, candidate);
        }

        var actor = session.CurrentUser?.Id ?? throw new InvalidOperationException("deployment-account-changed");
        var connection = session.ServiceId ?? throw new InvalidOperationException("deployment-account-changed");
        var binding = new DeploymentClientBinding(actor, connection, candidate.ExpectedInstalledPath);
        var client = NewModuleClient(binding);
        var catalog = new ModuleDeploymentCatalog(client);
        var modules = new ModuleDeploymentExecutor(client);
        backend.BindModuleClient(client);
        var snapshot = await catalog.ReadAsync(cancellationToken);
        var resolution = await coordinator.PlanAsync(snapshot.Target, root,
            snapshot.Releases.Concat(local), optional, cancellationToken);
        var status = applications.EvaluateCompatibility(runtimeManifest).Status;
        if (status is not (ApplicationCompatibilityStatus.Compatible or ApplicationCompatibilityStatus.MissingServerCapability)
            || runtimeManifest.Server.RequiredCapabilities.Any(capability =>
                session.CurrentServer?.Capabilities.Contains(capability, StringComparer.Ordinal) != true
                && !resolution.OrderedReleases.Any(release => release.Package.Kind == DeploymentPackageKind.ServerModule
                    && release.Capabilities.ContainsKey(capability))))
            throw new InvalidOperationException("deployment-application-incompatible");
        resolution = resolution with { ClientBinding = binding };
        return new(await modules.PrepareAsync(resolution, snapshot, cancellationToken), candidate);
    }

    public async Task<Review?> FindPendingAsync(AppPackageCandidate candidate, CancellationToken cancellationToken,
        bool localOnly = false)
    {
        var digest = await ReadArtifactIdentityAsync(candidate, cancellationToken);
        var key = App(candidate.Manifest.Id);
        var plan = await journal.FindPendingPlanAsync(key, candidate.Manifest.Version,
            digest, Guid.Empty, "", cancellationToken);
        if (!localOnly && plan is null && session.CurrentUser is { } user && session.ServiceId is { } connection)
            plan = await journal.FindPendingPlanAsync(key, candidate.Manifest.Version,
                digest, user.Id, connection, cancellationToken);
        if (plan?.ClientBinding is not { } binding) return null;
        return new(plan, candidate with
        {
            ExpectedSha256 = digest,
            InstalledVersion = plan.OrderedReleases[^1].CurrentVersion,
            ExpectedInstalledPath = binding.InstalledPath,
        });
    }

    private static async Task<string> ReadArtifactIdentityAsync(AppPackageCandidate candidate, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(candidate.LocalPath);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (candidate.ExpectedSha256 is { } expected && digest != expected)
            throw new InvalidOperationException("component.package_expectation_changed");
        return digest;
    }

    public async Task<DeveloperAppInfo> ExecuteAsync(Review review, CancellationToken cancellationToken)
    {
        var backend = new Backend(review.Candidate, installer, packages, applications, session);
        if (review.Plan.Target != LocalTarget)
            backend.BindModuleClient(NewModuleClient(review.Plan.ClientBinding));
        await backend.VerifyBeforeExecutionAsync(review.Plan, cancellationToken);
        var coordinator = new UnifiedDeploymentCoordinator(backend, journal, TimeProvider.System);
        await coordinator.ExecuteAsync(review.Plan, cancellationToken);
        return backend.InstalledApplication()
            ?? throw new InvalidOperationException("deployment-application-not-registered");
    }

    private ServerModuleManagementClient NewModuleClient(DeploymentClientBinding? binding)
    {
        var login = session.CurrentSession;
        RequireDeploymentLogin();
        var client = new ServerModuleManagementClient(http.CreateClient(ServerModuleCatalog.HttpClientName), session);
        RequireDeploymentLogin();
        return client;

        void RequireDeploymentLogin()
        {
            if (binding is null || login is null || !ReferenceEquals(login, session.CurrentSession)
                || session.State != AuthSessionState.Authenticated
                || binding.AccountId != session.CurrentUser?.Id || binding.ConnectionIdentity != session.ServiceId)
                throw new InvalidOperationException("deployment-account-changed");
        }
    }

    private static DeploymentPackageKey App(string id) => new(DeploymentPackageKind.Application, id);

    // These shipped applications need server capabilities at launch and install no server dependencies.
    internal static bool AllowsBundledLaunchRequirements(AppPackageCandidate candidate, DeveloperPackageManager packages) =>
        candidate.ExpectedSha256 is not null
        && candidate.Manifest.Dependencies?.Required is not { Count: > 0 }
        && candidate.Manifest.ServerRequirements is { SupportedPlatforms.Count: 0 } requirements
        && (candidate.Manifest.Id switch
        {
            "relaxkonos.taskmanager" => requirements.RequiredCapabilities.ToHashSet(StringComparer.Ordinal)
                .SetEquals([ServerCapabilities.Metrics, ServerCapabilities.Processes]),
            "relaxkonos.terminal" => requirements.RequiredCapabilities is [ServerCapabilities.Terminal],
            _ => false,
        })
        && string.Equals(Path.GetFullPath(candidate.LocalPath), Path.GetFullPath(packages.OfficialSource.BundledPath(candidate.Manifest.Id)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static ApplicationManifest RuntimeManifest(DeveloperPackageManifest manifest) =>
        new(new AppId(manifest.Id), manifest.DisplayName, manifest.Version,
            ClientPlatforms: manifest.ClientPlatforms, ServerRequirements: manifest.ServerRequirements);

    private sealed class Backend(AppPackageCandidate candidate, AppPackageInstallerService installer,
        DeveloperPackageManager packages, ApplicationManager applications, IAuthSession session) : IDeploymentBackend
    {
        private DeveloperAppInfo? installed;
        private ModuleDeploymentCatalog? catalog;
        private ModuleDeploymentExecutor? modules;

        private bool MatchesAccount(DeploymentPlan plan) => plan.ClientBinding is { } binding
            && (plan.Target == LocalTarget ? binding.AccountId == Guid.Empty && binding.ConnectionIdentity == ""
                : binding.AccountId == session.CurrentUser?.Id && binding.ConnectionIdentity == session.ServiceId);

        public IEnumerable<DeploymentRelease> InstalledReleases() => packages.InstalledRecords
            .Where(record => RuntimeSemanticVersion.TryParse(record.Version, out _)
                && applications.GetManifest(new AppId(record.Id))?.Version == record.Version)
            .Select(record => new DeploymentRelease(App(record.Id), record.Version, record.Path, true,
                [.. record.Dependencies?.Required ?? []], ImmutableDictionary<string, string>.Empty));

        public DeveloperAppInfo? InstalledApplication() => installed;

        public void BindModuleClient(ServerModuleManagementClient client)
        {
            catalog = new ModuleDeploymentCatalog(client);
            modules = new ModuleDeploymentExecutor(client);
        }

        public async Task VerifyBeforeExecutionAsync(DeploymentPlan plan, CancellationToken cancellationToken)
        {
            if (!MatchesAccount(plan))
                throw new InvalidOperationException("deployment-account-changed");
            if (plan.OrderedReleases.IsDefaultOrEmpty
                || plan.OrderedReleases[^1] is not { } root
                || root.Package != App(candidate.Manifest.Id) || root.Version != candidate.Manifest.Version
                || root.ArtifactIdentity != candidate.ExpectedSha256)
                throw new InvalidOperationException("deployment-application-plan-changed");
            installed ??= await packages.FindDeploymentResultAsync(candidate.Manifest.Id, plan.Id, cancellationToken);
            if (installed is not null)
            {
                if (installed.Version != root.Version
                    || applications.GetManifest(new AppId(root.Package.Id))?.Version != root.Version)
                    throw new InvalidOperationException("deployment-application-not-registered");
                return;
            }
            VerifyApplicationBaseline();
            packages.EnsureReplacementVersionIsCompatible(candidate.Manifest.Id, candidate.Manifest.Version);
            foreach (var dependency in plan.OrderedReleases)
                if (dependency.Package.Kind == DeploymentPackageKind.Application && dependency.Satisfied)
                    VerifyInstalledDependency(dependency);
            await using var archive = File.OpenRead(candidate.LocalPath);
            await AppPackageInstallerService.VerifyFrozenAsync(archive, candidate.ExpectedSha256, cancellationToken);
        }

        public async Task<DeploymentTarget> ObserveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return catalog is null ? LocalTarget : await catalog.ObserveAsync(cancellationToken);
        }

        public async Task ApplyOrReconcileAsync(DeploymentPlan plan, int stepIndex, CancellationToken cancellationToken)
        {
            var release = plan.OrderedReleases[stepIndex];
            if (release.Package.Kind == DeploymentPackageKind.ServerModule)
            {
                if (!release.Satisfied)
                    await VerifyDependenciesAsync(plan, stepIndex, cancellationToken);
                await (modules ?? throw new InvalidOperationException("deployment-server-unavailable"))
                    .ApplyOrReconcileAsync(plan, release, cancellationToken);
                return;
            }

            if (release.Satisfied)
            {
                VerifyInstalledDependency(release);
                return;
            }

            if (installed is not null) return;
            installed = await installer.InstallAsync(candidate, cancellationToken, async (manifest, ct) =>
            {
                if (manifest.Id != release.Package.Id || manifest.Version != release.Version
                    || candidate.ExpectedSha256 != release.ArtifactIdentity
                    || !MatchesAccount(plan))
                    throw new InvalidOperationException("deployment-application-plan-changed");
                VerifyApplicationBaseline();
                await VerifyDependenciesAsync(plan, stepIndex, ct);
                if (!(plan.Target == LocalTarget && AllowsBundledLaunchRequirements(candidate, packages))
                    && !applications.EvaluateCompatibility(RuntimeManifest(manifest)).IsCompatible)
                    throw new InvalidOperationException("deployment-application-incompatible");
                return plan.Id;
            });
        }

        public async Task VerifyAsync(DeploymentPlan plan, CancellationToken cancellationToken)
        {
            await VerifyDependenciesAsync(plan, plan.OrderedReleases.Length - 1, cancellationToken);
            if (installed is null || packages.FindInstalled(installed.Id) != installed
                || applications.GetManifest(new AppId(installed.Id))?.Version != installed.Version)
                throw new InvalidOperationException("deployment-application-not-registered");
        }

        private async Task VerifyDependenciesAsync(DeploymentPlan plan, int endIndex, CancellationToken cancellationToken)
        {
            for (var index = 0; index < endIndex; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dependency = plan.OrderedReleases[index];
                if (dependency.Package.Kind == DeploymentPackageKind.ServerModule)
                    await (modules ?? throw new InvalidOperationException("deployment-server-unavailable"))
                        .VerifyAsync(plan, dependency, cancellationToken);
                else if (dependency.Satisfied)
                    VerifyInstalledDependency(dependency);
            }
        }

        private void VerifyInstalledDependency(DeploymentRelease release)
        {
            var current = packages.FindInstalled(release.Package.Id);
            if (current?.InstallationPath != release.ArtifactIdentity || current.Version != release.Version
                || applications.GetManifest(new AppId(release.Package.Id))?.Version != release.Version)
                throw new InvalidOperationException("deployment-client-dependency-changed");
        }

        private void VerifyApplicationBaseline()
        {
            var current = packages.FindInstalled(candidate.Manifest.Id);
            if (current?.Version != candidate.InstalledVersion || current?.InstallationPath != candidate.ExpectedInstalledPath)
                throw new InvalidOperationException("component.state_revision_conflict");
        }
    }
}
