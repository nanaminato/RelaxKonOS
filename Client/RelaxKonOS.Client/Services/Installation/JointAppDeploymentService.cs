using System.Collections.Immutable;
using System.Security.Cryptography;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;
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
        var scope = CaptureSessionScope();
        if (candidate.ServerArchive is { } archive && (scope is null || archive.AccountId != scope.AccountId
            || archive.ConnectionIdentity != scope.ConnectionIdentity || !IsCurrent(scope)))
            throw new InvalidOperationException("deployment-account-changed");
        var supported = ApplicationPlatformNames.Normalize(candidate.Manifest.ClientPlatforms);
        if (supported.Count > 0 && !supported.Contains(ApplicationCompatibilityService.DetectClientPlatform(), StringComparer.Ordinal))
            throw new InvalidOperationException("deployment-client-platform-unsupported");
        var optional = selectedOptional.Distinct().ToArray();
        var declaredOptional = candidate.Manifest.Dependencies?.Optional ?? [];
        if (optional.Any(requirement => !declaredOptional.Contains(requirement)))
            throw new InvalidOperationException("deployment-optional-selection-invalid");

        var digest = await ReadArtifactIdentityAsync(candidate, cancellationToken);
        var current = packages.FindInstalled(candidate.Manifest.Id);
        if (current?.Version != candidate.InstalledVersion
            || candidate.OfficialRelease is not null && current?.InstallationPath != candidate.ExpectedInstalledPath)
            throw new InvalidOperationException("component.state_revision_conflict");
        packages.EnsureReplacementVersionIsCompatible(candidate.Manifest.Id, candidate.Manifest.Version);
        candidate = candidate with { ExpectedSha256 = digest, ExpectedInstalledPath = current?.InstallationPath };
        var archivePath = candidate.DeleteWhenFinished || candidate.OfficialRelease is not null || candidate.CatalogAuthorization is not null
            ? null : Path.GetFullPath(candidate.LocalPath);

        var backend = new Backend(candidate, installer, packages, applications, binding => MatchesScope(binding, scope));
        var coordinator = new UnifiedDeploymentCoordinator(backend, journal, TimeProvider.System);
        var root = new DeploymentRelease(App(candidate.Manifest.Id), candidate.Manifest.Version, digest, false,
            [.. candidate.Manifest.Dependencies?.Required ?? []], ImmutableDictionary<string, string>.Empty)
        {
            CurrentVersion = candidate.InstalledVersion,
        };
        var runtimeManifest = RuntimeManifest(candidate.Manifest);
        var local = backend.InstalledReleases().ToArray();
        var localResolution = DeploymentResolver.Resolve(root, local, optional);
        if ((runtimeManifest.Server is { SupportedPlatforms.Count: 0, RequiredCapabilities.Count: 0 }
                || AllowsBundledLaunchRequirements(candidate, packages))
            && localResolution.ProblemPackage?.Kind != DeploymentPackageKind.ServerModule)
        {
            var plan = await coordinator.PlanAsync(LocalTarget, root, local, optional, cancellationToken);
            var localBinding = CreateBinding(Guid.Empty, "");
            RequireScope(localBinding, scope);
            return new(plan with { ClientBinding = localBinding }, candidate);
        }

        if (scope is null) throw new InvalidOperationException("deployment-account-changed");
        var binding = CreateBinding(scope.AccountId, scope.ConnectionIdentity);
        var client = NewModuleClient(binding, scope);
        var catalog = new ModuleDeploymentCatalog(client);
        var modules = new ModuleDeploymentExecutor(client);
        backend.BindModuleClient(client);
        var snapshot = await catalog.ReadAsync(cancellationToken);
        var resolution = await coordinator.PlanAsync(snapshot.Target, root,
            snapshot.Releases.Concat(local), optional, cancellationToken);
        RequireScope(binding, scope);
        var status = applications.EvaluateCompatibility(runtimeManifest).Status;
        if (status is not (ApplicationCompatibilityStatus.Compatible or ApplicationCompatibilityStatus.MissingServerCapability)
            || runtimeManifest.Server.RequiredCapabilities.Any(capability =>
                session.CurrentServer?.Capabilities.Contains(capability, StringComparer.Ordinal) != true
                && !resolution.OrderedReleases.Any(release => release.Package.Kind == DeploymentPackageKind.ServerModule
                    && release.Capabilities.ContainsKey(capability))))
            throw new InvalidOperationException("deployment-application-incompatible");
        resolution = resolution with { ClientBinding = binding };
        var prepared = await modules.PrepareAsync(resolution, snapshot, cancellationToken);
        RequireScope(binding, scope);
        return new(prepared, candidate);

        DeploymentClientBinding CreateBinding(Guid account, string identity) => new(account, identity, candidate.ExpectedInstalledPath)
        {
            ArchivePath = archivePath,
            RecoverFromOfficialCatalog = candidate.OfficialRelease is not null && candidate.CatalogAuthorization is null,
            ComponentRelease = ComponentReleaseOf(candidate),
            ServerArchive = candidate.ServerArchive,
        };
    }

    public async Task<Review?> FindPendingAsync(AppPackageCandidate candidate, CancellationToken cancellationToken,
        bool localOnly = false)
    {
        var scope = CaptureSessionScope();
        var digest = await ReadArtifactIdentityAsync(candidate, cancellationToken);
        var key = App(candidate.Manifest.Id);
        var componentRelease = ComponentReleaseOf(candidate);
        var plan = await journal.FindPendingPlanAsync(key, candidate.Manifest.Version,
            digest, Guid.Empty, "", cancellationToken, componentRelease, candidate.ServerArchive);
        if (plan is null && !localOnly && scope is { } remote && IsCurrent(remote))
            plan = await journal.FindPendingPlanAsync(key, candidate.Manifest.Version,
                digest, remote.AccountId, remote.ConnectionIdentity, cancellationToken, componentRelease, candidate.ServerArchive);
        if (plan?.ClientBinding is not { } binding) return null;
        if (!MatchesComponentRelease(binding, candidate, digest)
            || binding.ServerArchive != candidate.ServerArchive || !MatchesScope(binding, scope)) return null;
        return new(plan, candidate with
        {
            ExpectedSha256 = digest,
            InstalledVersion = plan.OrderedReleases[^1].CurrentVersion,
            ExpectedInstalledPath = binding.InstalledPath,
        });
    }

    private static DeploymentComponentRelease? ComponentReleaseOf(AppPackageCandidate candidate) =>
        candidate is { CatalogAuthorization: { } authorization, CatalogComponentId: { } componentId }
            ? new(authorization.SourceId, componentId, authorization.PublisherId, authorization.ReleaseIdentity) : null;

    private static bool MatchesComponentRelease(DeploymentClientBinding? binding, AppPackageCandidate candidate, string digest) =>
        binding?.ComponentRelease is not { } reviewed
        ? candidate.CatalogAuthorization is null
        : candidate.CatalogComponentId == reviewed.ComponentId
            && candidate.CatalogAuthorization is { } authorization
            && AppPackageInstallerService.MatchesReviewedComponentRelease(reviewed, digest, authorization);

    private static async Task<string> ReadArtifactIdentityAsync(AppPackageCandidate candidate, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(candidate.LocalPath);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (candidate.ExpectedSha256 is { } expected && digest != expected)
            throw new InvalidOperationException("component.package_expectation_changed");
        return digest;
    }

    public async Task<IReadOnlyList<AppPackageCandidate>> RecoverArchivesAsync(
        IReadOnlyList<OfficialDesktopRelease> releases, CancellationToken cancellationToken)
    {
        var scope = CaptureSessionScope();
        var recoveries = await journal.ReadPendingRecoveriesAsync(Guid.Empty, "", cancellationToken);
        if (scope is { } remote && IsCurrent(remote))
            recoveries = [.. recoveries, .. await journal.ReadPendingRecoveriesAsync(remote.AccountId, remote.ConnectionIdentity, cancellationToken)];

        var candidates = new List<AppPackageCandidate>();
        var recovered = new HashSet<(DeploymentPackageKey Package, string Digest, DeploymentComponentRelease? Source, DeploymentServerArchive? Server)>();
        foreach (var recovery in recoveries)
        {
            var plan = recovery.Plan;
            if (cancellationToken.IsCancellationRequested) break;
            if (plan.ClientBinding is not { } binding || !MatchesScope(binding, scope)) continue;
            var root = plan.OrderedReleases[^1];
            var identity = (root.Package, root.ArtifactIdentity, binding.ComponentRelease, binding.ServerArchive);
            if (recovered.Contains(identity)) continue;
            AppPackageCandidate? candidate = null;
            try
            {
                if (binding.ArchivePath is { } path && File.Exists(path))
                    candidate = await installer.InspectLocalAsync(path, cancellationToken: cancellationToken);
                else if (binding.RecoverFromOfficialCatalog
                    && releases.FirstOrDefault(item => App(item.AppId) == root.Package
                        && item.Version == root.Version && item.Sha256 == root.ArtifactIdentity) is { } release)
                    candidate = await installer.StageOfficialAsync(release, cancellationToken);
                else if (binding.ComponentRelease is { } component)
                    candidate = await installer.RecoverComponentArchiveAsync(component, root.ArtifactIdentity, cancellationToken);
                else if (binding.ServerArchive is { } archive)
                    candidate = await installer.StageServerPackageAsync(archive.Path, cancellationToken);
                if (candidate is null) continue;
                if (root.Package != App(candidate.Manifest.Id) || root.Version != candidate.Manifest.Version) continue;
                var digest = await ReadArtifactIdentityAsync(candidate, cancellationToken);
                if (digest == root.ArtifactIdentity && MatchesComponentRelease(binding, candidate, digest)
                    && binding.ServerArchive == candidate.ServerArchive && MatchesScope(binding, scope) && recovered.Add(identity))
                {
                    candidates.Add(candidate with { ExpectedSha256 = digest, IsRecoveredDeployment = true, RequiresDeploymentReview = recovery.RequiresReview });
                    candidate = null; // The installer queue now owns the staged archive.
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error) when (AppPackageInstallerService.IsStoreFailure(error) || error is RuntimePolicyException) { }
            finally { if (candidate is not null) installer.Discard(candidate); }
        }
        return candidates;
    }

    private sealed record SessionScope(SessionDto Login, Guid AccountId, string ConnectionIdentity);

    private SessionScope? CaptureSessionScope() =>
        session is { State: AuthSessionState.Authenticated, CurrentSession: { } login }
        && session.CurrentUser is { } user && session.ServiceId is { } connection
            ? new(login, user.Id, connection) : null;

    private bool IsCurrent(SessionScope scope) =>
        session.CurrentUser?.Id == scope.AccountId && session.ServiceId == scope.ConnectionIdentity
        && session.State == AuthSessionState.Authenticated && ReferenceEquals(session.CurrentSession, scope.Login);

    private bool MatchesScope(DeploymentClientBinding binding, SessionScope? scope)
    {
        var local = binding.AccountId == Guid.Empty && binding.ConnectionIdentity == "";
        if (local && binding.ServerArchive is null) return true;
        return scope is not null
            && (local || binding.AccountId == scope.AccountId && binding.ConnectionIdentity == scope.ConnectionIdentity)
            && (binding.ServerArchive is not { } archive
                || archive.AccountId == scope.AccountId && archive.ConnectionIdentity == scope.ConnectionIdentity)
            && IsCurrent(scope);
    }

    public async Task<DeveloperAppInfo> ExecuteAsync(Review review, CancellationToken cancellationToken)
    {
        var scope = CaptureSessionScope();
        var backend = new Backend(review.Candidate, installer, packages, applications, binding => MatchesScope(binding, scope));
        if (review.Plan.Target != LocalTarget)
            backend.BindModuleClient(NewModuleClient(review.Plan.ClientBinding, scope));
        var coordinator = new UnifiedDeploymentCoordinator(backend, journal, TimeProvider.System);
        await coordinator.ExecuteAsync(review.Plan, cancellationToken);
        var installed = backend.InstalledApplication()
            ?? throw new InvalidOperationException("deployment-application-not-registered");
        installer.Discard(review.Candidate);
        return installed;
    }

    private void RequireScope(DeploymentClientBinding? binding, SessionScope? scope)
    {
        if (binding is null || !MatchesScope(binding, scope))
            throw new InvalidOperationException("deployment-account-changed");
    }

    private ServerModuleManagementClient NewModuleClient(DeploymentClientBinding? binding, SessionScope? scope)
    {
        RequireScope(binding, scope);
        var client = new ServerModuleManagementClient(http.CreateClient(ServerModuleCatalog.HttpClientName), session);
        RequireScope(binding, scope);
        return client;
    }

    private static DeploymentPackageKey App(string id) => new(DeploymentPackageKind.Application, id);

    // These shipped applications need server capabilities at launch and install no server dependencies.
    internal static bool AllowsBundledLaunchRequirements(AppPackageCandidate candidate, DeveloperPackageManager packages) =>
        AllowsBundledLaunchRequirements(candidate.Manifest, candidate.ExpectedSha256, candidate.LocalPath, packages);

    internal static bool AllowsBundledLaunchRequirements(DeveloperPackageManifest manifest, string? digest, string path,
        DeveloperPackageManager packages) =>
        digest is not null
        && manifest.Dependencies?.Required is not { Count: > 0 }
        && manifest.ServerRequirements is { SupportedPlatforms.Count: 0 } requirements
        && (manifest.Id switch
        {
            "relaxkonos.taskmanager" => requirements.RequiredCapabilities.ToHashSet(StringComparer.Ordinal)
                .SetEquals([ServerCapabilities.Metrics, ServerCapabilities.Processes]),
            "relaxkonos.terminal" => requirements.RequiredCapabilities is [ServerCapabilities.Terminal],
            _ => false,
        })
        && string.Equals(Path.GetFullPath(path), Path.GetFullPath(packages.OfficialSource.BundledPath(manifest.Id)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static ApplicationManifest RuntimeManifest(DeveloperPackageManifest manifest) =>
        new(new AppId(manifest.Id), manifest.DisplayName, manifest.Version,
            ClientPlatforms: manifest.ClientPlatforms, ServerRequirements: manifest.ServerRequirements);

    private sealed class Backend(AppPackageCandidate candidate, AppPackageInstallerService installer,
        DeveloperPackageManager packages, ApplicationManager applications, Func<DeploymentClientBinding, bool> matchesScope) : IDeploymentBackend
    {
        private DeveloperAppInfo? installed;
        private ModuleDeploymentCatalog? catalog;
        private ModuleDeploymentExecutor? modules;

        private bool MatchesAccount(DeploymentPlan plan) => plan.ClientBinding is { } binding
            && binding.ServerArchive == candidate.ServerArchive && matchesScope(binding)
            && (plan.Target == LocalTarget ? binding.AccountId == Guid.Empty && binding.ConnectionIdentity == ""
                : binding.AccountId != Guid.Empty && binding.ConnectionIdentity.Length > 0);

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
                || root.ArtifactIdentity != candidate.ExpectedSha256
                || !MatchesComponentRelease(plan.ClientBinding, candidate, root.ArtifactIdentity))
                throw new InvalidOperationException("deployment-application-plan-changed");
            installer.RevalidateComponentAuthorization(candidate);
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
            VerifyInstalledApplicationDependencies(plan);
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
                {
                    await VerifyDependenciesAsync(plan, stepIndex, cancellationToken);
                    // Other local prerequisites can appear after this module in dependency order.
                    VerifyInstalledApplicationDependencies(plan);
                }
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
            // Keep staged bytes available until the coordinator has verified and journaled every step.
            installed = await installer.InstallAsync(candidate with { DeleteWhenFinished = false }, cancellationToken, async (manifest, ct) =>
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
                DesktopDependencyBinding? dependency = null;
                if (modules is not null && ModuleDeploymentExecutor.RequiredDesktopModules(plan,
                        manifest.Dependencies?.Required ?? []) is { Length: > 0 } requiredModules)
                {
                    var binding = plan.ClientBinding!;
                    dependency = new(binding.AccountId, binding.ConnectionIdentity, plan.Target.ServerIdentity, requiredModules);
                    await packages.QueueUncommittedDesktopDependencyAsync(manifest.Id, new(plan.Id, dependency), ct);
                    await modules.RegisterDesktopDependenciesAsync(plan, requiredModules, ct);
                }
                return new AppDeploymentReceipt(plan.Id, dependency);
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
                throw new DeploymentPlanReviewRequiredException("deployment-client-dependency-changed");
        }

        private void VerifyInstalledApplicationDependencies(DeploymentPlan plan)
        {
            foreach (var dependency in plan.OrderedReleases)
                if (dependency.Package.Kind == DeploymentPackageKind.Application && dependency.Satisfied)
                    VerifyInstalledDependency(dependency);
        }

        private void VerifyApplicationBaseline()
        {
            var current = packages.FindInstalled(candidate.Manifest.Id);
            if (current?.Version != candidate.InstalledVersion || current?.InstallationPath != candidate.ExpectedInstalledPath)
                throw new InvalidOperationException("component.state_revision_conflict");
        }
    }
}
