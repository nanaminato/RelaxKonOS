using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

/// <summary>Explicit, expiring administrator consent for one declared service action/target.
/// The module's process credential identifies the caller; a consent ID never authenticates it.</summary>
internal sealed class ModulePrivilegeConsentStore
{
    private sealed record Consent(Guid Id, string Session, string Actor, ModulePrivilegeIdentity Identity,
        string IdentityHash, ModulePrivilegeConsentRequest Request, string RequestHash, DateTimeOffset Created,
        string Hash, string State, string? TokenHash, ModulePrivilegeAdministrator? Administrator,
        DateTimeOffset? ApprovedAt, string? RevokedBy, DateTimeOffset? RevokedAt);
    private sealed record Document(int SchemaVersion, Consent[] Consents);
    internal sealed record Authority(Guid Id, string Actor, ModulePrivilegeAdministrator Administrator, DateTimeOffset ExpiresAt);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly Func<string, ModulePrivilegeIdentity> _resolve;
    private readonly TimeProvider _clock;
    private readonly List<Consent> _consents;
    private string? _documentHash;
    private DateTimeOffset Now => _clock.GetUtcNow();

    internal ModulePrivilegeConsentStore(string contentRoot, Func<string, ModulePrivilegeIdentity> resolve, TimeProvider? clock = null)
    {
        _path = Path.Combine(contentRoot, "data", "module-management", "privilege-consents.json");
        _resolve = resolve; _clock = clock ?? TimeProvider.System;
        if (File.Exists(_path) != File.Exists(_path + ".initialized")) throw Error("recovery_required");
        if (!File.Exists(_path)) { _consents = []; return; }
        var stored = File.ReadAllBytes(_path);
        _documentHash = Convert.ToHexStringLower(SHA256.HashData(stored));
        var document = ModulePackageJson.Read<Document>(stored, 8 * 1024 * 1024);
        if (document.SchemaVersion != 1 || document.Consents is null || document.Consents.Length > 1000
            || document.Consents.Any(c => c is null) || document.Consents.Select(c => c.Id).Distinct().Count() != document.Consents.Length)
            throw Error("recovery_required");
        foreach (var consent in document.Consents)
        {
            if (consent.Id == Guid.Empty || !Text(consent.Actor) || !Text(consent.Session) || consent.Identity is null
                || consent.Request is null || consent.Identity.ModuleId != consent.Request.ModuleId
                || consent.IdentityHash != Hash(consent.Identity) || consent.RequestHash != Hash(consent.Request)
                || consent.Hash != Digest(consent.Id, consent.Session, consent.Actor, consent.IdentityHash, consent.RequestHash, consent.Created)
                || consent.State is not ("AwaitingConfirmation" or "Active" or "Revoked" or "Cancelled")
                || consent.Request.ExpiresAt <= consent.Created || consent.Request.ExpiresAt > consent.Created.AddDays(30)
                || consent.TokenHash is not null && (consent.State != "AwaitingConfirmation" || !DigestText(consent.TokenHash))
                || (consent.State is "Active" or "Revoked") != (consent.Administrator is not null && consent.ApprovedAt is not null)
                || consent.Administrator is not null && (!Text(consent.Administrator.Identity) || !Text(consent.Administrator.AuthenticationMethod))
                || (consent.State is "Revoked" or "Cancelled") != (Text(consent.RevokedBy) && consent.RevokedAt is not null))
                throw Error("recovery_required");
        }
        _consents = document.Consents.ToList();
    }

    internal async Task<ModulePrivilegeConsent> PrepareAsync(string session, string actor, ModulePrivilegeConsentRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureDocument();
            if (!Text(session) || !Text(actor) || !Text(request.Reason) || request.IdempotencyKey is not { Length: > 0 and <= 128 }
                || !request.IdempotencyKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) throw Error("consent_invalid", 400);
            var identity = _resolve(request.ModuleId);
            ValidateAction(identity, request.Kind, request.Target);
            var fingerprint = Hash(request);
            var existing = _consents.SingleOrDefault(c => c.Session == session && c.Request.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null)
            {
                if (existing.Actor != actor || existing.RequestHash != fingerprint) throw Error("idempotency_conflict");
                return View(existing, "");
            }
            if (request.ExpiresAt <= Now || request.ExpiresAt > Now.AddDays(30)) throw Error("consent_invalid", 400);
            if (_consents.Count >= 1000) throw Error("capacity");
            var id = Guid.NewGuid(); var created = Now; var identityHash = Hash(identity);
            var consent = new Consent(id, session, actor, identity, identityHash, request, fingerprint, created,
                Digest(id, session, actor, identityHash, fingerprint, created), "AwaitingConfirmation", null, null, null, null, null);
            _consents.Add(consent);
            try { Save(); } catch { _consents.Remove(consent); throw; }
            return View(consent, "");
        }
        finally { _gate.Release(); }
    }

    internal async Task<ModulePrivilegeConsent> ReadAsync(Guid id, string session)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureDocument();
            var consent = Owned(id, session); var token = "";
            if (consent.State == "AwaitingConfirmation" && consent.Request.ExpiresAt > Now && consent.Created.AddMinutes(5) > Now)
            {
                token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                consent = Replace(consent with { TokenHash = HashText(token) });
            }
            return View(consent, token);
        }
        finally { _gate.Release(); }
    }

    internal async Task<ModulePrivilegeConsent> ApproveAsync(Guid id, string session, ModulePrivilegeApproval approval,
        Func<Task<ModulePrivilegeAdministrator?>> verify)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureDocument();
            var consent = Owned(id, session);
            if (consent.State != "AwaitingConfirmation" || consent.Request.ExpiresAt <= Now || consent.Created.AddMinutes(5) <= Now
                || consent.Hash != approval.PlanHash || consent.TokenHash is null || approval.ConfirmationToken is not { Length: 64 }
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(consent.TokenHash), Encoding.ASCII.GetBytes(HashText(approval.ConfirmationToken))))
                throw Error("approval_invalid");
            Current(consent);
            var administrator = await verify();
            if (administrator is null || !Text(administrator.Identity) || !Text(administrator.AuthenticationMethod)) throw Error("admin_required", 403);
            Current(consent);
            if (consent.Request.ExpiresAt <= Now || consent.Created.AddMinutes(5) <= Now) throw Error("approval_invalid");
            return View(Replace(consent with { State = "Active", TokenHash = null, Administrator = administrator, ApprovedAt = Now }), "");
        }
        finally { _gate.Release(); }
    }

    internal async Task<ModulePrivilegeConsent> RevokeAsync(Guid id, string actor)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureDocument();
            if (!Text(actor)) throw Error("denied", 403);
            var consent = Find(id);
            if (consent.State is "Revoked" or "Cancelled") return View(consent, "");
            return View(Replace(consent with { State = consent.State == "Active" ? "Revoked" : "Cancelled", TokenHash = null, RevokedBy = actor, RevokedAt = Now }), "");
        }
        finally { _gate.Release(); }
    }

    internal async Task<ModulePrivilegeConsent[]> ListAsync(string? moduleId = null)
    {
        await _gate.WaitAsync();
        try { EnsureDocument(); return _consents.Where(c => moduleId is null || c.Identity.ModuleId == moduleId).Select(c => View(c, "")).ToArray(); }
        finally { _gate.Release(); }
    }

    internal async Task<ModulePrivilegeStatus> ExecuteAsync(Guid id, string moduleId, ModulePrivilegeRequest request,
        Func<Authority, Task<ModulePrivilegeStatus>> execute)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureDocument();
            var consent = Find(id);
            if (consent.Identity.ModuleId != moduleId || consent.State != "Active" || consent.Request.ExpiresAt <= Now
                || consent.Request.Kind != request.Kind || consent.Request.Target != request.Target)
                throw Error("confirmation_required", 403);
            Current(consent);
            // Hold the gate until dispatch completes: revocation is linearized with execution.
            // Existing durable operation IDs still prevent replay after a crash or lost response.
            return await execute(new(consent.Id, consent.Actor, consent.Administrator!, consent.Request.ExpiresAt));
        }
        finally { _gate.Release(); }
    }

    private void Current(Consent consent)
    {
        if (Hash(_resolve(consent.Identity.ModuleId)) != consent.IdentityHash) throw Error("plan_changed");
    }
    private static void ValidateAction(ModulePrivilegeIdentity identity, string kind, string target)
    {
        _ = ModulePrivilegePlanStore.Action(kind);
        if (!identity.Privileges.Any(p => p.Kind == kind && p.Target == target)) throw Error("not_declared", 403);
        if (!identity.AllowedServices.Contains(target, StringComparer.Ordinal)) throw Error("target_invalid", 403);
    }
    private Consent Find(Guid id) => _consents.SingleOrDefault(c => c.Id == id) ?? throw Error("consent_not_found", 404);
    private Consent Owned(Guid id, string session)
    {
        var consent = Find(id);
        if (consent.Session != session) throw Error("denied", 403);
        return consent;
    }
    private Consent Replace(Consent consent)
    {
        var index = _consents.FindIndex(c => c.Id == consent.Id); var previous = _consents[index];
        _consents[index] = consent;
        try { Save(); } catch { _consents[index] = previous; throw; }
        return consent;
    }
    private ModulePrivilegeConsent View(Consent consent, string token) => new(consent.Id, consent.Identity.ModuleId,
        consent.Identity.Version, consent.Identity.PublisherId, consent.Identity.ArtifactSha256, consent.Request.Kind,
        consent.Request.Target, consent.Request.Reason, consent.Actor, consent.Created, consent.Request.ExpiresAt,
        consent.State is "AwaitingConfirmation" or "Active" && (consent.Request.ExpiresAt <= Now
            || consent.State == "AwaitingConfirmation" && consent.Created.AddMinutes(5) <= Now) ? "Expired" : consent.State, consent.Hash, token);
    private void EnsureDocument()
    {
        if (_documentHash is null)
        {
            if (File.Exists(_path) || File.Exists(_path + ".initialized")) throw Error("recovery_required");
        }
        else if (!File.Exists(_path) || !File.Exists(_path + ".initialized")
            || Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_path))) != _documentHash) throw Error("recovery_required");
    }
    private void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, _consents.ToArray()), ModulePackageJson.Options);
        if (bytes.Length > 8 * 1024 * 1024) throw Error("capacity");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.GetDirectoryName(_path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var marker = _path + ".initialized";
        if (!File.Exists(marker)) Write(marker, Encoding.UTF8.GetBytes("v1\n"));
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { Write(temporary, bytes); File.Move(temporary, _path, true); _documentHash = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Write(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        output.Write(bytes); output.Flush(true);
    }
    private static bool Text(string? text) => text is { Length: > 0 and <= 512 } && !text.Any(char.IsControl);
    private static bool DigestText(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigitLower);
    private static string Hash<T>(T value) => ModulePrivilegePlanStore.Hash(value);
    private static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Digest(Guid id, string session, string actor, string identityHash, string requestHash, DateTimeOffset created) =>
        Hash(new { id, session, actor, identityHash, requestHash, created });
    private static ModulePrivilegeException Error(string suffix, int status = 409) => new("module.privilege_" + suffix, status);
}
