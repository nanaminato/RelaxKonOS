using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

internal sealed record ModulePrivilegeIdentity(string ModuleId, string Version, string PublisherId, string ArtifactSha256,
    string ManifestSha256, string PolicyRevision, ModulePackageCapability[] Privileges, string[] AllowedServices);
internal sealed record ModulePrivilegeAdministrator(string Identity, string AuthenticationMethod);
internal sealed class ModulePrivilegeException(string code, int status = 409) : Exception(code)
{
    internal string Code => Message;
    internal int Status => status;
}

/// <summary>Durable single-use confirmation gate. Recovery observes an interrupted operation, never replays it.</summary>
internal sealed class ModulePrivilegePlanStore
{
    private sealed record Plan(Guid Id, string Session, ModulePrivilegeIdentity Identity, string IdentityHash,
        ModulePrivilegeRequest Request, string RequestHash, string Hash, DateTimeOffset Created, DateTimeOffset Expires,
        string State, string? Problem, string? TokenHash, DateTimeOffset Updated, ApprovalAudit Audit);
    private sealed record ApprovalAudit(string InitiatingUser, string? ApprovingUser, ModulePrivilegeAdministrator? Administrator,
        DateTimeOffset? ConfirmedAt, DateTimeOffset? AttemptCompletedAt);
    private sealed record Document(int SchemaVersion, Plan[] Plans);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _marker;
    private readonly Func<string, ModulePrivilegeIdentity> _resolve;
    private readonly TimeProvider _clock;
    private readonly List<Plan> _plans;

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
            var document = ModulePackageJson.Read<Document>(stored, 8 * 1024 * 1024);
            if (document.SchemaVersion != 4 || document.Plans is null || document.Plans.Length > 1000 || document.Plans.Any(p => p is null)
                || document.Plans.Select(p => p.Id).Distinct().Count() != document.Plans.Length)
                throw Error("recovery_required");
            foreach (var plan in document.Plans) ValidateStored(plan);
            _plans = document.Plans.ToList();
            for (var i = 0; i < _plans.Count; i++)
                if (_plans[i].State == "Executing")
                    _plans[i] = _plans[i] with { State = "RecoveryRequired", Problem = "module.privilege_recovery_required", TokenHash = null, Updated = Now };
            Save();
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
                || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 4)
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
            || plan.IdentityHash != Hash(plan.Identity) || plan.RequestHash != Hash(plan.Request)
            || plan.Hash != PlanHash(plan.Id, plan.Session, plan.Audit.InitiatingUser, plan.IdentityHash, plan.RequestHash, plan.Created, plan.Expires)
            || plan.TokenHash is not null && (plan.State != "AwaitingConfirmation"
                || plan.TokenHash.Length != 64 || !plan.TokenHash.All(char.IsAsciiHexDigitLower)))
            throw Error("recovery_required");
        var attempted = plan.State is "Executing" or "Succeeded" or "Failed" or "RecoveryRequired";
        if (attempted != (plan.Audit.Administrator is not null && plan.Audit.ConfirmedAt is not null
                && plan.Audit.ApprovingUser == plan.Audit.InitiatingUser)
            || attempted && (!AuditIdentity(plan.Audit.Administrator!.Identity) || !AuditIdentity(plan.Audit.Administrator.AuthenticationMethod))
            || !attempted && (plan.Audit.Administrator is not null || plan.Audit.ApprovingUser is not null || plan.Audit.ConfirmedAt is not null)
            || plan.State is "Succeeded" or "Failed" && plan.Audit.AttemptCompletedAt is null)
            throw Error("recovery_required");
    }
    private static bool AuditIdentity(string? value) => value is { Length: > 0 and <= 512 } && !value.Any(char.IsControl);
    private static string PlanHash(Guid id, string session, string initiatingUser, string identityHash, string requestHash,
        DateTimeOffset created, DateTimeOffset expires) => Hash(new { id, session, initiatingUser, identityHash, requestHash, created, expires });
    internal async Task<ModulePrivilegePrepared> PrepareAsync(string moduleId, string session, ModulePrivilegeRequest request, string initiatingUser)
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
            var requestHash = Hash(request);
            var existing = _plans.SingleOrDefault(p => p.Session == session && p.Identity.ModuleId == moduleId
                && p.Request.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null)
            {
                if (existing.RequestHash != requestHash || existing.Audit.InitiatingUser != initiatingUser) throw Error("idempotency_conflict");
                existing = Expire(existing);
                return new(existing.Id, existing.Hash, existing.State);
            }
            if (_plans.Count >= 1000) throw Error("capacity");
            var id = Guid.NewGuid();
            var created = Now;
            var expires = created.AddMinutes(5);
            var identityHash = Hash(identity);
            var hash = PlanHash(id, session, initiatingUser, identityHash, requestHash, created, expires);
            var plan = new Plan(id, session, identity, identityHash, request, requestHash, hash, created, expires,
                "AwaitingConfirmation", null, null, created, new(initiatingUser, null, null, null, null));
            _plans.Add(plan);
            Save();
            return new(id, hash, plan.State);
        }
        finally { _gate.Release(); }
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
                Effects(plan.Request.Kind), Warnings(plan.Request.Kind),
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
            return Status(Replace(plan with { State = "Cancelled", TokenHash = null, Updated = Now }));
        }
        finally { _gate.Release(); }
    }

    internal async Task<ModulePrivilegeStatus> ApproveAsync(Guid id, string session, ModulePrivilegeApproval approval,
        Func<Task<ModulePrivilegeAdministrator?>> verifyAdministrator, Func<Guid, ModulePrivilegeRequest, Task<bool>> execute)
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
            plan = Replace(plan with { State = "Executing", TokenHash = null, Updated = Now,
                Audit = plan.Audit with { ApprovingUser = plan.Audit.InitiatingUser, Administrator = administrator, ConfirmedAt = Now } });
            try
            {
                var success = await execute(plan.Id, plan.Request);
                plan = Replace(plan with { State = success ? "Succeeded" : "Failed",
                    Problem = success ? null : "module.privilege_helper_failed", Updated = Now,
                    Audit = plan.Audit with { AttemptCompletedAt = Now } });
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
    /// delivered Host adapter may execute; everything else fails closed instead of degrading to a command.</summary>
    internal static void Validate(ModulePrivilegeRequest request)
    {
        if (!ModuleCapabilityPolicy.Kinds.Contains(request.Kind, StringComparer.Ordinal)
            || !ModuleCapabilityPolicy.Actions(request.Kind).Contains(request.Action, StringComparer.Ordinal))
            throw Error("unsupported", 400);
        if (request.Secrets is { Length: > 0 }) throw Error("unsupported", 400);
        if (request.Target is not { Length: > 0 and <= 512 } || request.Target.Any(char.IsControl))
            throw Error("target_invalid", 400);
        if (!ModuleCapabilityPolicy.IsExecutable(request.Kind)) throw Error("unsupported", 400);
    }
    private static string[] Effects(string kind) => kind switch
    {
        ModuleCapabilityPolicy.ServiceManage => ["service-start", "service-stop", "service-restart"],
        ModuleCapabilityPolicy.PackageManage => ["package-install", "package-remove", "package-modify"],
        ModuleCapabilityPolicy.FileManage => ["file-write", "file-delete"],
        ModuleCapabilityPolicy.DataManage => ["data-delete", "data-restore"],
        _ => [],
    };
    private static string[] Warnings(string kind) => kind switch
    {
        ModuleCapabilityPolicy.ServiceManage => ["Service availability and dependent workloads may be interrupted."],
        ModuleCapabilityPolicy.PackageManage => ["Package maintainer scripts may run with host privileges."],
        _ => [],
    };
    private Plan Owned(Guid id, string session)
    {
        var plan = Find(id);
        if (plan.Session != session) throw Error("denied", 403);
        return Expire(plan);
    }
    private Plan Find(Guid id) => _plans.SingleOrDefault(p => p.Id == id) ?? throw Error("plan_not_found", 404);
    private Plan Expire(Plan plan) => plan.State == "AwaitingConfirmation" && plan.Expires <= Now
        ? Replace(plan with { State = "Expired", TokenHash = null, Updated = Now }) : plan;
    private Plan Replace(Plan plan)
    {
        _plans[_plans.FindIndex(p => p.Id == plan.Id)] = plan;
        Save();
        return plan;
    }
    private void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(4, _plans.ToArray()), ModulePackageJson.Options);
        if (bytes.Length > 8 * 1024 * 1024) throw Error("capacity");
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!File.Exists(_marker)) WriteDurable(_marker, Encoding.UTF8.GetBytes("v4\n"));
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
    private static ModulePrivilegeStatus Status(Plan plan) => new(plan.Id, plan.State, plan.Problem, plan.Updated);
    private static ModulePrivilegeException Error(string suffix, int status = 409) => new("module.privilege_" + suffix, status);
}
