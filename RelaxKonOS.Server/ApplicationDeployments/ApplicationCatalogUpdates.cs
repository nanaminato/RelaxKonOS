using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>Updates replace only the trusted image and template binding; operator intent is never regenerated from defaults.</summary>
internal static class ApplicationCatalogUpdates
{
    public static CatalogApplicationUpdatePreviewDto Preview(ApplicationRecord application, RevisionRecord? current,
        ApplicationCatalog.Entry target, bool hasActiveOperation)
    {
        var blockers = new List<string>();
        var minimum = target.Describe().MinimumResources;
        if (application.CatalogTemplateId != target.Id || application.CatalogTemplateVersion is null || application.SourceKind != ApplicationSourceKind.Image)
            blockers.Add(ApplicationDeploymentProblemCodes.CatalogTemplateInvalid);
        if (application.CatalogTemplateVersion == target.Version) blockers.Add(ApplicationDeploymentProblemCodes.CatalogUpdateAlreadyCurrent);
        if (hasActiveOperation) blockers.Add(ApplicationDeploymentProblemCodes.ResourceConflict);
        if (current is null) blockers.Add(ApplicationDeploymentProblemCodes.RevisionNotFound);
        else if (current.Id != application.CurrentRevisionId || current.ApplicationId != application.Id
            || current.CatalogTemplateId != target.Id || current.CatalogTemplateVersion != application.CatalogTemplateVersion
            || current.Platform != "linux/amd64" || current.ImageId is not { Length: > 0 }
            || current.ImageReference == target.ImageReference
            || target.UpgradeFrom?.Any(source => source.Version == current.CatalogTemplateVersion
                && source.ImageReference == current.ImageReference) != true)
            blockers.Add(ApplicationDeploymentProblemCodes.CatalogUpdateSourceUnverified);

        if (target.IsMultiService || application.ContainerPort != target.ContainerPort || application.WorkloadKind != ApplicationWorkloadKind.Web
            || application.ReadinessLevel != ApplicationReadinessLevel.Http
            || application.HealthCheckPath != target.HealthPath || application.DesiredState == ApplicationDesiredState.Retained
            || !MatchesVolumes(application.Volumes, target.Volumes)
            || current is not null && !MatchesVolumes(current.Volumes, target.Volumes)
            || target.Fields.Any(field => field.Required && !application.Configuration.Any(actual => actual.Name == field.Bindings.Single().EnvironmentName &&
                (field.Type == "secret" ? actual.IsSecret && actual.SecretVersion > 0 : !actual.IsSecret && !string.IsNullOrWhiteSpace(actual.Value))))
            || minimum.CpuCores is { } minCpu && application.Limits.CpuCores is { } cpu && cpu < minCpu
            || minimum.MemoryBytes is { } minMemory && application.Limits.MemoryBytes is { } memory && memory < minMemory
            || minimum.PidsLimit is { } minPids && application.Limits.PidsLimit is { } pids && pids < minPids)
            blockers.Add(ApplicationDeploymentProblemCodes.CatalogUpdateDefinitionIncompatible);
        return new(application.Id, application.UpdatedAt, application.CurrentRevisionId, application.CatalogTemplateVersion ?? string.Empty,
            target.Describe(), current?.ImageReference, target.ImageReference, target.UpdateNotes, blockers);
    }

    public static ApplicationCatalog.Entry Validate(ApplicationRecord application, RevisionRecord? current, UpdateCatalogApplicationRequest request)
    {
        if (!request.Confirmed) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ConfirmationRequired, 400);
        if (request.ExpectedUpdatedAt == default) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        if (application.UpdatedAt != request.ExpectedUpdatedAt || application.CurrentRevisionId != request.ExpectedRevisionId || application.CatalogTemplateVersion != request.CurrentTemplateVersion)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.DefinitionConflict);
        var target = ApplicationCatalog.Require(request.TemplateId, request.TemplateVersion);
        if (request.ExpectedTargetImageReference != target.ImageReference)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.DefinitionConflict);
        RequireCompatible(application, current, target);
        return target;
    }

    internal static void RequireCompatible(ApplicationRecord application, RevisionRecord? current, ApplicationCatalog.Entry target)
    {
        var preview = Preview(application, current, target, false);
        if (preview.Blockers.FirstOrDefault() is { } problem) throw new ApplicationDeploymentException(problem);
        ApplicationTemplateCatalog.RequirePinnedImage(target.ImageReference);
    }

    private static bool MatchesVolumes(IReadOnlyList<ApplicationVolumeRecord> volumes, IReadOnlyList<ApplicationVolumeDto> expected) =>
        volumes.Count == expected.Count && expected.All(required => volumes.Any(actual =>
            actual.Name == required.Name && actual.ContainerPath == required.ContainerPath && actual.ReadOnly == required.ReadOnly));
}
