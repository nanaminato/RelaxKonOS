using System.Net.Http.Headers;
using RelaxKonOS.ModuleHost.Protocol;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Services.Modules;

/// <summary>Every call is bound to the login that opened the management surface.</summary>
public sealed class ServerModuleManagementClient(HttpClient http, IAuthSession session)
{
    private readonly object? _login = session.CurrentSession;
    private readonly string? _service = session.ServiceId;
    private string? _serverIdentity;
    private bool _serverMismatch;
    public bool IsCurrent => session.State == AuthSessionState.Authenticated
        && !_serverMismatch && ReferenceEquals(_login, session.CurrentSession) && _service == session.ServiceId;
    public async Task<ModulePrivilegePlan> PrivilegePlanAsync(Guid id)
    {
        var plan = await SendAsync<ModulePrivilegePlan>(HttpMethod.Get, ServerModulePrivilegeRoutes.Plans + "/" + id);
        Require(plan.PlanId == id && ValidIds([plan.ModuleId]) && ValidDigest(plan.PlanHash)
            && ValidDigest(plan.ArtifactSha256) && plan.Effects is { Length: <= 128 } && plan.Warnings is { Length: <= 128 }
            && ModuleCapabilityCatalog.IsCategory(plan.Kind) && ModuleCapabilityCatalog.IsAction(plan.Kind, plan.Action)
            && (plan.Kind switch
            {
                ModuleCapabilityCatalog.PackageManage => ValidPackageIdentity(plan.Package) && plan.File is null && plan.Data is null,
                ModuleCapabilityCatalog.FileManage => ValidFileIdentity(plan.File) && plan.Package is null && plan.Data is null,
                ModuleCapabilityCatalog.DataManage => ValidDataIdentity(plan.Data) && plan.Package is null && plan.File is null,
                _ => plan.Package is null && plan.File is null && plan.Data is null,
            })
            && plan.State is "AwaitingConfirmation" or "Executing" or "Succeeded" or "Failed" or "Cancelled" or "Expired" or "RecoveryRequired"
            && (plan.State != "AwaitingConfirmation" || ValidDigest(plan.ConfirmationToken)));
        return plan;
    }
    /// <summary>The frozen package identity is display-only on this side; a plan that names a package without
    /// one, or a service plan that carries one, is a protocol error rather than something to render.</summary>
    private static bool ValidPackageIdentity(ModulePackagePlanIdentity? package) => package is { } value
        && ValidText(value.SourceRef, 96) && ValidText(value.Package, 128) && ValidText(value.Version, 128)
        && value.Architecture is { Length: > 0 and <= 32 } && value.Architecture.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && ValidText(value.Repository, 8192) && ValidText(value.ArtifactFileName, 256)
        && ValidDigest(value.Sha256) && value.Bytes is > 0 and <= 536870912
        && value.Dependencies is { Length: <= 64 }
        && value.Dependencies.All(dependency => dependency is not null && ValidText(dependency.FileName, 256)
            && ValidDigest(dependency.Sha256) && dependency.Bytes is > 0 and <= 536870912)
        && value.Dependencies.Select(dependency => dependency.FileName).Distinct(StringComparer.Ordinal).Count() == value.Dependencies.Length;
    /// <summary>A file plan names one canonical path inside its declared root and the exact before/after
    /// state the user confirms. The body itself never crosses this contract.</summary>
    private static bool ValidFileIdentity(ModuleFilePlanIdentity? file) => file is { } value
        && ValidText(value.Root, 512) && ValidText(value.Path, 512)
        && value.Path.StartsWith(value.Root + "/", StringComparison.Ordinal)
        && value.Action is "write" or "delete"
        && value.BeforeKind is "file" or "absent"
        && value.BeforeBytes is >= 0 and <= 1073741824
        && (value.BeforeKind == "file" ? ValidDigest(value.BeforeSha256) : value.BeforeSha256 == "" && value.BeforeBytes == 0)
        && value.AfterKind is "file" or "absent"
        && value.AfterBytes is >= 0 and <= 1073741824
        && (value.AfterKind == "file" ? ValidDigest(value.AfterSha256) : value.AfterSha256 == "" && value.AfterBytes == 0)
        && value.Mode is >= 0 and <= 0x1ff
        && (value.Action == "write" ? value.AfterKind == "file" : value.AfterKind == "absent");
    /// <summary>A data plan names the exact declared root and its structural digest; it carries no file
    /// name or content and never a backup path on this host.</summary>
    private static bool ValidDataIdentity(ModuleDataPlanIdentity? data) => data is { } value
        && ValidText(value.Root, 512) && value.BeforeKind == "directory"
        && value.Action is "preserve" or "backup" or "delete"
        && value.BeforeFiles is >= 0 and <= 100000 && value.BeforeBytes is >= 0 and <= 1073741824
        && ValidDigest(value.BeforeDigest)
        && (value.Action == "backup" ? ValidResourceIdentifier(value.BackupRef) : value.BackupRef == "");
    private static bool ValidResourceIdentifier(string? value) => value is { Length: > 0 and <= 64 }
        && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    public Task<ModulePrivilegeStatus> ApprovePrivilegeAsync(Guid id, ModulePrivilegeApproval approval) =>
        SendAsync<ModulePrivilegeStatus>(HttpMethod.Post, ServerModulePrivilegeRoutes.Plans + "/" + id + "/approve", approval);
    public Task<ModulePrivilegeStatus> CancelPrivilegeAsync(Guid id) =>
        SendAsync<ModulePrivilegeStatus>(HttpMethod.Post, ServerModulePrivilegeRoutes.Plans + "/" + id + "/cancel");
    public async Task<ModulePrivilegeConsent> PrepareConsentAsync(ModulePrivilegeConsentRequest request)
    {
        var consent = await SendAsync<ModulePrivilegeConsent>(HttpMethod.Post, ServerModulePrivilegeRoutes.Consents, request);
        ValidateConsent(consent, false);
        Require(consent.ModuleId == request.ModuleId && consent.Kind == request.Kind && consent.Action == request.Action
            && consent.Target == request.Target && consent.Reason == request.Reason && consent.ExpiresAt == request.ExpiresAt);
        return consent;
    }
    public async Task<ModulePrivilegeConsent> ReadConsentAsync(Guid id)
    {
        var consent = await SendAsync<ModulePrivilegeConsent>(HttpMethod.Get, ServerModulePrivilegeRoutes.Consents + "/" + id);
        ValidateConsent(consent, true); Require(consent.ConsentId == id); return consent;
    }
    public async Task<ModulePrivilegeConsent[]> ReadConsentsAsync()
    {
        var consents = await SendAsync<ModulePrivilegeConsent[]>(HttpMethod.Get, ServerModulePrivilegeRoutes.Consents);
        Require(consents is { Length: <= 1000 } && consents.All(c => c is not null) && consents.Select(c => c.ConsentId).Distinct().Count() == consents.Length);
        foreach (var consent in consents) ValidateConsent(consent, false);
        return consents;
    }
    public async Task<ModulePrivilegeConsent> ApproveConsentAsync(Guid id, ModulePrivilegeApproval approval)
    {
        var consent = await SendAsync<ModulePrivilegeConsent>(HttpMethod.Post, ServerModulePrivilegeRoutes.Consents + "/" + id + "/approve", approval);
        ValidateConsent(consent, false); Require(consent.ConsentId == id && consent.State == "Active"); return consent;
    }
    public async Task<ModulePrivilegeConsent> RevokeConsentAsync(Guid id)
    {
        var consent = await SendAsync<ModulePrivilegeConsent>(HttpMethod.Post, ServerModulePrivilegeRoutes.Consents + "/" + id + "/revoke");
        ValidateConsent(consent, false); Require(consent.ConsentId == id && consent.State is "Revoked" or "Cancelled"); return consent;
    }
    private static void ValidateConsent(ModulePrivilegeConsent consent, bool review)
    {
        Require(consent is not null && consent.ConsentId != Guid.Empty && ValidIds([consent.ModuleId])
            && consent.Version is { Length: > 0 and <= 32 } && consent.PublisherId is { Length: > 0 and <= 128 }
            && ValidDigest(consent.ArtifactSha256) && ValidDigest(consent.PlanHash)
            && ModuleCapabilityCatalog.IsCategory(consent.Kind) && ModuleCapabilityCatalog.IsAction(consent.Kind, consent.Action)
            && consent.Target is { Length: > 0 and <= 256 } && consent.Reason is { Length: > 0 and <= 512 }
            && consent.GrantedBy is { Length: > 0 and <= 512 } && consent.CreatedAt != default
            && consent.ExpiresAt > consent.CreatedAt && consent.ExpiresAt <= consent.CreatedAt.AddDays(30)
            && consent.State is "AwaitingConfirmation" or "Active" or "Revoked" or "Cancelled" or "Expired"
            && (review && consent.State == "AwaitingConfirmation" ? ValidDigest(consent.ConfirmationToken) : consent.ConfirmationToken == ""));
    }

    public async Task<IReadOnlyList<ServerModuleDto>> ReadAsync()
    {
        var facts = await SendAsync<IReadOnlyList<ServerModuleDto>>(HttpMethod.Get, ServerApiRoutes.Modules);
        if (facts.Count is 0 or > 128 || facts.Any(x => string.IsNullOrEmpty(x.Id) || string.IsNullOrEmpty(x.ServiceId)
            || string.IsNullOrEmpty(x.DesiredRevision) || string.IsNullOrEmpty(x.ProcessGeneration))
            || facts.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != facts.Count
            || facts.Select(x => x.ServiceId).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidOperationException("module-response-invalid");
        var identity = facts[0].ServiceId!;
        if (_serverIdentity is not null && _serverIdentity != identity)
        {
            _serverMismatch = true;
            throw new InvalidOperationException("module-server-mismatch");
        }
        _serverIdentity = identity;
        return facts;
    }
    public async Task<ServerModulePlanDto> PreviewAsync(ServerModulePlanRequest request)
    {
        Require(_serverIdentity is not null && request.ServiceId == _serverIdentity && ValidIds(request.ModuleIds) && ValidIds(request.EnabledModuleIds));
        var plan = await SendAsync<ServerModulePlanDto>(HttpMethod.Post, ServerModuleManagementRoutes.Plans, request);
        ValidatePlan(plan);
        ServerModulePlanRequest Canonical(ServerModulePlanRequest target) => target with
        { ModuleIds = target.ModuleIds.Order(StringComparer.Ordinal).ToArray(), EnabledModuleIds = target.EnabledModuleIds.Order(StringComparer.Ordinal).ToArray() };
        Require(JsonSerializer.Serialize(Canonical(request), RelaxKonOSJsonOptions.Default) == JsonSerializer.Serialize(Canonical(plan.Target), RelaxKonOSJsonOptions.Default));
        return plan;
    }
    public async Task<ServerModulePlanStatusDto> PlanStatusAsync(Guid id)
    {
        var result = await SendAsync<ServerModulePlanStatusDto>(HttpMethod.Get, ServerModuleManagementRoutes.Plans + "/" + id);
        Require(result.Plan is not null); ValidatePlan(result.Plan);
        Require(result.Plan.PlanId == id);
        if (result.Operation is { } operation)
        {
            ValidateOperation(operation); Require(operation.PlanId == id
                && operation.EnabledModuleIds.ToHashSet(StringComparer.Ordinal).SetEquals(result.Plan.Target.EnabledModuleIds));
        }
        return result;
    }
    public async Task<ServerModuleOperationDto> ApplyAsync(Guid planId)
    {
        var result = await SendAsync<ServerModuleOperationDto>(HttpMethod.Post, ServerModuleManagementRoutes.Plans + "/" + planId + "/apply");
        ValidateOperation(result); Require(result.PlanId == planId);
        return result;
    }
    public async Task<ServerModuleOperationDto> OperationAsync(Guid id)
    {
        var result = await SendAsync<ServerModuleOperationDto>(HttpMethod.Get, ServerModuleManagementRoutes.Operations + "/" + id);
        ValidateOperation(result); Require(result.OperationId == id);
        return result;
    }
    public async Task<ServerModuleOperationPage> HistoryAsync()
    {
        var page = await SendAsync<ServerModuleOperationPage>(HttpMethod.Get, ServerModuleManagementRoutes.Operations);
        Require(page.Items is { Count: <= 100 } && page.NextOffset is null or > 0);
        foreach (var operation in page.Items) ValidateOperation(operation);
        Require(page.Items.Select(x => x.OperationId).Distinct().Count() == page.Items.Count);
        return page;
    }
    public async Task<ServerInstalledModulePageDto> InstalledPackagesAsync()
    {
        var page = await SendAsync<ServerInstalledModulePageDto>(HttpMethod.Get, ServerModuleManagementRoutes.InstalledPackages);
        Require(page.Items is { Count: <= 128 }
            && page.Items.All(module => module is not null && ValidIds([module.ModuleId]) && module.Version is { Length: > 0 and <= 32 }
                && module.ReleaseId is { Length: > 0 and <= 128 } && ValidDigest(module.ArtifactSha256)
                && ValidDigest(module.ManifestSha256) && module.InstalledAt != default)
            && page.Items.Select(module => module.ModuleId).Distinct(StringComparer.Ordinal).Count() == page.Items.Count);
        return page;
    }
    public async Task<ServerInstalledModulePageDto> PackageVersionsAsync(string moduleId)
    {
        Require(ValidIds([moduleId]));
        var page = await SendAsync<ServerInstalledModulePageDto>(HttpMethod.Get,
            ServerModuleManagementRoutes.PackageVersions.Replace("{moduleId}", Uri.EscapeDataString(moduleId)));
        Require(page.Items is { Count: <= 128 }
            && page.Items.All(module => module is not null && module.ModuleId == moduleId && module.Version is { Length: > 0 and <= 32 }
                && ValidDigest(module.ArtifactSha256) && module.InstalledAt != default));
        return page;
    }
    public async Task<ServerPackageOperationPageDto> PackageOperationsAsync()
    {
        var page = await SendAsync<ServerPackageOperationPageDto>(HttpMethod.Get, ServerModuleManagementRoutes.PackageOperations);
        Require(page.Items is { Count: <= 128 }
            && page.Items.All(operation => operation is not null && operation.OperationId != Guid.Empty
                && operation.Action is "install" or "rollback" && ValidIds([operation.ModuleId])
                && operation.Version is { Length: > 0 and <= 32 } && ValidDigest(operation.ArtifactSha256)
                && operation.State is "AwaitingRestart" or "Succeeded" or "Failed"
                && operation.CreatedAt != default && operation.UpdatedAt >= operation.CreatedAt));
        return page;
    }

    public async Task<ServerModuleStorePageDto> SearchStoreAsync(string? query, string? sourceId = null)
    {
        Require(query is null or { Length: <= 128 } && (sourceId is null || ValidIds([sourceId])));
        var route = ServerModuleManagementRoutes.ModuleStoreSearch + "?limit=25";
        if (!string.IsNullOrWhiteSpace(query)) route += "&q=" + Uri.EscapeDataString(query);
        if (!string.IsNullOrWhiteSpace(sourceId)) route += "&sourceId=" + Uri.EscapeDataString(sourceId);
        var page = await SendAsync<ServerModuleStorePageDto>(HttpMethod.Get, route);
        Require(page.Items is { Count: <= 50 } && page.NextOffset is null or > 0
            && page.BlockedBy is { Count: <= 64 }
            && page.Items.All(item => item is not null && ValidIds([item.SourceId]) && ValidIds([item.ModuleId])
                && item.Version is { Length: > 0 and <= 32 } && item.ReleaseId is { Length: > 0 and <= 128 }
                && ValidDigest(item.ArtifactSha256) && ValidDigest(item.ManifestSha256) && item.ArchiveBytes > 0
                && item.BlockedBy is { Count: <= 64 }));
        return page;
    }

    public async Task<ServerModuleStoreSourcePageDto> StoreSourcesAsync()
    {
        var page = await SendAsync<ServerModuleStoreSourcePageDto>(HttpMethod.Get,
            ServerModuleManagementRoutes.ModuleStoreSources);
        Require((page.CatalogRevision == "uninitialized" || page.CatalogRevision is { Length: 32 }
                && page.CatalogRevision.All(char.IsAsciiHexDigitLower))
            && page.Sources is { Count: <= 16 } && page.Sources.All(source => source is not null
            && ValidIds([source.SourceId]) && source.Status is "current" or "refresh-required" or "unavailable"
            && source.Sequence >= 0 && source.PolicyRevision >= 0 && source.ReleaseCount >= 0
            && source.RevokedCount >= 0 && ValidCode(source.ProblemCode)));
        return page;
    }

    public async Task<ServerModuleCatalogReceiptDto> RefreshCatalogAsync(string sourceId, string expectedRevision,
        string idempotencyKey)
    {
        Require(ValidIds([sourceId])
            && (expectedRevision == "uninitialized" || expectedRevision is { Length: 32 }
                && expectedRevision.All(char.IsAsciiHexDigitLower))
            && idempotencyKey is { Length: > 0 and <= 128 }
            && idempotencyKey.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
        var receipt = await SendAsync<ServerModuleCatalogReceiptDto>(HttpMethod.Post,
            ServerModuleManagementRoutes.ModuleStoreRefresh, new ServerModuleCatalogRefreshRequest(sourceId,
                expectedRevision, idempotencyKey, null));
        Require(receipt is not null && receipt.SourceId == sourceId && receipt.Revision is { Length: 32 }
            && receipt.Revision.All(char.IsAsciiHexDigitLower) && receipt.Sequence > 0
            && ValidDigest(receipt.PayloadSha256) && receipt.AcceptedAt != default);
        return receipt;
    }

    public async Task<ServerModulePackageUploadDto> DownloadStorePackageAsync(string sourceId, string releaseId)
    {
        Require(ValidIds([sourceId]) && ValidIds([releaseId]));
        var upload = await SendAsync<ServerModulePackageUploadDto>(HttpMethod.Post,
            ServerModuleManagementRoutes.ModuleStoreDownload, new ServerModuleStoreDownloadRequest(sourceId, releaseId));
        Require(upload is not null && upload.UploadId is { Length: 32 } && upload.UploadId.All(char.IsAsciiHexDigitLower)
            && upload.Bytes > 0 && ValidDigest(upload.Sha256));
        return upload;
    }

    public async Task<ServerModuleHealthPageDto> CheckHealthAsync()
    {
        var report = await SendAsync<ServerModuleHealthPageDto>(HttpMethod.Get, ServerModuleManagementRoutes.ModuleHealth);
        Require(report is not null && report.CheckedAt != default && report.Modules is { Count: <= 256 }
            && report.Modules.All(module => module is not null && ValidIds([module.ModuleId])
                && module.Status is "host" or "loaded" or "healthy" or "unhealthy" or "quarantined" or "load-failed"
                && ValidCode(module.ProblemCode))
            && report.Modules.Select(module => module.ModuleId).Distinct(StringComparer.Ordinal).Count() == report.Modules.Count);
        return report;
    }

    public async Task<ServerModulePackageUploadDto> UploadPackageAsync(Stream content, long length)
    {
        Require(content.CanRead && length is > 0 and <= 1L << 30);
        using var body = new StreamContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.ContentLength = length;
        var upload = await SendAsync<ServerModulePackageUploadDto>(HttpMethod.Post, ServerModuleManagementRoutes.PackageUpload,
            content: body);
        Require(upload is not null && upload.UploadId is { Length: 32 } && upload.UploadId.All(char.IsAsciiHexDigitLower)
            && upload.Bytes == length && ValidDigest(upload.Sha256));
        return upload;
    }

    public async Task<ServerModulePackagePlanDto> AutoPlanPackageAsync(string uploadId)
    {
        Require(uploadId is { Length: 32 } && uploadId.All(char.IsAsciiHexDigitLower));
        var plan = await SendAsync<ServerModulePackagePlanDto>(HttpMethod.Post, ServerModuleManagementRoutes.PackageAutoPlan,
            new ServerModulePackageAutoPlanRequest(uploadId));
        ValidatePackagePlan(plan);
        return plan;
    }

    public async Task<ServerModulePackageInstallResultDto> ApplyPackagePlanAsync(Guid planId)
    {
        Require(planId != Guid.Empty);
        var result = await SendAsync<ServerModulePackageInstallResultDto>(HttpMethod.Post,
            ServerModuleManagementRoutes.PackagePlanApply.Replace("{planId:guid}", planId.ToString()));
        Require(result is not null && result.OperationId != Guid.Empty && ValidInstalled(result.Record));
        return result;
    }

    public async Task<ServerModulePackageInstallResultDto> RollbackPackageAsync(string moduleId, string version)
    {
        Require(ValidIds([moduleId]) && version is { Length: > 0 and <= 32 });
        var result = await SendAsync<ServerModulePackageInstallResultDto>(HttpMethod.Post,
            ServerModuleManagementRoutes.PackageRollback, new ServerModulePackageRollbackRequest(moduleId, version));
        Require(result is not null && result.OperationId != Guid.Empty && ValidInstalled(result.Record));
        return result;
    }

    private static bool ValidInstalled(ServerInstalledModuleDto? module) => module is not null
        && ValidIds([module.ModuleId]) && module.Version is { Length: > 0 and <= 32 }
        && module.ReleaseId is { Length: > 0 and <= 128 } && ValidDigest(module.ArtifactSha256)
        && ValidDigest(module.ManifestSha256) && module.InstalledAt != default;

    private static void ValidatePackagePlan(ServerModulePackagePlanDto plan)
    {
        Require(plan is not null && plan.PlanId != Guid.Empty && ValidIds([plan.ModuleId])
            && plan.Version is { Length: > 0 and <= 32 } && ValidDigest(plan.ArtifactSha256)
            && ValidDigest(plan.ManifestSha256) && plan.ExpiresAt != default
            && plan.Runtime is "in-process" or "module-host" && plan.Privileges is { Count: <= 64 }
            && plan.Privileges.All(declaration => declaration is not null && ModuleCapabilityCatalog.IsCategory(declaration.Kind)
                && declaration.Platforms is { Count: > 0 and <= 3 } && declaration.Platforms.All(ModuleCapabilityCatalog.IsPlatform)
                && declaration.Resources is { Count: <= 128 } && declaration.Resources.All(resource => resource is { Length: > 0 and <= 1024 }))
            && plan.Name is { Length: > 0 and <= 128 } && plan.Summary is { Length: <= 512 }
            && (plan.AppliedOperationId is null || plan.AppliedOperationId != Guid.Empty));
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition)
    { if (!condition) throw new InvalidOperationException("module-response-invalid"); }
    private static bool ValidIds(IReadOnlyList<string>? ids) => ids is { Count: <= 128 }
        && ids.All(id => id is { Length: > 0 and <= 128 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
        && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count;
    private static bool ValidDigest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static bool ValidText(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum
        && !value.Any(char.IsControl);
    private static bool ValidCode(string? code) => code is null || code.Length is > 7 and <= 96 && code.StartsWith("module-", StringComparison.Ordinal)
        && code.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    private void ValidatePlan(ServerModulePlanDto plan)
    {
        Require(_serverIdentity is not null && plan.PlanId != Guid.Empty && plan.ServiceId == _serverIdentity && plan.Target is not null
            && plan.Target.ServiceId == _serverIdentity && plan.Target.ExpectedRevision == plan.ExpectedRevision
            && !string.IsNullOrWhiteSpace(plan.ExpectedRevision) && plan.ExpiresAt != default
            && ValidIds(plan.Target.ModuleIds) && plan.Target.ModuleIds.Count > 0 && ValidIds(plan.Target.EnabledModuleIds)
            && ValidIds(plan.Added) && ValidIds(plan.Removed) && plan.BlockedBy is { Count: <= 256 });
        Require(plan.BlockedBy.All(reason => reason is not null && reason.ProblemCode is not null && ValidCode(reason.ProblemCode)));
    }
    private void ValidateOperation(ServerModuleOperationDto operation)
    {
        Require(operation is not null && _serverIdentity is not null && operation.ServiceId == _serverIdentity
            && operation.OperationId != Guid.Empty && operation.PlanId != Guid.Empty && Enum.IsDefined(operation.State)
            && operation.CreatedAt != default && operation.UpdatedAt != default && !string.IsNullOrWhiteSpace(operation.DesiredRevision)
            && ValidIds(operation.EnabledModuleIds) && ValidCode(operation.ProblemCode));
        Require(operation.ModuleResults is null || operation.ModuleResults.Count <= 128
            && operation.ModuleResults.All(result => result is not null && ValidIds([result.ModuleId]) && ValidCode(result.ProblemCode))
            && operation.ModuleResults.Select(result => result.ModuleId).Distinct(StringComparer.Ordinal).Count() == operation.ModuleResults.Count);
        Require(operation.State != ServerModuleOperationState.Succeeded || operation.ProblemCode is null && operation.ModuleResults?.All(result => result.Succeeded) != false);
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body = null, HttpContent? content = null)
    {
        if (!IsCurrent || session.EffectiveBaseUrl is not { } baseUrl) throw new InvalidOperationException("module-session-changed");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), route.TrimStart('/')));
        if (content is not null) request.Content = content;
        else if (body is not null) request.Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default);
        using var response = await http.SendAsync(request);
        if (!IsCurrent) throw new InvalidOperationException("module-session-changed");
        if (!response.IsSuccessStatusCode)
        {
            var code = "module-http-" + (int)response.StatusCode;
            try
            {
                using var problem = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 65536));
                if (problem.RootElement.TryGetProperty("problemCode", out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } problemCode && ValidCode(problemCode)) code = problemCode;
            }
            catch (JsonException) { }
            throw new InvalidOperationException(code);
        }
        var result = JsonSerializer.Deserialize<T>(await ReadBoundedAsync(response.Content, 2 * 1024 * 1024), RelaxKonOSJsonOptions.Default)
            ?? throw new InvalidOperationException("module-response-invalid");
        if (!IsCurrent) throw new InvalidOperationException("module-session-changed");
        return result;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidOperationException("module-response-invalid");
        using var buffer = new MemoryStream();
        using var stream = await content.ReadAsStreamAsync();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + count > maximum) throw new InvalidOperationException("module-response-invalid");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
}
