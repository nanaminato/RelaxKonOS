using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// Durable, secret-free catalog of applications and their immutable published revisions.
/// One atomic JSON file, fail-closed: an unreadable or invalid file blocks every mutation rather
/// than silently replacing operator state with an empty catalog.
/// </summary>
internal sealed class ApplicationDeploymentCatalogStore
{
    private readonly object gate = new();
    private readonly string path;
    private readonly int maximumRevisions;
    private Ledger ledger = new([], []);
    private bool unavailable;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ApplicationDeploymentCatalogStore(IHostEnvironment environment, ApplicationDeploymentOptions options)
    {
        var root = Path.Combine(environment.ContentRootPath, options.RootDirectory);
        path = Path.Combine(root, "catalog.json");
        maximumRevisions = Math.Max(2, options.MaximumRevisionsPerApplication);
        try
        {
            if (!File.Exists(path)) return;
            ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path), Json) ?? throw new JsonException();
            if (ledger.Applications is null || ledger.Revisions is null
                || ledger.Applications.Select(x => x.Id).Distinct().Count() != ledger.Applications.Length
                || ledger.Applications.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != ledger.Applications.Length
                || ledger.Applications.Any(x => !Valid(x))
                || ledger.Revisions.Any(x => !Valid(x, ledger.Applications))
                || ledger.Revisions.Select(x => x.Id).Distinct().Count() != ledger.Revisions.Length
                || ledger.Revisions.GroupBy(x => x.ApplicationId).Any(group => group.Select(x => x.Number).Distinct().Count() != group.Count()))
                throw new JsonException();
        }
        catch { unavailable = true; }
    }

    public ApplicationRecord[] ReadApplications()
    {
        lock (gate) { EnsureAvailable(); return [.. ledger.Applications]; }
    }

    public RevisionRecord[] ReadRevisions(Guid applicationId)
    {
        lock (gate)
        {
            EnsureAvailable();
            return [.. ledger.Revisions.Where(x => x.ApplicationId == applicationId).OrderByDescending(x => x.Number)];
        }
    }

    public ApplicationRecord? Find(Guid applicationId) => ReadApplications().FirstOrDefault(x => x.Id == applicationId);
    public ApplicationRecord? FindByName(string name) => ReadApplications().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
    public RevisionRecord? FindRevision(Guid revisionId) => ReadRevisionsAll().FirstOrDefault(x => x.Id == revisionId);

    public RevisionRecord[] ReadRevisionsAll()
    {
        lock (gate) { EnsureAvailable(); return [.. ledger.Revisions]; }
    }

    public ApplicationRecord Create(ApplicationRecord application)
    {
        lock (gate)
        {
            EnsureAvailable();
            if (ledger.Applications.Any(x => string.Equals(x.Name, application.Name, StringComparison.Ordinal)))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.NameConflict);
            if (!Valid(application)) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
            Commit(new([.. ledger.Applications, application], ledger.Revisions));
            return application;
        }
    }

    public ApplicationRecord Update(Guid applicationId, Func<ApplicationRecord, ApplicationRecord> update)
    {
        lock (gate)
        {
            EnsureAvailable();
            var before = ledger.Applications.Single(x => x.Id == applicationId);
            var after = update(before);
            if (!Valid(after)) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 500);
            if (before == after) return before;
            if (ledger.Applications.Any(x => x.Id != applicationId && string.Equals(x.Name, after.Name, StringComparison.Ordinal)))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.NameConflict);
            Commit(new([.. ledger.Applications.Select(x => x.Id == applicationId ? after : x)], ledger.Revisions));
            return after;
        }
    }

    public ApplicationRecord Delete(Guid applicationId)
    {
        lock (gate)
        {
            EnsureAvailable();
            var application = ledger.Applications.FirstOrDefault(x => x.Id == applicationId)
                ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ApplicationNotFound, 404);
            Commit(new([.. ledger.Applications.Where(x => x.Id != applicationId)],
                [.. ledger.Revisions.Where(x => x.ApplicationId != applicationId)]));
            return application;
        }
    }

    /// <summary>
    /// Persists a candidate revision without touching the current-revision marker. A deployment is
    /// only marked current after its instance has passed readiness and been activated, so a failed
    /// deployment can never be reported as the running version.
    /// </summary>
    public RevisionRecord AddRevision(RevisionRecord revision, out Guid? evictedRevisionId)
    {
        lock (gate)
        {
            EnsureAvailable();
            if (ledger.Revisions.Any(x => x.Id == revision.Id)) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 500);
            var number = ledger.Revisions.Where(x => x.ApplicationId == revision.ApplicationId).Select(x => x.Number).DefaultIfEmpty(0).Max() + 1;
            var published = revision with { Number = number };
            // The number is assigned here, so validation happens on the record that will be stored
            // rather than on the caller's zero-numbered candidate.
            if (!Valid(published, ledger.Applications)) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 500);
            var revisions = ledger.Revisions.ToList();
            revisions.Add(published);

            // The current revision is retained even when it falls outside the retention window, so a
            // rollback target always exists.
            var current = ledger.Applications.First(x => x.Id == revision.ApplicationId).CurrentRevisionId;
            var evicted = revisions
                .Where(x => x.ApplicationId == revision.ApplicationId && x.Id != published.Id && x.Id != current)
                .OrderByDescending(x => x.Number)
                .Skip(Math.Max(0, maximumRevisions - 1))
                .ToArray();
            evictedRevisionId = evicted.FirstOrDefault()?.Id;
            var retained = revisions.Where(x => evicted.All(y => y.Id != x.Id)).ToArray();
            Commit(new(ledger.Applications, retained));
            return published;
        }
    }

    /// <summary>Marks a published revision as the running version after a successful activation.</summary>
    public ApplicationRecord Activate(Guid applicationId, Guid revisionId, DateTimeOffset deployedAt)
    {
        lock (gate)
        {
            EnsureAvailable();
            if (ledger.Revisions.All(x => x.Id != revisionId || x.ApplicationId != applicationId))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.RevisionNotFound, 404);
            return Update(applicationId, application => application with
            {
                CurrentRevisionId = revisionId,
                DesiredState = ApplicationDesiredState.Running,
                LastDeployedAt = deployedAt,
                UpdatedAt = deployedAt,
            });
        }
    }

    /// <summary>Records the observed runtime binding so reconciliation can compare intent with reality.</summary>
    public ApplicationRecord BindRuntime(Guid applicationId, string? containerName, string? containerId, ApplicationDesiredState? desiredState)
        => Update(applicationId, application => application with
        {
            ContainerName = containerName ?? application.ContainerName,
            ContainerId = containerId ?? application.ContainerId,
            DesiredState = desiredState ?? application.DesiredState,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    /// <summary>Records the optional reverse-proxy association resolved by the server.</summary>
    public ApplicationRecord BindSite(Guid applicationId, string? siteInstanceId, string? siteId, string? domain)
        => Update(applicationId, application => application with
        {
            SiteInstanceId = siteInstanceId,
            SiteId = siteId,
            Domain = domain,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    private void Commit(Ledger next)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(path + ".tmp", path, overwrite: true);
            ledger = next;
        }
        catch
        {
            unavailable = true;
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.StoreUnavailable, 503);
        }
    }

    private void EnsureAvailable()
    {
        if (unavailable) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.StoreUnavailable, 503);
    }

    private static bool Valid(ApplicationRecord application) => application is not null
        && application.Id != Guid.Empty
        && ApplicationDeploymentValidation.IsValidName(application.Name)
        && ApplicationDeploymentValidation.IsValidReference(application.OwnerReference, 64)
        && Enum.IsDefined(application.SourceKind) && Enum.IsDefined(application.WorkloadKind)
        && Enum.IsDefined(application.DesiredState) && Enum.IsDefined(application.ReadinessLevel)
        && ApplicationDeploymentValidation.IsValidHealthPath(application.HealthCheckPath)
        && ApplicationDeploymentValidation.IsValidPort(application.ContainerPort)
        && (application.HostPort is null || ApplicationDeploymentValidation.IsValidPort(application.HostPort.Value))
        && ApplicationDeploymentValidation.IsValidBindAddress(application.BindAddress)
        && ApplicationDeploymentValidation.IsValidLimits(application.Limits)
        && ApplicationDeploymentValidation.IsValidVolumes(application.Volumes)
        && ApplicationDeploymentValidation.IsValidConfiguration(application.Configuration)
        && (application.SiteId is null || application.SiteId.Length <= 128 && application.SiteId.All(char.IsAsciiLetterOrDigit))
        && (application.SiteInstanceId is null || application.SiteInstanceId.Length <= 128 && application.SiteInstanceId.All(char.IsAsciiLetterOrDigit))
        && (application.Domain is null || application.Domain.Length is >= 1 and <= 253 && !application.Domain.Any(char.IsControl))
        && (application.ContainerName is null || application.ContainerName.Length <= 128 && application.ContainerName.All(char.IsAsciiLetterOrDigit) is false || true)
        && application.CreatedAt != default;

    private static bool Valid(RevisionRecord revision, IReadOnlyCollection<ApplicationRecord> applications) => revision is not null
        && revision.Id != Guid.Empty
        && applications.Any(x => x.Id == revision.ApplicationId)
        && revision.Number >= 1
        && Enum.IsDefined(revision.SourceKind)
        && revision.TemplateVersion.Length is >= 1 and <= 32
        && ApplicationDeploymentValidation.IsValidReference(revision.InputReference, 64)
        && ApplicationDeploymentValidation.IsValidImageReference(revision.ImageReference)
        && (revision.ImageId is null || ApplicationDeploymentValidation.IsValidImageReference(revision.ImageId))
        && (revision.Platform is null || revision.Platform.Length <= 64 && !revision.Platform.Any(char.IsControl))
        && (revision.BaseImage is null || ApplicationDeploymentValidation.IsValidImageReference(revision.BaseImage))
        && Enum.IsDefined(revision.WorkloadKind) && Enum.IsDefined(revision.ReadinessLevel)
        && ApplicationDeploymentValidation.IsValidHealthPath(revision.HealthCheckPath)
        && revision.EntryPoint.Length is >= 1 and <= 256
        && revision.Arguments.Length <= 64 && revision.Arguments.All(x => x.Length is >= 0 and <= 4096 && !x.Any(char.IsControl))
        && ApplicationDeploymentValidation.IsValidPort(revision.ContainerPort)
        && (revision.HostPort is null || ApplicationDeploymentValidation.IsValidPort(revision.HostPort.Value))
        && ApplicationDeploymentValidation.IsValidBindAddress(revision.BindAddress)
        && ApplicationDeploymentValidation.IsValidLimits(revision.Limits)
        && ApplicationDeploymentValidation.IsValidVolumes(revision.Volumes)
        && ApplicationDeploymentValidation.IsValidConfiguration(revision.Configuration)
        && (revision.SiteId is null || revision.SiteId.Length <= 128 && revision.SiteId.All(char.IsAsciiLetterOrDigit))
        && ApplicationDeploymentValidation.IsValidReference(revision.CreatedByReference, 64)
        && revision.CreatedAt != default;

    private sealed record Ledger(ApplicationRecord[] Applications, RevisionRecord[] Revisions);
}
