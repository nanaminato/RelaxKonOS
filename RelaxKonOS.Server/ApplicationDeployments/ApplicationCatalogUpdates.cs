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
        // New required fields, mount paths, listening ports or minimum resources need a separate
        // explicit definition edit. An update must never silently recreate credentials or data mounts.
        if (application.ContainerPort != target.ContainerPort || application.WorkloadKind != ApplicationWorkloadKind.Web
            || application.ReadinessLevel != ApplicationReadinessLevel.Http
            || target.Volumes.Any(required => !application.Volumes.Any(actual => actual.Name == required.Name && actual.ContainerPath == required.ContainerPath && !actual.ReadOnly))
            || target.Fields.Any(field => field.Required && !application.Configuration.Any(actual => actual.Name == field.EnvironmentName &&
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
        RequireCompatible(application, current, target);
        return target;
    }

    internal static void RequireCompatible(ApplicationRecord application, RevisionRecord? current, ApplicationCatalog.Entry target)
    {
        var preview = Preview(application, current, target, false);
        if (preview.Blockers.FirstOrDefault() is { } problem) throw new ApplicationDeploymentException(problem);
        ApplicationTemplateCatalog.RequirePinnedImage(target.ImageReference);
    }
}
