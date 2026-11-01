using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Startup;

internal sealed record ModulePrivilegeIdentity(string ModuleId, string Version, string PublisherId, string ArtifactSha256,
    string ManifestSha256, string PolicyRevision, ModulePackageCapability[] Privileges, string[] AllowedServices);
internal sealed record ModulePrivilegeAdministrator(string Identity, string AuthenticationMethod);

/// <summary>The Host-approved source reference plus the exact artifact identity the Helper resolved. Both are
/// frozen at prepare time: the Helper re-resolves and compares them before executing, never re-planning.</summary>
internal sealed record ModulePrivilegePackage(string SourceRef, ModulePackageResolution Resolution);

/// <summary>One frozen configuration file. Content itself is never part of this record: only its digest and
/// length are persisted, and the bytes are held transiently for the one approved publication.</summary>
internal sealed record ModulePrivilegeFile(string Root, string Path, ModuleFileAction Action, ModuleFileState Expected,
    string ContentSha256, long ContentBytes, int Mode);

/// <summary>One frozen data root. The digest covers every regular file and is verified again by the Helper.</summary>
internal sealed record ModulePrivilegeData(string Root, ModuleDataAction Action, ModuleDataState Expected,
    string Scope, string? BackupRef);

/// <summary>What one confirmed plan dispatches. Content is the transient file body, if any.</summary>
internal sealed record ModulePrivilegeExecution(ModulePrivilegeRequest Request, ModulePrivilegePackage? Package,
    ModulePrivilegeFile? File, ModulePrivilegeData? Data, byte[]? Content);

internal sealed class ModulePrivilegeException(string code, int status = 409) : Exception(code)
{
    internal string Code => Message;
    internal int Status => status;
}

/// <summary>Durable single-use confirmation gate. Recovery observes an interrupted operation, never replays it.</summary>
internal sealed class ModulePrivilegePlanStore
{
    private sealed record Plan(Guid Id, string Session, ModulePrivilegeIdentity Identity, string IdentityHash,
        ModulePrivilegeRequest Request, string RequestHash, ModulePrivilegePackage? Package, string PackageHash,
        ModulePrivilegeFile? File, string FileHash, ModulePrivilegeData? Data, string DataHash,
        string Hash, DateTimeOffset Created, DateTimeOffset Expires, string State, string? Problem, string? TokenHash,
        DateTimeOffset Updated, ApprovalAudit Audit);
    private sealed record ApprovalAudit(string InitiatingUser, string? ApprovingUser, ModulePrivilegeAdministrator? Administrator,
        DateTimeOffset? ConfirmedAt, DateTimeOffset? AttemptCompletedAt);
    private sealed record Document(int SchemaVersion, Plan[] Plans);
    private const int MaximumFileContentBytes = 8 * 1024 * 1024;
    private const long MaximumPendingContentBytes = 64L * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _marker;
    private readonly Func<string, ModulePrivilegeIdentity> _resolve;
    private readonly TimeProvider _clock;
    private readonly List<Plan> _plans;
    private readonly Dictionary<Guid, byte[]> _content = [];
    private long _pendingContentBytes;

    internal ModulePrivilegePlanStore(string contentRoot, Func<string, ModulePrivilegeIdentity> resolve, TimeProvider? clock = null)
    {
        _path = Path.Combine(contentRoot, "data", "module-management", "privilege-plans.json");
        _marker = _path + ".initialized";
        _resolve = resolve;
        _clock = clock ?? TimeProvider.System;
        if (File.Exists(_path) != File.Exists(_marker)) throw Error("recovery_required");
        if (!File.Exists(_path)) _plans = [];
        else
        {
            var stored = File.ReadAllBytes(_path);
            RequireCurrentSchema(stored);
            Document document;
            try { document = ModulePackageJson.Read<Document>(stored, 8 * 1024 * 1024); }
            catch (ModulePackageException) { throw Error("recovery_required"); }
            if (document.SchemaVersion != 6 || document.Plans is null || document.Plans.Length > 1000 || document.Plans.Any(p => p is null)
                || document.Plans.Select(p => p.Id).Distinct().Count() != document.Plans.Length)
                throw Error("recovery_required");
            foreach (var plan in document.Plans) ValidateStored(plan);
            _plans = document.Plans.ToList();
            var changed = false;
            for (var i = 0; i < _plans.Count; i++)
            {
                if (_plans[i].State == "Executing")
                {
                    _plans[i] = _plans[i] with { State = "RecoveryRequired", Problem = "module.privilege_recovery_required", TokenHash = null, Updated = Now };
                    changed = true;
                }
                // File content is deliberately not persisted. A pending publication from a previous process
                // cannot be completed from a digest alone, so it expires instead of executing different bytes.
                else if (_plans[i].State == "AwaitingConfirmation" && _plans[i].File?.Action == ModuleFileAction.Write)
                {
                    _plans[i] = _plans[i] with { State = "Expired", Problem = "module.privilege_plan_changed", TokenHash = null, Updated = Now };
                    changed = true;
                }
            }
            if (changed) Save();
        }
    }
    private DateTimeOffset Now => _clock.GetUtcNow();
    private static void RequireCurrentSchema(byte[] stored)
    {
        try
        {
            using var document = JsonDocument.Parse(stored);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 6)
                throw Error("recovery_required");
        }
        catch (JsonException) { throw Error("recovery_required"); }
    }
    private static void ValidateStored(Plan plan)
    {
        if (plan is null || plan.Id == Guid.Empty || string.IsNullOrEmpty(plan.Session)
            || plan.Identity is null || plan.Request is null || plan.Audit is null || !AuditIdentity(plan.Audit.InitiatingUser)
            || plan.State is not ("AwaitingConfirmation" or "Executing" or "Succeeded" or "Failed" or "Cancelled" or "Expired" or "RecoveryRequired")
            || plan.Expires != plan.Created.AddMinutes(5)
            || plan.IdentityHash != Hash(plan.Identity) || plan.RequestHash != RequestHash(plan.Request, ContentDigest(plan.File))
            || plan.Hash != PlanHash(plan.Id, plan.Session, plan.Audit.InitiatingUser, plan.IdentityHash, plan.RequestHash,
                plan.PackageHash, plan.FileHash, plan.DataHash, plan.Created, plan.Expires)
            || plan.TokenHash is not null && (plan.State != "AwaitingConfirmation"
                || plan.TokenHash.Length != 64 || !plan.TokenHash.All(char.IsAsciiHexDigitLower)))
            throw Error("recovery_required");
        ValidateStoredPackage(plan);
        ValidateStoredFile(plan);
        ValidateStoredData(plan);
        var attempted = plan.State is "Executing" or "Succeeded" or "Failed" or "RecoveryRequired";
        if (attempted != (plan.Audit.Administrator is not null && plan.Audit.ConfirmedAt is not null
                && plan.Audit.ApprovingUser == plan.Audit.InitiatingUser)
            || attempted && (!AuditIdentity(plan.Audit.Administrator!.Identity) || !AuditIdentity(plan.Audit.Administrator.AuthenticationMethod))
            || !attempted && (plan.Audit.Administrator is not null || plan.Audit.ApprovingUser is not null || plan.Audit.ConfirmedAt is not null)
            || plan.State is "Succeeded" or "Failed" && plan.Audit.AttemptCompletedAt is null)
            throw Error("recovery_required");
    }
    /// <summary>A stored plan carries a frozen package identity exactly when its category is package.manage,
    /// and that identity must still be a usable artifact bound by the plan hash.</summary>
    private static void ValidateStoredPackage(Plan plan)
    {
        if (plan.Request.Kind != ModuleCapabilityPolicy.PackageManage)
        {
            if (plan.Package is not null || plan.PackageHash != "") throw Error("recovery_required");
            return;
        }
        if (plan.Package is not { } package || !Token(package.SourceRef)
            || !AptPackageResolution.IsUsableModuleResolution(package.Resolution, out _)
            || package.Resolution.Package != plan.Request.Target
            || plan.PackageHash != PackageHash(package))
            throw Error("recovery_required");
    }
    /// <summary>A file plan carries exactly one frozen file identity, that identity names the request target,
    /// and the declared root/action still allow it.</summary>
    private static void ValidateStoredFile(Plan plan)
    {
        if (plan.Request.Kind != ModuleCapabilityPolicy.FileManage)
        {
            if (plan.File is not null || plan.FileHash != "") throw Error("recovery_required");
            return;
        }
        if (plan.File is not { } file || !ModuleResourceIdentity.CanonicalPath(file.Root)
            || !ModuleResourceIdentity.WithinRoot(file.Root, file.Path)
            || file.Path != plan.Request.Target || ActionName(file.Action) != plan.Request.Action
            || !ModuleResourceIdentity.Usable(file.Expected) || file.Mode is < 0 or > 0x1ff)
            throw Error("recovery_required");
        if (!plan.Identity.Privileges.OfType<FileManageCapability>().Any(capability =>
                capability.Root == file.Root
                && ModuleCapabilityPolicy.Allows(capability, plan.Request.Action, plan.Request.Target)))
            throw Error("recovery_required");
        if (file.Action == ModuleFileAction.Write)
        {
            if (!ModuleResourceIdentity.Digest(file.ContentSha256)
                || file.ContentBytes is < 0 or > MaximumFileContentBytes) throw Error("recovery_required");
        }
        else if (file.ContentSha256 != "" || file.ContentBytes != 0) throw Error("recovery_required");
        if (plan.FileHash != Hash(file)) throw Error("recovery_required");
    }
    private static void ValidateStoredData(Plan plan)
    {
        if (plan.Request.Kind != ModuleCapabilityPolicy.DataManage)
        {
            if (plan.Data is not null || plan.DataHash != "") throw Error("recovery_required");
            return;
        }
        if (plan.Data is not { } data || !ModuleResourceIdentity.CanonicalPath(data.Root)
            || data.Root != plan.Request.Target || ActionName(data.Action) != plan.Request.Action
            || !ModuleResourceIdentity.Identifier(data.Scope) || data.Scope != plan.Identity.ModuleId
            || !ModuleResourceIdentity.Usable(data.Expected) || !data.Expected.Exists || !data.Expected.IsDirectory)
            throw Error("recovery_required");
        if (data.Action == ModuleDataAction.Backup
            ? !ModuleResourceIdentity.Identifier(data.BackupRef) : data.BackupRef is not null)
            throw Error("recovery_required");
        if (!plan.Identity.Privileges.OfType<DataManageCapability>().Any(capability =>
                capability.Root == data.Root
                && ModuleCapabilityPolicy.Allows(capability, plan.Request.Action, plan.Request.Target)))
            throw Error("recovery_required");
        if (plan.DataHash != Hash(data)) throw Error("recovery_required");
    }
    private static bool AuditIdentity(string? value) => value is { Length: > 0 and <= 512 } && !value.Any(char.IsControl);
    private static bool Token(string? value) => value is { Length: > 0 and <= 96 }
        && value[0] is >= 'a' and <= 'z' && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    private static string PackageHash(ModulePrivilegePackage? package) => package is null ? "" : Hash(package);
    private static string FileHash(ModulePrivilegeFile? file) => file is null ? "" : Hash(file);
    private static string DataHash(ModulePrivilegeData? data) => data is null ? "" : Hash(data);
    private static string ContentDigest(ModulePrivilegeFile? file) =>
        file is { Action: ModuleFileAction.Write } ? file.ContentSha256 : "";
    private static string PlanHash(Guid id, string session, string initiatingUser, string identityHash, string requestHash,
        string packageHash, string fileHash, string dataHash, DateTimeOffset created, DateTimeOffset expires) =>
        Hash(new { id, session, initiatingUser, identityHash, requestHash, packageHash, fileHash, dataHash, created, expires });
    private static string ActionName(ModuleFileAction action) => action switch
    {
        ModuleFileAction.Write => "write",
        ModuleFileAction.Delete => "delete",
        _ => "",
    };
    private static string ActionName(ModuleDataAction action) => action switch
    {
        ModuleDataAction.Preserve => "preserve",
        ModuleDataAction.Backup => "backup",
        ModuleDataAction.Delete => "delete",
        _ => "",
    };
    internal async Task<ModulePrivilegePrepared> PrepareAsync(string moduleId, string session, ModulePrivilegeRequest request,
        string initiatingUser, Func<ModulePackageQuery, Task<ModulePackageResolution?>>? queryPackage = null,
        Func<ModuleFileQuery, Task<ModuleFileState?>>? queryFile = null,
        Func<ModuleDataQuery, Task<ModuleDataState?>>? queryData = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(session) || !AuditIdentity(initiatingUser) || request.Reason is not { Length: > 0 and <= 512 }
                || request.IdempotencyKey is not { Length: > 0 and <= 128 }
                || !request.IdempotencyKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                throw Error("target_invalid", 400);
            Validate(request);
            var identity = _resolve(moduleId);
            if (!identity.Privileges.Any(capability => ModuleCapabilityPolicy.Allows(capability, request.Action, request.Target)))
                throw Error("not_declared", 403);
            if (request.Kind == ModuleCapabilityPolicy.ServiceManage
                && !identity.AllowedServices.Contains(request.Target, StringComparer.Ordinal)) throw Error("target_invalid", 403);
            var content = request.Kind == ModuleCapabilityPolicy.FileManage && request.Action == "write"
                ? DecodeContent(request.ContentBase64) : null;
            var requestHash = RequestHash(request, content is null ? "" : Digest(content));
            var existing = _plans.SingleOrDefault(p => p.Session == session && p.Identity.ModuleId == moduleId
                && p.Request.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null)
            {
                if (existing.RequestHash != requestHash || existing.Audit.InitiatingUser != initiatingUser) throw Error("idempotency_conflict");
                existing = Expire(existing);
                return new(existing.Id, existing.Hash, existing.State);
            }
            if (_plans.Count >= 1000) throw Error("capacity");
            // The frozen identity is part of the plan, not a later approximation: a re-query that moved
            // is a different plan and can never be approved under the hash the user saw.
            ModulePrivilegePackage? package = null;
            ModulePrivilegeFile? file = null;
            ModulePrivilegeData? data = null;
            switch (request.Kind)
            {
                case ModuleCapabilityPolicy.PackageManage:
                    package = await FreezePackageAsync(identity, request, queryPackage);
                    break;
                case ModuleCapabilityPolicy.FileManage:
                    file = await FreezeFileAsync(identity, request, content, queryFile);
                    break;
                case ModuleCapabilityPolicy.DataManage:
                    data = await FreezeDataAsync(identity, request, queryData);
                    break;
            }
            var packageHash = PackageHash(package);
            var fileHash = FileHash(file);
            var dataHash = DataHash(data);
            var id = Guid.NewGuid();
            var created = Now;
            var expires = created.AddMinutes(5);
            var identityHash = Hash(identity);
            var hash = PlanHash(id, session, initiatingUser, identityHash, requestHash, packageHash, fileHash, dataHash, created, expires);
            var plan = new Plan(id, session, identity, identityHash, request with { ContentBase64 = null }, requestHash,
                package, packageHash, file, fileHash, data, dataHash, hash, created, expires,
                "AwaitingConfirmation", null, null, created, new(initiatingUser, null, null, null, null));
            _plans.Add(plan);
            if (content is not null)
            {
                if (_pendingContentBytes + content.LongLength > MaximumPendingContentBytes)
                {
                    _plans.Remove(plan);
                    throw Error("capacity");
                }
                _content[id] = content;
                _pendingContentBytes += content.LongLength;
            }
            try { Save(); }
            catch
            {
                _plans.Remove(plan);
                ForgetContent(id);
                throw;
            }
            return new(id, hash, plan.State);
        }
        finally { _gate.Release(); }
    }
    /// <summary>Resolve the declared package against the Helper's own approved source policy. A category,
    /// manager, source or version that cannot be resolved here never becomes a plan.</summary>
    private static async Task<ModulePrivilegePackage> FreezePackageAsync(ModulePrivilegeIdentity identity,
        ModulePrivilegeRequest request, Func<ModulePackageQuery, Task<ModulePackageResolution?>>? queryPackage)
    {
        var declared = identity.Privileges.OfType<PackageManageCapability>()
            .Where(capability => ModuleCapabilityPolicy.Allows(capability, request.Action, request.Target)).ToArray();
        if (declared.Length == 0) throw Error("not_declared", 403);
        // Only the apt adapter is delivered. A windows-msi declaration stays a declaration until its own
        // adapter and acceptance exist; it never degrades to an installer command line. More than one apt
        // match is ambiguous, so it fails closed instead of picking a source reference.
        var apt = declared.Where(capability => capability.Manager == "apt").ToArray();
        if (apt.Length != 1 || queryPackage is null) throw Error("unsupported", 400);
        var capability = apt[0];
        var resolution = await queryPackage(new(capability.SourceRef, request.Target));
        if (resolution is null || !AptPackageResolution.IsUsableModuleResolution(resolution, out _)
            || resolution.Package != request.Target) throw Error("unsupported", 400);
        if (!DebianVersion.IsWithin(resolution.Version, capability.Versions.Minimum, capability.Versions.MaximumExclusive))
            throw Error("not_declared", 403);
        return new(capability.SourceRef, resolution);
    }
    private static async Task<ModulePrivilegeFile> FreezeFileAsync(ModulePrivilegeIdentity identity,
        ModulePrivilegeRequest request, byte[]? content, Func<ModuleFileQuery, Task<ModuleFileState?>>? queryFile)
    {
        var declared = identity.Privileges.OfType<FileManageCapability>()
            .Where(capability => ModuleCapabilityPolicy.Allows(capability, request.Action, request.Target)).ToArray();
        if (declared.Length == 0) throw Error("not_declared", 403);
        if (declared.Length != 1) throw Error("unsupported", 400);
        if (queryFile is null) throw Error("unsupported", 400);
        var capability = declared[0];
        if (content is not null && content.LongLength > capability.MaximumBytes) throw Error("target_invalid", 403);
        var action = request.Action switch
        {
            "write" => ModuleFileAction.Write,
            "delete" => ModuleFileAction.Delete,
            _ => throw Error("unsupported", 400),
        };
        var state = await queryFile(new(capability.Root, request.Target, action));
        if (state is null) throw Error("unsupported", 400);
        if (state.IsDirectory || state.IsSymbolicLink) throw Error("target_invalid", 403);
        if (!ModuleResourceIdentity.Usable(state)) throw Error("unsupported", 400);
        if (action == ModuleFileAction.Delete && !state.Exists) throw Error("target_invalid", 403);
        return new(capability.Root, request.Target, action, state, content is null ? "" : Digest(content),
            content?.LongLength ?? 0, state.Mode);
    }
    private static async Task<ModulePrivilegeData> FreezeDataAsync(ModulePrivilegeIdentity identity,
        ModulePrivilegeRequest request, Func<ModuleDataQuery, Task<ModuleDataState?>>? queryData)
    {
        var declared = identity.Privileges.OfType<DataManageCapability>()
            .Where(capability => ModuleCapabilityPolicy.Allows(capability, request.Action, request.Target)).ToArray();
        if (declared.Length == 0) throw Error("not_declared", 403);
        if (declared.Length != 1) throw Error("unsupported", 400);
        if (queryData is null) throw Error("unsupported", 400);
        var capability = declared[0];
        var action = request.Action switch
        {
            "preserve" => ModuleDataAction.Preserve,
            "backup" => ModuleDataAction.Backup,
            "delete" => ModuleDataAction.Delete,
            _ => throw Error("unsupported", 400),
        };
        if (action == ModuleDataAction.Backup ? !ModuleResourceIdentity.Identifier(request.BackupRef) : request.BackupRef is not null)
            throw Error("target_invalid", 400);
        var state = await queryData(new(capability.Root, action));
        if (state is null || !ModuleResourceIdentity.Usable(state) || !state.Exists || !state.IsDirectory)
            throw Error("unsupported", 400);
        return new(capability.Root, action, state, identity.ModuleId,
            action == ModuleDataAction.Backup ? request.BackupRef : null);
    }

    internal async Task<ModulePrivilegePlan> ReadForUserAsync(Guid id, string session)
    {
        await _gate.WaitAsync();
        try
        {
            var plan = Owned(id, session);
            var token = "";
            if (plan.State == "AwaitingConfirmation")
            {
                token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                plan = Replace(plan with { TokenHash = HashText(token), Updated = Now });
            }
            return new(plan.Id, plan.Identity.ModuleId, plan.Identity.Version, plan.Identity.PublisherId,
                plan.Identity.ArtifactSha256, plan.Request.Kind, plan.Request.Action, plan.Request.Target, plan.Request.Reason,
                Effects(plan.Request.Kind), Warnings(plan), PackageView(plan.Package), FileView(plan.File), DataView(plan.Data),
                plan.Hash, plan.Created, plan.Expires, plan.State, plan.Problem, token);
        }
        finally { _gate.Release(); }
    }

    internal async Task<ModulePrivilegeStatus> GetForModuleAsync(Guid id, string moduleId)
    {
        await _gate.WaitAsync();
        try
        {
            var plan = Find(id);
            if (plan.Identity.ModuleId != moduleId) throw Error("denied", 403);
            return Status(Expire(plan));
        }
        finally { _gate.Release(); }
    }

    internal async Task<ModulePrivilegeStatus> CancelAsync(Guid id, string session)
    {
        await _gate.WaitAsync();
        try
        {
            var plan = Owned(id, session);
            if (plan.State != "AwaitingConfirmation") throw Error("approval_invalid");
            ForgetContent(plan.Id);
            return Status(Replace(plan with { State = "Cancelled", TokenHash = null, Updated = Now }));
        }
        finally { _gate.Release(); }
    }

    internal async Task<ModulePrivilegeStatus> ApproveAsync(Guid id, string session, ModulePrivilegeApproval approval,
        Func<Task<ModulePrivilegeAdministrator?>> verifyAdministrator,
        Func<Guid, ModulePrivilegeExecution, Task<bool>> execute)
    {
        await _gate.WaitAsync();
        try
        {
            var plan = Owned(id, session);
            if (plan.State == "Expired") throw Error("plan_expired");
            if (plan.State != "AwaitingConfirmation" || plan.Hash != approval.PlanHash || plan.TokenHash is null
                || approval.ConfirmationToken is not { Length: 64 }
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(plan.TokenHash), Encoding.ASCII.GetBytes(HashText(approval.ConfirmationToken))))
                throw Error("approval_invalid");
            if (Hash(_resolve(plan.Identity.ModuleId)) != plan.IdentityHash) throw Error("plan_changed");
            // No capability grant or automatic administrator classification can skip this confirmation gate.
            var administrator = await verifyAdministrator();
            if (administrator is null || !AuditIdentity(administrator.Identity) || !AuditIdentity(administrator.AuthenticationMethod)) throw Error("admin_required", 403);
            if (plan.Expires <= Now) throw Error("plan_expired");
            if (Hash(_resolve(plan.Identity.ModuleId)) != plan.IdentityHash) throw Error("plan_changed");
            byte[]? content = null;
            if (plan.File is { Action: ModuleFileAction.Write })
            {
                if (!_content.TryGetValue(plan.Id, out content)) throw Error("plan_changed");
            }
            plan = Replace(plan with { State = "Executing", TokenHash = null, Updated = Now,
                Audit = plan.Audit with { ApprovingUser = plan.Audit.InitiatingUser, Administrator = administrator, ConfirmedAt = Now } });
            ForgetContent(plan.Id);
            try
            {
                var execution = new ModulePrivilegeExecution(plan.Request, plan.Package, plan.File, plan.Data, content);
                var success = await execute(plan.Id, execution);
                plan = Replace(plan with { State = success ? "Succeeded" : "Failed",
                    Problem = success ? null : "module.privilege_helper_failed", Updated = Now,
                    Audit = plan.Audit with { AttemptCompletedAt = Now } });
            }
            catch (ModulePrivilegeException error) when (error.Code == "module.privilege_plan_changed")
            {
                // The Helper refused before executing because the frozen identity, source, policy or path
                // moved. That is a known non-execution, not an unknown outcome, so the plan cannot be retried.
                plan = Replace(plan with { State = "Failed", Problem = error.Code, Updated = Now,
                    Audit = plan.Audit with { AttemptCompletedAt = Now } });
                throw;
            }
            catch (Exception)
            {
                // A lost response cannot establish whether the Helper applied the action. Never retry automatically.
                plan = Replace(plan with { State = "RecoveryRequired", Problem = "module.privilege_recovery_required", Updated = Now,
                    Audit = plan.Audit with { AttemptCompletedAt = Now } });
            }
            return Status(plan);
        }
        finally { _gate.Release(); }
    }
    /// <summary>Reject anything outside the frozen category catalog before planning. Only categories with a
    /// delivered Host adapter may be planned; everything else fails closed instead of degrading to a command.
    /// package.manage and file.manage/data.manage additionally need a Helper resolution before a plan exists.</summary>
    internal static void Validate(ModulePrivilegeRequest request)
    {
        if (!ModuleCapabilityPolicy.Kinds.Contains(request.Kind, StringComparer.Ordinal)
            || !ModuleCapabilityPolicy.Actions(request.Kind).Contains(request.Action, StringComparer.Ordinal))
            throw Error("unsupported", 400);
        if (request.Secrets is { Length: > 0 }) throw Error("unsupported", 400);
        if (request.Target is not { Length: > 0 and <= 512 } || request.Target.Any(char.IsControl))
            throw Error("target_invalid", 400);
        if (!ModuleCapabilityPolicy.IsExecutable(request.Kind)) throw Error("unsupported", 400);
        // Delivered file/data actions are deliberately narrower than the declarable catalog. set-mode,
        // set-owner and restore need adapters of their own and must not ride on this one.
        if (request.Kind == ModuleCapabilityPolicy.FileManage && request.Action is not ("write" or "delete"))
            throw Error("unsupported", 400);
        if (request.Kind == ModuleCapabilityPolicy.DataManage && request.Action is not ("preserve" or "backup" or "delete"))
            throw Error("unsupported", 400);
        if (request.Kind is ModuleCapabilityPolicy.FileManage or ModuleCapabilityPolicy.DataManage
            && !ModuleResourceIdentity.CanonicalPath(request.Target))
            throw Error("target_invalid", 400);
        if (request.Kind == ModuleCapabilityPolicy.FileManage && request.Action == "write" ? request.ContentBase64 is null
            : request.ContentBase64 is not null)
            throw Error("target_invalid", 400);
        if (request.Kind == ModuleCapabilityPolicy.DataManage && request.Action == "backup" ? request.BackupRef is null
            : request.BackupRef is not null)
            throw Error("target_invalid", 400);
        if (request.Kind != ModuleCapabilityPolicy.FileManage && request.ContentBase64 is not null)
            throw Error("target_invalid", 400);
    }
    internal static ModulePackageTransactionAction? PackageAction(string action) => action switch
    {
        "install" => ModulePackageTransactionAction.Install,
        "update" => ModulePackageTransactionAction.Update,
        "repair" => ModulePackageTransactionAction.Repair,
        "remove" => ModulePackageTransactionAction.Remove,
        _ => null,
    };
    private static string[] Effects(string kind) => kind switch
    {
        ModuleCapabilityPolicy.ServiceManage => ["service-start", "service-stop", "service-restart"],
        ModuleCapabilityPolicy.PackageManage => ["package-install", "package-remove", "package-modify"],
        ModuleCapabilityPolicy.FileManage => ["file-write", "file-delete"],
        ModuleCapabilityPolicy.DataManage => ["data-preserve", "data-backup", "data-delete"],
        _ => [],
    };
    private static string[] Warnings(Plan plan) => plan.Request.Kind switch
    {
        ModuleCapabilityPolicy.ServiceManage => ["Service availability and dependent workloads may be interrupted."],
        ModuleCapabilityPolicy.PackageManage => PackageAction(plan.Request.Action) is { } action
            ? ModulePackageDescription.Warnings(action, false)
            : ["Package maintainer scripts may run with host privileges."],
        ModuleCapabilityPolicy.FileManage => ["The confirmed configuration file is replaced atomically; previous content is not kept as a general rollback.",
            "Secrets must be delivered through a Host secret reference; they must not be placed in configuration content.",
            "The file owner and mode are fixed by the Host resource policy."],
        ModuleCapabilityPolicy.DataManage => plan.Request.Action switch
        {
            "backup" => ["A verified copy is written under the Host backup root.", "The declared data root itself is not modified."],
            "delete" => ["Deleting the declared data root is irreversible.", "Create and verify a backup before deleting data.",
                "Only the declared root is emptied; unrelated paths are untouched."],
            _ => ["The declared data root stays in place.", "A preservation receipt records its digest, file count and size."],
        },
        _ => [],
    };
    private static ModulePackagePlanIdentity? PackageView(ModulePrivilegePackage? package) => package is null ? null : new(
        package.SourceRef, package.Resolution.Package, package.Resolution.Version, package.Resolution.Architecture,
        package.Resolution.BaseUri.TrimEnd('/') + "/" + package.Resolution.Suite + "/" + package.Resolution.Area,
        FileName(package.Resolution.PoolUri), package.Resolution.Sha256, package.Resolution.Bytes,
        package.Resolution.Dependencies.Select(dependency => new ModulePackagePlanDependency(
            dependency.FileName, dependency.Bytes, dependency.Sha256)).ToArray());
    private static ModuleFilePlanIdentity? FileView(ModulePrivilegeFile? file) => file is null ? null : new(
        file.Root, file.Path, ActionName(file.Action), file.Expected.Exists ? "file" : "absent",
        file.Expected.Exists ? file.Expected.Sha256 : "", file.Expected.Exists ? file.Expected.Bytes : 0,
        file.Action == ModuleFileAction.Write ? "file" : "absent",
        file.Action == ModuleFileAction.Write ? file.ContentSha256 : "",
        file.Action == ModuleFileAction.Write ? file.ContentBytes : 0, file.Mode);
    private static ModuleDataPlanIdentity? DataView(ModulePrivilegeData? data) => data is null ? null : new(
        data.Root, ActionName(data.Action), data.Expected.Exists ? "directory" : "absent",
        data.Expected.Files, data.Expected.Bytes, data.Expected.Digest, data.BackupRef ?? "");
    private static string FileName(string uri) => uri[(uri.LastIndexOf('/') + 1)..];
    private Plan Owned(Guid id, string session)
    {
        var plan = Find(id);
        if (plan.Session != session) throw Error("denied", 403);
        return Expire(plan);
    }
    private Plan Find(Guid id) => _plans.SingleOrDefault(p => p.Id == id) ?? throw Error("plan_not_found", 404);
    private Plan Expire(Plan plan)
    {
        if (plan.State != "AwaitingConfirmation" || plan.Expires > Now) return plan;
        ForgetContent(plan.Id);
        return Replace(plan with { State = "Expired", TokenHash = null, Updated = Now });
    }
    private Plan Replace(Plan plan)
    {
        _plans[_plans.FindIndex(p => p.Id == plan.Id)] = plan;
        Save();
        return plan;
    }
    private void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(6, _plans.ToArray()), ModulePackageJson.Options);
        if (bytes.Length > 8 * 1024 * 1024) throw Error("capacity");
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!File.Exists(_marker)) WriteDurable(_marker, Encoding.UTF8.GetBytes("v6\n"));
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteDurable(temporary, bytes); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void WriteDurable(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        output.Write(bytes);
        output.Flush(true);
    }
    internal static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, ModulePackageJson.Options)));
    private static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string RequestHash(ModulePrivilegeRequest request, string contentDigest) =>
        Hash(new { request = request with { ContentBase64 = null }, contentDigest });
    private static byte[] DecodeContent(string? value)
    {
        try
        {
            var content = Convert.FromBase64String(value ?? "");
            if (content.Length > MaximumFileContentBytes) throw Error("target_invalid", 400);
            return content;
        }
        catch (FormatException) { throw Error("target_invalid", 400); }
    }
    private static string Digest(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
    private void ForgetContent(Guid id)
    {
        if (_content.Remove(id, out var bytes)) _pendingContentBytes -= bytes.LongLength;
    }
    private static ModulePrivilegeStatus Status(Plan plan) => new(plan.Id, plan.State, plan.Problem, plan.Updated);
    private static ModulePrivilegeException Error(string suffix, int status = 409) => new("module.privilege_" + suffix, status);
}
