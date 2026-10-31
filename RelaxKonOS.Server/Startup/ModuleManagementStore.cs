using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Startup;

internal sealed class ModuleManagementException(string code, int status = 409) : Exception(code)
{
    internal string Code => Message;
    internal int Status => status;
}

/// <summary>One atomic document owns desired configuration, frozen plans and operation receipts.
/// A process-lifetime file lock excludes another host; the revision also detects external edits.</summary>
internal sealed partial class ModuleManagementStore : IDisposable
{
    internal sealed record SavedPlan(string Actor, string Fingerprint, ServerModulePlanDto Plan, Guid? OperationId);
    internal sealed record Document(string ServiceId, string Revision, string[] EnabledIds,
        [property: JsonRequired] Dictionary<string, ServerModuleResidencyPolicyDto> Policies,
        List<SavedPlan> Plans, List<ServerModuleOperationDto> Operations);
    internal sealed record InventoryState(string Revision, IReadOnlySet<string> EnabledIds, IReadOnlyDictionary<string, Guid> PendingOperations,
        IReadOnlyDictionary<string, ServerModuleResidencyPolicyDto> Policies);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly FileStream _hostLock;
    private readonly bool _preserveUnixOwner;
    private Document _document;
    private string _fileHash;
    internal bool Writable { get; }
    internal bool RecoveryMode { get; }
    internal string ProcessGeneration { get; } = Guid.NewGuid().ToString("N");
    internal string EffectiveRevision { get; }
    internal IReadOnlyDictionary<string, ServerModuleResidencyPolicyDto> EffectivePolicies { get; }
    internal string ServiceId => _document.ServiceId;
    internal string Revision { get { lock (_gate) { ReadCurrent(); return _document.Revision; } } }
    internal IReadOnlySet<string> DesiredIds { get { lock (_gate) { ReadCurrent(); return _document.EnabledIds.ToHashSet(StringComparer.Ordinal); } } }
    internal InventoryState CaptureInventory()
    {
        lock (_gate)
        {
            ReadCurrent();
            var pending = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var operation in _document.Operations.Where(x => x.State is ServerModuleOperationState.AwaitingRestart
                or ServerModuleOperationState.Prepared or ServerModuleOperationState.Applying or ServerModuleOperationState.Verifying
                or ServerModuleOperationState.RecoveryRequired))
                foreach (var id in _document.Plans.Single(x => x.OperationId == operation.OperationId).Plan.Target.ModuleIds)
                    pending[id] = operation.OperationId;
            return new(_document.Revision, _document.EnabledIds.ToHashSet(StringComparer.Ordinal), pending,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, ServerModuleResidencyPolicyDto>(new Dictionary<string, ServerModuleResidencyPolicyDto>(_document.Policies, StringComparer.Ordinal)));
        }
    }

    internal ModuleManagementStore(IConfiguration configuration, string contentRoot) : this(configuration, contentRoot, true) { }

    private ModuleManagementStore(IConfiguration configuration, string contentRoot, bool initializePermissions,
        string? expectedServiceId = null, string? expectedRevision = null)
    {
        var authority = configuration["ModuleManagement:Authority"] ?? "deployment";
        _preserveUnixOwner = !initializePermissions && OperatingSystem.IsLinux();
        if (authority is not ("managed" or "deployment"))
            throw new InvalidOperationException("ModuleManagement:Authority must be managed or deployment.");
        Writable = authority == "managed";
        var mode = configuration["ModuleManagement:Mode"] ?? "normal";
        if (mode is not ("normal" or "recovery")) throw new InvalidOperationException("ModuleManagement:Mode must be normal or recovery.");
        RecoveryMode = mode == "recovery";
        if (RecoveryMode && (!Writable || !File.Exists(Path.Combine(contentRoot, "data", "module-management", "state.json"))))
            throw new InvalidOperationException("Module recovery requires an initialized managed state store.");
        if (Writable && configuration.GetSection("Modules").GetChildren().Any())
            throw new InvalidOperationException("Managed module authority cannot coexist with Modules deployment settings.");
        if (Writable && (configuration.GetSection("ModuleResidency:Strategy").Exists()
            || configuration.GetSection("ModuleResidency:IdleGraceSeconds").Exists()
            || configuration.GetSection("ModuleResidency:Modules").GetChildren().Any(module => module.GetChildren().Any(setting =>
                setting.Key.Equals("Strategy", StringComparison.OrdinalIgnoreCase) || setting.Key.Equals("IdleGraceSeconds", StringComparison.OrdinalIgnoreCase)))))
            throw new InvalidOperationException("Managed policy authority cannot coexist with deployment residency strategy/grace settings.");
        var directory = Path.Combine(contentRoot, "data", "module-management");
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            var info = new DirectoryInfo(directory);
            if (!info.Exists) info.Create(security); else if (initializePermissions) info.SetAccessControl(security);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (initializePermissions) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _path = Path.Combine(directory, "state.json");
        _hostLock = new FileStream(Path.Combine(directory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var configuredModules = ServerModules.Create(configuration);
            var configured = configuredModules.EnabledIds.Order(StringComparer.Ordinal).ToArray();
            var residency = ModuleResidencyOptions.Parse(configuration, configuredModules.KnownModuleIds, configuredModules.KnownReleasableModuleIds);
            var policies = configuredModules.KnownReleasableModuleIds.ToDictionary(id => id,
                id => new ServerModuleResidencyPolicyDto(residency.For(id).Strategy.ToString(), (int)residency.For(id).IdleGracePeriod.TotalSeconds), StringComparer.Ordinal);
            _document = File.Exists(_path) ? Read() : new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), configured, policies, [], []);
            if (expectedServiceId is not null && _document.ServiceId != expectedServiceId) throw new ModuleManagementException("module-server-mismatch");
            if (expectedRevision is not null && _document.Revision != expectedRevision) throw new ModuleManagementException("module-revision-conflict");
            _document = _document with { Operations = _document.Operations.Select(operation =>
                operation.State is ServerModuleOperationState.Prepared or ServerModuleOperationState.Applying or ServerModuleOperationState.Verifying
                    ? operation with { State = ServerModuleOperationState.Failed, ProblemCode = "module-operation-interrupted", UpdatedAt = DateTimeOffset.UtcNow }
                    : operation).ToList() };
            if (!Writable) _document = _document with { EnabledIds = configured, Policies = policies,
                Revision = Hash(JsonSerializer.Serialize(new { configured, policies }, RelaxKonOSJsonOptions.Default)) };
            _ = residency.WithPolicies(_document.Policies, configuredModules.KnownReleasableModuleIds);
            Save(_document);
            _fileHash = Hash(File.ReadAllText(_path));
            EffectiveRevision = RecoveryMode ? "recovery-" + ProcessGeneration : _document.Revision;
            EffectivePolicies = new System.Collections.ObjectModel.ReadOnlyDictionary<string, ServerModuleResidencyPolicyDto>(new Dictionary<string, ServerModuleResidencyPolicyDto>(_document.Policies, StringComparer.Ordinal));
        }
        catch { _hostLock.Dispose(); throw; }
    }

    internal IReadOnlyList<ServerModuleActionAdmission> Admissions(bool runtimeSupported = false, ServerModuleResidencyDto? residency = null, bool desiredEnabled = true) =>
        Enum.GetValues<ServerModuleAction>().Select(action =>
        {
            var reason = action switch
            {
                _ when RecoveryMode && action != ServerModuleAction.Enable => "module-recovery-mode",
                ServerModuleAction.Enable or ServerModuleAction.Disable => Writable ? null : "module-configuration-read-only",
                ServerModuleAction.SetResidencyPolicy when !Writable => "module-configuration-read-only",
                ServerModuleAction.SetResidencyPolicy => runtimeSupported ? null : "module-action-unsupported",
                ServerModuleAction.Wake or ServerModuleAction.TrySleep when !runtimeSupported => "module-action-unsupported",
                ServerModuleAction.Wake or ServerModuleAction.TrySleep when residency is null => "module-disabled",
                ServerModuleAction.Wake or ServerModuleAction.TrySleep when !desiredEnabled => "module-pending-disable",
                ServerModuleAction.TrySleep when !residency!.Releasable => residency.BlockedBy ?? "module-policy-keeps-loaded",
                ServerModuleAction.Wake or ServerModuleAction.TrySleep => null,
                _ => "module-action-unsupported"
            };
            return new ServerModuleActionAdmission(action, reason is null, reason is null ? [] : [new(reason)]);
        }).ToArray();

    internal ServerModulePlanDto Preview(string actor, ServerModulePlanRequest request, IReadOnlyList<ServerModuleDto> inventory)
    {
        lock (_gate)
        {
            ReadCurrent();
            if (request.ServiceId != ServiceId) throw new ModuleManagementException("module-server-mismatch");
            if (!Enum.IsDefined(request.Action) || request.ModuleIds is null || request.EnabledModuleIds is null
                || request.ModuleIds.Count == 0 || request.ModuleIds.Count > 128 || request.EnabledModuleIds.Count > 128
                || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128
                || request.ModuleIds.Any(string.IsNullOrWhiteSpace) || request.EnabledModuleIds.Any(string.IsNullOrWhiteSpace)
                || request.ModuleIds.Distinct(StringComparer.Ordinal).Count() != request.ModuleIds.Count
                || request.EnabledModuleIds.Distinct(StringComparer.Ordinal).Count() != request.EnabledModuleIds.Count)
                throw new ModuleManagementException("module-request-invalid", 400);
            request = request with { ModuleIds = request.ModuleIds.Order(StringComparer.Ordinal).ToArray(),
                EnabledModuleIds = request.EnabledModuleIds.Order(StringComparer.Ordinal).ToArray() };
            var fingerprint = Hash(JsonSerializer.Serialize(request, RelaxKonOSJsonOptions.Default));
            var existing = _document.Plans.Find(x => x.Actor == actor && x.Plan.Target.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null)
            {
                if (existing.Fingerprint != fingerprint) throw new ModuleManagementException("module-idempotency-conflict");
                return existing.Plan;
            }
            if (_document.Plans.Count >= 10000) throw new ModuleManagementException("module-history-capacity", 503);
            if (request.ExpectedRevision != _document.Revision) throw new ModuleManagementException("module-revision-conflict");
            var known = inventory.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            if (request.ModuleIds.Concat(request.EnabledModuleIds).Any(id => !known.Contains(id)))
                throw new ModuleManagementException("module-not-found", 404);
            var desired = request.EnabledModuleIds.ToHashSet(StringComparer.Ordinal);
            var added = desired.Except(_document.EnabledIds).Order(StringComparer.Ordinal).ToArray();
            var removed = _document.EnabledIds.Except(desired).Order(StringComparer.Ordinal).ToArray();
            var blockers = Validate(request, inventory);
            if ((IsRuntimeAction(request.Action) || request.Action == ServerModuleAction.SetResidencyPolicy)
                && (added.Length != 0 || removed.Length != 0)) blockers.Add(new("module-request-invalid"));
            // The action and named members must describe the entire difference. Dependency changes
            // must be included explicitly in ModuleIds and the exact enabled set by the caller.
            if (request.Action == ServerModuleAction.Enable && (removed.Length != 0 || request.ModuleIds.Any(id => !desired.Contains(id)))
                || request.Action == ServerModuleAction.Disable && (added.Length != 0 || request.ModuleIds.Any(desired.Contains))
                || added.Concat(removed).Any(id => !request.ModuleIds.Contains(id)))
                blockers.Add(new("module-request-invalid"));
            var restart = !IsRuntimeAction(request.Action) && !desired.SetEquals(inventory.Where(x => x.EffectiveEnabled).Select(x => x.Id));
            var plan = new ServerModulePlanDto(Guid.NewGuid(), ServiceId, request, _document.Revision,
                DateTimeOffset.UtcNow.AddMinutes(10), restart || request.Action == ServerModuleAction.SetResidencyPolicy
                    && request.ModuleIds.Any(id => !EffectivePolicies.TryGetValue(id, out var policy)
                        || policy != new ServerModuleResidencyPolicyDto(request.Strategy ?? "", request.IdleGraceSeconds ?? -1)), blockers, added, removed);
            Commit(_document with { Plans = [.. _document.Plans, new(actor, fingerprint, plan, null)] });
            return plan;
        }
    }

    private List<ServerModuleBlocker> Validate(ServerModulePlanRequest request, IReadOnlyList<ServerModuleDto> inventory)
    {
        List<ServerModuleBlocker> blockers = [];
        if (RecoveryMode && (request.Action != ServerModuleAction.Enable
            || !request.EnabledModuleIds.ToHashSet(StringComparer.Ordinal).SetEquals(inventory.Select(module => module.Id))))
            blockers.Add(new("module-recovery-mode"));
        if (!Writable && !IsRuntimeAction(request.Action)) blockers.Add(new("module-configuration-read-only"));
        if (request.Action is not (ServerModuleAction.Enable or ServerModuleAction.Disable or ServerModuleAction.SetResidencyPolicy) && !IsRuntimeAction(request.Action)) blockers.Add(new("module-action-unsupported"));
        if (request.ReleaseId is not null || request.Action != ServerModuleAction.SetResidencyPolicy && (request.Strategy is not null || request.IdleGraceSeconds is not null))
            blockers.Add(new("module-action-unsupported"));
        if (request.Action == ServerModuleAction.SetResidencyPolicy)
        {
            if (request.Strategy is not ("KeepLoaded" or "IdleDelayed" or "ReleaseOnLogout") || request.IdleGraceSeconds is not (>= 0 and <= 604800))
                blockers.Add(new("module-residency-policy-invalid"));
            foreach (var id in request.ModuleIds)
                if (!_document.Policies.ContainsKey(id)) blockers.Add(new("module-action-unsupported", id));
        }
        var desired = request.EnabledModuleIds.ToHashSet(StringComparer.Ordinal);
        if (_document.Operations.Any(x => x.State is ServerModuleOperationState.Prepared or ServerModuleOperationState.Applying or ServerModuleOperationState.Verifying))
            blockers.Add(new("module-operation-in-progress"));
        if (IsRuntimeAction(request.Action))
        {
            foreach (var id in request.ModuleIds)
            {
                var module = inventory.SingleOrDefault(x => x.Id == id);
                if (module is null) blockers.Add(new("module-not-found", id));
                else if (!module.EffectiveEnabled) blockers.Add(new("module-disabled", id));
                else if (!desired.Contains(id)) blockers.Add(new("module-pending-disable", id));
                else if (module.ResourceReleaseLevel != "objects") blockers.Add(new("module-action-unsupported", id));
                else if (module.RecoveryState == "required") blockers.Add(new("module-recovery-blocked", id));
                else if (request.Action == ServerModuleAction.TrySleep && module.Residency?.Releasable != true)
                    blockers.Add(new(module.Residency?.BlockedBy ?? "module-policy-keeps-loaded", id));
                else if (request.Action == ServerModuleAction.TrySleep && module.Residency?.ActiveLeases > 0)
                    blockers.Add(new("module-active-work", id));
            }
        }
        foreach (var module in inventory)
        {
            if (desired.Contains(module.Id))
                foreach (var dependency in module.Requires.Where(id => !desired.Contains(id))) blockers.Add(new("module-dependency-blocked", dependency));
            else if (module.OrphanedWork.Count > 0) blockers.Add(new("module-recovery-blocked", module.Id));
        }
        return blockers;
    }

    internal ServerModuleOperationDto Apply(string actor, Guid planId, IReadOnlyList<ServerModuleDto> inventory)
    {
        lock (_gate)
        {
            ReadCurrent();
            var saved = _document.Plans.Find(x => x.Plan.PlanId == planId && x.Actor == actor)
                ?? throw new ModuleManagementException("module-plan-not-found", 404);
            if (saved.OperationId is { } operationId) return _document.Operations.Single(x => x.OperationId == operationId);
            var plan = saved.Plan;
            if (IsRuntimeAction(plan.Target.Action)) throw new ModuleManagementException("module-runtime-executor-required");
            if (plan.ExpiresAt <= DateTimeOffset.UtcNow) throw new ModuleManagementException("module-plan-expired");
            if (plan.ExpectedRevision != _document.Revision) throw new ModuleManagementException("module-revision-conflict");
            var blockers = plan.BlockedBy.Concat(Validate(plan.Target, inventory)).ToArray();
            if (blockers.Length > 0) throw new ModuleManagementException(blockers[0].ProblemCode);
            var revision = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            var operation = new ServerModuleOperationDto(Guid.NewGuid(), planId, ServiceId,
                plan.RestartRequired ? ServerModuleOperationState.AwaitingRestart : ServerModuleOperationState.Succeeded,
                now, now, revision, plan.Target.EnabledModuleIds);
            var plans = _document.Plans.Select(x => x == saved ? x with { OperationId = operation.OperationId } : x).ToList();
            var policies = new Dictionary<string, ServerModuleResidencyPolicyDto>(_document.Policies, StringComparer.Ordinal);
            if (plan.Target.Action == ServerModuleAction.SetResidencyPolicy)
                foreach (var id in plan.Target.ModuleIds) policies[id] = new(plan.Target.Strategy!, plan.Target.IdleGraceSeconds!.Value);
            // The desired set and durable receipt share one atomic rename. A lost HTTP reply can
            // therefore always be recovered by applying the original plan again.
            Commit(_document with { Revision = revision, EnabledIds = plan.Target.EnabledModuleIds.ToArray(),
                Policies = policies, Plans = plans, Operations = [.. _document.Operations, operation] });
            return operation;
        }
    }

    internal ServerModuleOperationDto Operation(string actor, Guid id)
    {
        lock (_gate)
        {
            ReadCurrent();
            if (!_document.Plans.Any(x => x.Actor == actor && x.OperationId == id)) throw new ModuleManagementException("module-operation-not-found", 404);
            return _document.Operations.Single(x => x.OperationId == id);
        }
    }
    internal ServerModulePlanStatusDto PlanStatus(string actor, Guid id)
    {
        lock (_gate)
        {
            ReadCurrent();
            var saved = _document.Plans.SingleOrDefault(x => x.Actor == actor && x.Plan.PlanId == id)
                ?? throw new ModuleManagementException("module-plan-not-found", 404);
            return new(saved.Plan, saved.OperationId is { } operationId ? _document.Operations.Single(x => x.OperationId == operationId) : null);
        }
    }
    internal ServerModuleOperationPage Operations(string actor, string? moduleId, int offset, int limit)
    {
        lock (_gate)
        {
            ReadCurrent();
            if (offset < 0 || limit is < 1 or > 100) throw new ModuleManagementException("module-request-invalid", 400);
            var ids = _document.Plans.Where(x => x.Actor == actor && (moduleId is null || x.Plan.Target.ModuleIds.Contains(moduleId)))
                .Select(x => x.OperationId).ToHashSet();
            var results = _document.Operations.Where(x => ids.Contains(x.OperationId)).Reverse().Skip(offset).Take(limit + 1).ToArray();
            return new(results.Take(limit).ToArray(), results.Length > limit ? offset + limit : null);
        }
    }

    internal void VerifyStartup(IReadOnlySet<string> effective)
    {
        if (RecoveryMode) throw new ModuleManagementException("module-recovery-mode");
        lock (_gate)
        {
            ReadCurrent();
            bool Matches(ServerModuleOperationDto operation)
            {
                if (!effective.SetEquals(operation.EnabledModuleIds)) return false;
                var target = _document.Plans.Single(x => x.OperationId == operation.OperationId).Plan.Target;
                return target.Action != ServerModuleAction.SetResidencyPolicy || target.ModuleIds.All(id =>
                    EffectivePolicies.TryGetValue(id, out var policy) && policy == new ServerModuleResidencyPolicyDto(target.Strategy!, target.IdleGraceSeconds!.Value));
            }
            var operations = _document.Operations.Select(operation => !RequiresStartupVerification(operation) ? operation :
                operation with { State = Matches(operation) ? ServerModuleOperationState.Succeeded : ServerModuleOperationState.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow, ProblemCode = Matches(operation) ? null : "module-target-superseded" }).ToList();
            Commit(_document with { Operations = operations });
        }
    }
    private Document Read() => ReadDocument(_path);
    private static Document ReadDocument(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Module management state exceeds capacity.");
        var result = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), new JsonSerializerOptions(RelaxKonOSJsonOptions.Default)
        { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
        if (result is null || !Guid.TryParse(result.ServiceId, out _) || string.IsNullOrWhiteSpace(result.Revision)
            || result.EnabledIds is null || result.EnabledIds.Length > 128 || result.EnabledIds.Any(string.IsNullOrWhiteSpace)
            || result.EnabledIds.Distinct(StringComparer.Ordinal).Count() != result.EnabledIds.Length
            || result.Policies is null || result.Policies.Count > 128
            || result.Plans is null || result.Operations is null || result.Plans.Count > 10000 || result.Operations.Count > 10000
            || result.Plans.Any(x => x.Plan is null || string.IsNullOrWhiteSpace(x.Actor) || x.Plan.ServiceId != result.ServiceId
                || x.Plan.Target is null || x.Plan.Target.ServiceId != result.ServiceId)
            || result.Plans.Select(x => x.Plan.PlanId).Distinct().Count() != result.Plans.Count
            || result.Operations.Select(x => x.OperationId).Distinct().Count() != result.Operations.Count
            || result.Operations.Any(x => x.ServiceId != result.ServiceId || !result.Plans.Any(p => p.Plan.PlanId == x.PlanId && p.OperationId == x.OperationId)))
            throw new InvalidDataException("Module management state is invalid; host recovery is required.");
        return result;
    }
    private void ReadCurrent()
    {
        var hash = Hash(File.ReadAllText(_path));
        if (hash == _fileHash) return;
        // Never silently adopt out-of-band edits in a running process.
        throw new ModuleManagementException("module-revision-conflict");
    }
    private void Commit(Document next) { Save(next); _document = next; _fileHash = Hash(File.ReadAllText(_path)); }
    private void Save(Document next)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, next, RelaxKonOSJsonOptions.Default);
                if (stream.Length > 32 * 1024 * 1024) throw new ModuleManagementException("module-history-capacity", 503);
                stream.Flush(true);
            }
            if (_preserveUnixOwner) PreserveUnixOwner(temporary, _path);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() => _hostLock.Dispose();
}
