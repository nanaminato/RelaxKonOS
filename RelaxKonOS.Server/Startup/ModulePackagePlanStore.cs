using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

internal sealed record ModulePackagePlan(Guid PlanId, string Actor, string SourceId, string ReleaseId, string UploadId,
    string ModuleId, string Version, string PublisherId, string ArtifactSha256, string ManifestSha256,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, Guid? AppliedOperationId,
    string Runtime, ModulePackagePrivilege[] Privileges, ModulePackageUi Ui);

internal sealed record ModulePackagePlanDocument(int SchemaVersion, ModulePackagePlan[] Plans);

/// <summary>Short-lived frozen package install plan. It binds the upload, release, module version and
/// digests so apply cannot silently install a different package.</summary>
internal static class ModulePackagePlanStore
{
    private const int Limit = 1024 * 1024;
    private const int Maximum = 1000;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    internal static string PathFor(string contentRoot) =>
        Path.Combine(contentRoot, "data", "module-management", "package-plans.json");

    internal static ModulePackagePlan Create(string contentRoot, string actor, string uploadId,
        AcceptedModulePackageCandidate candidate, DateTimeOffset now)
    {
        var plans = Read(contentRoot).Where(plan => plan.ExpiresAt > now.AddHours(-1)).ToList();
        if (plans.Count >= Maximum) throw new ModulePackageException("module-package-plan-capacity");
        var plan = new ModulePackagePlan(Guid.NewGuid(), actor, candidate.SourceId, candidate.ReleaseId, uploadId,
            candidate.Manifest.ModuleId, candidate.Manifest.Version, candidate.Manifest.PublisherId,
            candidate.ArchiveSha256, candidate.ManifestSha256, now, now.Add(Lifetime), null, candidate.Manifest.Runtime, candidate.Manifest.Privileges, candidate.Manifest.Ui);
        plans.Add(plan);
        Write(contentRoot, plans);
        return plan;
    }

    internal static ModulePackagePlan Get(string contentRoot, Guid planId, string actor, DateTimeOffset now)
    {
        var plan = Read(contentRoot).SingleOrDefault(item => item.PlanId == planId)
            ?? throw new ModulePackageException("module-package-plan-not-found");
        if (!string.Equals(plan.Actor, actor, StringComparison.Ordinal))
            throw new ModulePackageException("module-package-plan-forbidden");
        if (plan.ExpiresAt <= now) throw new ModulePackageException("module-package-plan-expired");
        return plan;
    }

    internal static ModulePackagePlan MarkApplied(string contentRoot, Guid planId, Guid operationId, DateTimeOffset now)
    {
        var plans = Read(contentRoot).ToList();
        var index = plans.FindIndex(plan => plan.PlanId == planId);
        if (index < 0) throw new ModulePackageException("module-package-plan-not-found");
        if (plans[index].ExpiresAt <= now) throw new ModulePackageException("module-package-plan-expired");
        if (plans[index].AppliedOperationId is { } existing && existing != operationId)
            throw new ModulePackageException("module-package-plan-already-applied");
        plans[index] = plans[index] with { AppliedOperationId = operationId };
        Write(contentRoot, plans);
        return plans[index];
    }

    internal static IReadOnlyList<ModulePackagePlan> Read(string contentRoot)
    {
        var path = PathFor(contentRoot);
        if (!File.Exists(path)) return [];
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > Limit) throw Invalid();
        ModulePackagePlanDocument? document;
        try { document = JsonSerializer.Deserialize<ModulePackagePlanDocument>(File.ReadAllBytes(path), RelaxKonOSJsonOptions.Default); }
        catch (JsonException) { throw Invalid(); }
        if (document is null || document.SchemaVersion != 2 || document.Plans is not { Length: <= Maximum }
            || document.Plans.Select(plan => plan.PlanId).Distinct().Count() != document.Plans.Length
            || document.Plans.Any(plan => plan is null || plan.PlanId == Guid.Empty || plan.Actor is not { Length: > 0 and <= 256 }
                || !Identifier(plan.SourceId) || !Identifier(plan.ReleaseId) || plan.UploadId is not { Length: 32 }
                || !plan.UploadId.All(char.IsAsciiHexDigitLower) || !Identifier(plan.ModuleId)
                || !Version(plan.Version) || !Identifier(plan.PublisherId) || !Digest(plan.ArtifactSha256)
                || !Digest(plan.ManifestSha256) || plan.ExpiresAt <= plan.CreatedAt
                || plan.Runtime is not ("in-process" or "module-host") || plan.Privileges is not { Length: <= 128 } || plan.Ui is null
                || plan.AppliedOperationId == Guid.Empty))
            throw Invalid();
        return document.Plans;
    }

    private static void Write(string contentRoot, IReadOnlyList<ModulePackagePlan> plans)
    {
        if (plans.Count > Maximum) throw new ModulePackageException("module-package-plan-capacity");
        var path = PathFor(contentRoot);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new ModulePackagePlanDocument(2, plans.ToArray()), RelaxKonOSJsonOptions.Default);
            if (bytes.Length > Limit) throw new ModulePackageException("module-package-plan-capacity");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       65536, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (OperatingSystem.IsLinux() && Environment.UserName == "root")
            {
                var state = Path.Combine(directory, "state.json");
                if (File.Exists(state)) ModuleManagementStore.PreserveUnixOwner(temporary, state);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 96 } && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    private static bool Version(string? value) => System.Version.TryParse(value, out var parsed)
        && parsed.Build >= 0 && parsed.Revision == -1 && parsed.ToString(3) == value;
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static ModulePackageException Invalid() => new("module-package-plan-state-invalid");
}
