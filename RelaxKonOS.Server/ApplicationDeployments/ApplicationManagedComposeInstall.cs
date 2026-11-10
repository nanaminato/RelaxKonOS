using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.ApplicationDeployments;

internal sealed class ApplicationManagedComposeInstall(ApplicationDeploymentCatalogStore catalog,
    ApplicationDeploymentSecretStore secrets, ApplicationDeploymentRuntime runtime,
    ApplicationManagedComposeDispatch dispatch, ApplicationDeploymentOptions options)
{
    internal async Task DeployAsync(ApplicationRecord application, Guid operationId, string actor,
        IApplicationDeploymentProgress progress, CancellationToken ct)
    {
        var release = ApplicationCatalog.Require(application.CatalogTemplateId, application.CatalogTemplateVersion);
        if (!release.IsMultiService || application.HostPort is not { } port)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        var platform = await runtime.PreflightManagedComposeAsync(ct);
        if (runtime.ProbeBindAddress(application.BindAddress, port) is { } conflict) throw conflict;

        var plan = Compile(release, application.Id, port, application.Configuration);
        var values = plan.Services.ToDictionary(service => service.Id,
            _ => new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var field in application.Configuration)
        {
            var value = field.IsSecret ? secrets.Reveal(application.Id, field.Name, field.SecretVersion) : field.Value!;
            foreach (var binding in release.Fields.Single(definition => definition.Id == field.Name).Bindings)
                values[binding.Service].Add(binding.EnvironmentName, value);
        }
        var environment = values.ToDictionary(service => service.Key,
            service => (IReadOnlyDictionary<string, string>)service.Value, StringComparer.Ordinal);
        var definition = plan.RenderDeployment(out _, environment);
        var reference = DockerStackValidation.DefinitionVersion(definition.Name, definition.ComposeYaml);
        await progress.ReportAsync(new(DeploymentStage.Pulling, Cancellable: true), ct);
        var images = new List<ManagedServiceImage>();
        foreach (var service in plan.Services)
        {
            var identity = await runtime.ResolveImageIdentityAsync(service.ImageReference, platform, ct);
            if (identity.ImageId is null)
            {
                var pull = await runtime.PullAsync(service.ImageReference, null, ct, _ => { });
                if (!pull.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ImageNotFound);
                identity = await runtime.ResolveImageIdentityAsync(service.ImageReference, platform, ct);
            }
            if (identity.ImageId is null) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ImageNotFound);
            images.Add(new(service.Id, service.ImageReference, identity.ImageId));
        }
        var webImage = images.Single(image => image.Service == plan.WebService);
        var revision = catalog.AddRevision(new(Guid.NewGuid(), application.Id, 0, ApplicationSourceKind.Image,
            "managed-compose-v1", reference, webImage.ImageReference, webImage.ImageId, platform, null,
            application.WorkloadKind, application.ReadinessLevel, application.HealthCheckPath, "image default", [],
            application.ContainerPort, application.HostPort, application.BindAddress, application.Limits,
            application.Volumes, application.Configuration, application.SiteId, ApplicationDeploymentValidation.Reference(actor),
            DateTimeOffset.UtcNow, release.Id, release.Version, ManagedImages: [.. images]), out _);

        await progress.ReportAsync(new(DeploymentStage.Creating, Cancellable: false), ct);
        // The immutable revision binds both protected versions and public fields.
        var receipt = await dispatch.DispatchAsync(operationId, revision.Id, plan, environment, revision.Id.ToString("N"), ct);
        await progress.ReportAsync(new(DeploymentStage.HealthChecking, Cancellable: false), ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(options.HealthCheckTimeoutSeconds, 5, 3600));
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.HealthCheckIntervalSeconds, 1, 30));
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (receipt.State == DockerStackOperationState.Succeeded)
            {
                var web = await VerifyAsync(application, revision, plan, receipt, ct);
                if (web is not null)
                {
                    await progress.ReportAsync(new(DeploymentStage.Activating, Cancellable: false), ct);
                    catalog.Activate(application.Id, revision.Id, DateTimeOffset.UtcNow, (web.Name, web.Id));
                    return;
                }
            }
            else if (receipt.State is not (DockerStackOperationState.Queued or DockerStackOperationState.Running))
                throw new ApplicationDeploymentException(ApplicationManagedComposeDispatch.OutcomeUnknown);
            await Task.Delay(interval, ct);
            receipt = dispatch.ObserveOriginal(operationId) ?? throw new ApplicationDeploymentException(ApplicationManagedComposeDispatch.OutcomeUnknown);
        }
        throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.HealthCheckTimeout);
    }

    internal async Task<bool> RecoverAsync(DeploymentEntry entry, DockerStackOperationDto receipt, CancellationToken ct)
    {
        if (receipt.State != DockerStackOperationState.Succeeded || entry.Operation.TargetRevisionId is not { } revisionId
            || catalog.FindRevision(revisionId) is not { ManagedImages: not null, HostPort: { } port } revision
            || revision.InputReference != entry.Compose?.PlanReference
            || catalog.Find(entry.Operation.ApplicationId) is not { ManagedCompose: true } application
            || application.Id != revision.ApplicationId
            || application.CurrentRevisionId is { } current && current != revision.Id)
            return false;
        var release = ApplicationCatalog.Require(revision.CatalogTemplateId, revision.CatalogTemplateVersion);
        await runtime.PreflightManagedComposeAsync(ct);
        var plan = Compile(release, application.Id, port, revision.Configuration);
        var shape = plan.Services.ToDictionary(service => service.Id, service => (IReadOnlyDictionary<string, string>)
            service.EnvironmentNames.ToDictionary(name => name, _ => "", StringComparer.Ordinal), StringComparer.Ordinal);
        var definition = plan.RenderDeployment(out _, shape);
        if (DockerStackValidation.DefinitionVersion(definition.Name, definition.ComposeYaml) != revision.InputReference) return false;
        var web = await VerifyAsync(application, revision, plan, receipt, ct);
        if (web is null) return false;
        if (application.CurrentRevisionId != revision.Id || application.ContainerId != web.Id)
            catalog.Activate(application.Id, revision.Id, DateTimeOffset.UtcNow, (web.Name, web.Id));
        return true;
    }

    private static ManagedComposePlan Compile(ApplicationCatalog.Entry release, Guid id, int port, ApplicationConfigRecord[] configuration) =>
        release.CompileTopology(id, port, configuration.Select(field => field.Name).ToHashSet(StringComparer.Ordinal));

    private async Task<DockerContainerDetailsDto?> VerifyAsync(ApplicationRecord application, RevisionRecord revision,
        ManagedComposePlan plan, DockerStackOperationDto receipt, CancellationToken ct)
    {
        if (receipt.Services.Count != plan.Services.Count || revision.ManagedImages?.Length != plan.Services.Count) return null;
        DockerContainerDetailsDto? web = null;
        foreach (var service in plan.Services)
        {
            var containerName = plan.ContainerName(service.Id);
            var expected = revision.ManagedImages.SingleOrDefault(image => image.Service == service.Id);
            if (expected?.ImageReference != service.ImageReference || !receipt.Services.Any(observed =>
                observed.Service == service.Id && observed.Container == containerName && observed.State == "running"))
                return null;
            var details = await runtime.InspectAsync(containerName, ct);
            if (details is null || details.Name != containerName || string.IsNullOrWhiteSpace(details.Id)
                || details.State != "running" || details.ImageId != expected.ImageId
                || !ApplicationDeploymentValidation.IsOwnedBy(details.Labels, application.Id)
                || details.Labels.GetValueOrDefault(DockerStackValidation.ComposeProjectLabel) != plan.Project
                || details.Labels.GetValueOrDefault("com.docker.compose.service") != service.Id
                || !details.Networks.SequenceEqual([plan.NetworkName]))
                return null;
            var published = details.Ports.Where(binding => !binding.EndsWith(" → ", StringComparison.Ordinal));
            if (service.Id == plan.WebService)
            {
                if (!published.SequenceEqual([$"{service.ContainerPort}/tcp → 127.0.0.1:{plan.HostPort}"])) return null;
                web = details;
            }
            else if (published.Any()) return null;
        }
        return web is not null && await runtime.VerifyManagedComposeResourcesAsync(plan, ct)
            && await runtime.IsReadyAsync(application, web.Id, ct, requireSuccessStatus: true) ? web : null;
    }
}
