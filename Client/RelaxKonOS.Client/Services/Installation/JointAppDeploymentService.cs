using System.Collections.Immutable;
using System.Security.Cryptography;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Runtime;

namespace RelaxKonOS.Client.Services.AppPackages;

internal sealed class JointAppDeploymentService(IHttpClientFactory http, IAuthSession session,
    AppPackageInstallerService installer, DeveloperPackageManager packages, ApplicationManager applications,
    FileDeploymentJournal journal)
{
    internal sealed record Review(DeploymentPlan Plan, Backend Backend);

    public async Task<Review> PrepareAsync(AppPackageCandidate candidate, IEnumerable<DeploymentRequirement> selectedOptional,
        CancellationToken cancellationToken)
    {
        var actor = session.CurrentUser?.Id ?? throw new InvalidOperationException("deployment-account-changed");
        var connection = session.ServiceId ?? throw new InvalidOperationException("deployment-account-changed");
        var supported = ApplicationPlatformNames.Normalize(candidate.Manifest.ClientPlatforms);
        if (supported.Count > 0 && !supported.Contains(ApplicationCompatibilityService.DetectClientPlatform(), StringComparer.Ordinal))
            throw new InvalidOperationException("deployment-client-platform-unsupported");

        await using var stream = File.OpenRead(candidate.LocalPath);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (candidate.ExpectedSha256 is { } expected && digest != expected)
            throw new InvalidOperationException("component.package_expectation_changed");
        var current = packages.FindInstalled(candidate.Manifest.Id);
        if (current?.Version != candidate.InstalledVersion
            || candidate.OfficialRelease is not null && current?.InstallationPath != candidate.ExpectedInstalledPath)
            throw new InvalidOperationException("component.state_revision_conflict");
        candidate = candidate with { ExpectedSha256 = digest, ExpectedInstalledPath = current?.InstallationPath };

        var client = NewModuleClient();
        var catalog = new ModuleDeploymentCatalog(client);
        var modules = new ModuleDeploymentExecutor(client);
        var backend = new Backend(candidate, installer, packages, applications, catalog, modules, session);
        var snapshot = await catalog.ReadAsync(cancellationToken);
        var coordinator = new UnifiedDeploymentCoordinator(backend, journal, TimeProvider.System);
        var root = new DeploymentRelease(App(candidate.Manifest.Id), candidate.Manifest.Version, digest, false,
            [.. candidate.Manifest.Dependencies?.Required ?? []], ImmutableDictionary<string, string>.Empty)
        {
            CurrentVersion = candidate.InstalledVersion,
        };
        var resolution = await coordinator.PlanAsync(snapshot.Target, root,
            snapshot.Releases.Concat(backend.InstalledReleases()), selectedOptional, cancellationToken);
        var runtimeManifest = RuntimeManifest(candidate.Manifest);
        var status = applications.EvaluateCompatibility(runtimeManifest).Status;
        if (status is not (ApplicationCompatibilityStatus.Compatible or ApplicationCompatibilityStatus.MissingServerCapability)
            || runtimeManifest.Server.RequiredCapabilities.Any(capability =>
                session.CurrentServer?.Capabilities.Contains(capability, StringComparer.Ordinal) != true
                && !resolution.OrderedReleases.Any(release => release.Package.Kind == DeploymentPackageKind.ServerModule
                    && release.Capabilities.ContainsKey(capability))))
            throw new InvalidOperationException("deployment-application-incompatible");
        resolution = resolution with { ClientBinding = new(actor, connection, candidate.ExpectedInstalledPath) };
        return new(await modules.PrepareAsync(resolution, snapshot, cancellationToken), backend);
    }

    public async Task<Review?> FindPendingAsync(AppPackageCandidate candidate, CancellationToken cancellationToken)
    {
        if (session.CurrentUser is not { } user || session.ServiceId is not { } connection
            || candidate.ExpectedSha256 is not { } digest) return null;
        var plan = await journal.FindPendingPlanAsync(App(candidate.Manifest.Id), candidate.Manifest.Version,
            digest, user.Id, connection, cancellationToken);
        if (plan?.ClientBinding is not { } binding) return null;
        var client = NewModuleClient();
        var backend = new Backend(candidate with
        {
            InstalledVersion = plan.OrderedReleases[^1].CurrentVersion,
            ExpectedInstalledPath = binding.InstalledPath,
        }, installer, packages, applications, new(client), new(client), session);
        return new(plan, backend);
    }

    public async Task<DeveloperAppInfo> ExecuteAsync(Review review, CancellationToken cancellationToken)
    {
        await review.Backend.VerifyBeforeExecutionAsync(review.Plan, cancellationToken);
        review.Backend.BindModuleClient(NewModuleClient());
        var coordinator = new UnifiedDeploymentCoordinator(review.Backend, journal, TimeProvider.System);
        await coordinator.ExecuteAsync(review.Plan, cancellationToken);
        return review.Backend.InstalledApplication()
            ?? throw new InvalidOperationException("deployment-application-not-registered");
    }

    private ServerModuleManagementClient NewModuleClient() =>
        new(http.CreateClient(ServerModuleCatalog.HttpClientName), session);

    private static DeploymentPackageKey App(string id) => new(DeploymentPackageKind.Application, id);

    private static ApplicationManifest RuntimeManifest(DeveloperPackageManifest manifest) =>
        new(new AppId(manifest.Id), manifest.DisplayName, manifest.Version,
            ClientPlatforms: manifest.ClientPlatforms, ServerRequirements: manifest.ServerRequirements);

    internal sealed class Backend(AppPackageCandidate candidate, AppPackageInstallerService installer,
        DeveloperPackageManager packages, ApplicationManager applications, ModuleDeploymentCatalog catalog,
        ModuleDeploymentExecutor modules, IAuthSession session) : IDeploymentBackend
    {
        private DeveloperAppInfo? installed;
        private readonly Guid? actor = session.CurrentUser?.Id;
        private readonly string? connection = session.ServiceId;

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
            if (actor is null || session.CurrentUser?.Id != actor || session.ServiceId != connection
                || plan.ClientBinding is not { } binding || binding.AccountId != actor || binding.ConnectionIdentity != connection)
                throw new InvalidOperationException("deployment-account-changed");
            installed ??= await packages.FindDeploymentResultAsync(candidate.Manifest.Id, plan.Id, cancellationToken);
            if (installed is not null) return;
            VerifyApplicationBaseline();
            foreach (var dependency in plan.OrderedReleases)
                if (dependency.Package.Kind == DeploymentPackageKind.Application && dependency.Satisfied)
                    VerifyInstalledDependency(dependency);
            await using var archive = File.OpenRead(candidate.LocalPath);
            await AppPackageInstallerService.VerifyFrozenAsync(archive, candidate.ExpectedSha256, cancellationToken);
        }

        public async Task<DeploymentTarget> ObserveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await catalog.ObserveAsync();
        }

        public async Task ApplyOrReconcileAsync(DeploymentPlan plan, int stepIndex, CancellationToken cancellationToken)
        {
            var release = plan.OrderedReleases[stepIndex];
            if (release.Package.Kind == DeploymentPackageKind.ServerModule)
            {
                await modules.ApplyOrReconcileAsync(plan, release, cancellationToken);
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
                    || actor is null || session.CurrentUser?.Id != actor || session.ServiceId != connection)
                    throw new InvalidOperationException("deployment-application-plan-changed");
                VerifyApplicationBaseline();
                await VerifyDependenciesAsync(plan, ct);
                if (!applications.EvaluateCompatibility(RuntimeManifest(manifest)).IsCompatible)
                    throw new InvalidOperationException("deployment-application-incompatible");
                return plan.Id;
            });
        }

        public async Task VerifyAsync(DeploymentPlan plan, CancellationToken cancellationToken)
        {
            await VerifyDependenciesAsync(plan, cancellationToken);
            if (installed is null || packages.FindInstalled(installed.Id) != installed
                || applications.GetManifest(new AppId(installed.Id))?.Version != installed.Version)
                throw new InvalidOperationException("deployment-application-not-registered");
        }

        private async Task VerifyDependenciesAsync(DeploymentPlan plan, CancellationToken cancellationToken)
        {
            foreach (var dependency in plan.OrderedReleases)
            {
                if (dependency.Package.Kind == DeploymentPackageKind.ServerModule)
                    await modules.VerifyAsync(plan, dependency, cancellationToken);
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
