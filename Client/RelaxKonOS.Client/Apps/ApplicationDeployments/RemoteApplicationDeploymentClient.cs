using System.Net.Http.Headers;
using System.Net.Http.Json;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments;

/// <summary>
/// Typed JWT client for application deployment. It never sends a host path, a secret value, or shell
/// text: an archive travels as an upload reference and a secret travels as a value on its own create
/// or update request only.
/// </summary>
public sealed class RemoteApplicationDeploymentClient(HttpClient http, IAuthSession session) : IRemoteApplicationDeploymentClient
{
    public Task<IReadOnlyList<ApplicationDeploymentTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<ApplicationDeploymentTemplateDto>>(ApplicationDeploymentApiRoutes.Templates, cancellationToken);

    public Task<IReadOnlyList<ApplicationDto>> ListApplicationsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<ApplicationDto>>(ApplicationDeploymentApiRoutes.Applications, cancellationToken);

    public Task<ApplicationDeploymentSnapshotDto?> GetSnapshotAsync(Guid applicationId, CancellationToken cancellationToken = default) =>
        TrySendAsync<ApplicationDeploymentSnapshotDto>(ApplicationDeploymentApiRoutes.Application(applicationId), cancellationToken);

    public Task<ApplicationDto> CreateApplicationAsync(CreateApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync<ApplicationDto>(HttpMethod.Post, ApplicationDeploymentApiRoutes.Applications, request, idempotencyKey, cancellationToken);

    public Task<ApplicationDto> UpdateApplicationAsync(Guid applicationId, UpdateApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync<ApplicationDto>(HttpMethod.Put, ApplicationDeploymentApiRoutes.Application(applicationId), request, idempotencyKey, cancellationToken);

    public Task<IReadOnlyList<ApplicationRevisionDto>> ListRevisionsAsync(Guid applicationId, CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<ApplicationRevisionDto>>(ApplicationDeploymentApiRoutes.Revisions(applicationId), cancellationToken);

    public Task<IReadOnlyList<DeploymentOperationDto>> ListOperationsAsync(Guid applicationId, int limit = 50, CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<DeploymentOperationDto>>($"{ApplicationDeploymentApiRoutes.ApplicationOperations(applicationId)}?limit={limit}", cancellationToken);

    public Task<DeploymentLogDto> GetLogsAsync(Guid applicationId, int tail = 200, CancellationToken cancellationToken = default) =>
        SendAsync<DeploymentLogDto>($"{ApplicationDeploymentApiRoutes.Logs(applicationId)}?tail={tail}", cancellationToken);

    public Task<DeploymentOperationDiagnosticsDto?> GetOperationDiagnosticsAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        TrySendAsync<DeploymentOperationDiagnosticsDto>(ApplicationDeploymentApiRoutes.OperationLogs(operationId), cancellationToken);

    public Task<DeploymentOperationDto> DeployAsync(Guid applicationId, DeployApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync<DeploymentOperationDto>(HttpMethod.Post, ApplicationDeploymentApiRoutes.Deploy(applicationId), request, idempotencyKey, cancellationToken);

    public Task<DeploymentOperationDto> RollbackAsync(Guid applicationId, RollbackApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync<DeploymentOperationDto>(HttpMethod.Post, ApplicationDeploymentApiRoutes.Rollback(applicationId), request, idempotencyKey, cancellationToken);

    public Task<DeploymentOperationDto> LifecycleAsync(Guid applicationId, string action, ApplicationLifecycleRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var route = action switch
        {
            "start" => ApplicationDeploymentApiRoutes.Start(applicationId),
            "stop" => ApplicationDeploymentApiRoutes.Stop(applicationId),
            "restart" => ApplicationDeploymentApiRoutes.Restart(applicationId),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        return SendAsync<DeploymentOperationDto>(HttpMethod.Post, route, request, idempotencyKey, cancellationToken);
    }

    public Task<DeploymentOperationDto> DeleteAsync(Guid applicationId, DeleteApplicationRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync<DeploymentOperationDto>(HttpMethod.Delete, ApplicationDeploymentApiRoutes.Application(applicationId), request, idempotencyKey, cancellationToken);

    public Task<DeploymentOperationDto?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        TrySendAsync<DeploymentOperationDto>(ApplicationDeploymentApiRoutes.Operation(operationId), cancellationToken);

    public Task<DeploymentOperationDto?> GetActiveOperationAsync(Guid applicationId, CancellationToken cancellationToken = default) =>
        TrySendAsync<DeploymentOperationDto>($"{ApplicationDeploymentApiRoutes.ActiveOperation()}?applicationId={applicationId:D}", cancellationToken);

    public Task<DeploymentOperationDto> CancelOperationAsync(Guid operationId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync<DeploymentOperationDto>(HttpMethod.Post, ApplicationDeploymentApiRoutes.Cancel(operationId), null, idempotencyKey, cancellationToken);

    public IAsyncDisposable WatchLogs(Guid operationId, Action<RelaxKonOS.Protocol.Hubs.DeploymentLiveLogSnapshot> receive, Action<bool> connectionChanged)
        => new DeploymentLogStream(session, operationId, receive, connectionChanged);

    public async Task<DeploymentStagedFileDto> UploadArchiveAsync(string fileName, Stream content, IProgress<DeploymentUploadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new DeploymentUploadContent(content, progress);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await SendAsync<DeploymentStagedFileDto>(HttpMethod.Post, ApplicationDeploymentApiRoutes.Uploads, form, null, cancellationToken);
    }

    public Task<DeploymentStagedFileDto> CreateFileReferenceAsync(string path, CancellationToken cancellationToken = default) =>
        SendAsync<DeploymentStagedFileDto>(HttpMethod.Post, ApplicationDeploymentApiRoutes.FileReferences,
            new CreateDeploymentFileReferenceRequest(path), null, cancellationToken);

    private Task<T> SendAsync<T>(string route, CancellationToken cancellationToken) =>
        SendAsync<T>(HttpMethod.Get, route, null, null, cancellationToken);

    private async Task<T?> TrySendAsync<T>(string route, CancellationToken cancellationToken) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = deadline.Token;
        using var request = CreateRequest(HttpMethod.Get, route);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ThrowIfProblemAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string route, HttpContent? body, string? idempotencyKey, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(body is MultipartFormDataContent ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(30));
        cancellationToken = deadline.Token;
        using var request = CreateRequest(method, route, idempotencyKey);
        if (body is not null) request.Content = body;
        if (body is MultipartFormDataContent)
        {
            // Let authentication and size checks reject the request before streaming a large body.
            request.Headers.ExpectContinue = true;
            request.Options.Set(RelaxKonOS.Client.Services.Diagnostics.NetworkDiagnosticsHandler.SkipRequestBodyCapture, true);
        }
        using var response = await http.SendAsync(request, cancellationToken);
        await ThrowIfProblemAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new InvalidOperationException(LocalizedText.Get("application_deployments.error.empty_response"));
    }

    private Task<T> SendAsync<T>(HttpMethod method, string route, object body, string? idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync<T>(method, route, JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default), idempotencyKey, cancellationToken);

    private HttpRequestMessage CreateRequest(HttpMethod method, string route, string? idempotencyKey = null)
    {
        if (session.State != AuthSessionState.Authenticated || session.Tokens is null || session.ServerUrl is null)
            throw new InvalidOperationException(LocalizedText.Get("application_deployments.error.not_signed_in"));
        var request = new HttpRequestMessage(method, new Uri(new Uri(session.ServerUrl), route.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Tokens.AccessToken);
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return request;
    }

    /// <summary>
    /// Surfaces the server's stable problem code instead of a raw HTTP status, so the UI can localize
    /// it. The code is the product message; the transport detail never reaches the operator.
    /// </summary>
    private static async Task ThrowIfProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var code = await ReadProblemCodeAsync(response, cancellationToken);
        // List endpoints use 404 to mean that an older Server has no application-deployment API.
        // Preserve protocol problem codes for actual missing application resources, but never show a
        // generic HTTP title or an internal fallback code to the operator.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound &&
            !IsApplicationDeploymentProblemCode(code))
            code = "application-deployment.http_404";
        throw new ApplicationDeploymentClientException(code ?? $"application-deployment.http_{(int)response.StatusCode}", (int)response.StatusCode);
    }

    private static bool IsApplicationDeploymentProblemCode(string? code) =>
        code?.StartsWith("application-deployment.", StringComparison.Ordinal) == true;

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<DeploymentProblemDetails>(RelaxKonOSJsonOptions.Default, cancellationToken);
            if (!string.IsNullOrWhiteSpace(problem?.ProblemCode)) return problem.ProblemCode;
            return string.IsNullOrWhiteSpace(problem?.Title) ? null : problem.Title;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private sealed record DeploymentProblemDetails(string? Title, string? ProblemCode);
}

/// <summary>A failure carrying the server's stable problem code, which the UI localizes.</summary>
public sealed class ApplicationDeploymentClientException(string problemCode, int statusCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
    public int StatusCode { get; } = statusCode;
}
