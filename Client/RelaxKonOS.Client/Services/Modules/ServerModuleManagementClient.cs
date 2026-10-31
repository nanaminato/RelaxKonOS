using System.Net.Http.Headers;
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
            && (plan.AppliedOperationId is null || plan.AppliedOperationId != Guid.Empty));
    }

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition)
    { if (!condition) throw new InvalidOperationException("module-response-invalid"); }
    private static bool ValidIds(IReadOnlyList<string>? ids) => ids is { Count: <= 128 }
        && ids.All(id => id is { Length: > 0 and <= 128 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
        && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count;
    private static bool ValidDigest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
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
