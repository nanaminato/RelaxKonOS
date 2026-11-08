using RelaxKonOS.Server.Startup;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Certificate;

namespace RelaxKonOS.Server.Endpoints;

public static class CertificateEndpoints
{
    public static IEndpointRouteBuilder MapCertificateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(CertificateApiRoutes.Certificates).RequireAuthorization().WithTags("Certificates").RequireHostFeature(ServerHostFeature.Certificates);
        group.MapGet(CertificateApiRoutes.RenewalRunsPattern, (RelaxKonOS.Server.Certificate.CertificateRenewalRunRepository runs,
            RelaxKonOS.Server.Certificate.CertificateOperationStore operations, CancellationToken ct) => runs.ListAsync(operations, ct));
        group.MapGet(CertificateApiRoutes.RenewalHistoryPattern, (Guid id, RelaxKonOS.Server.Certificate.CertificateOperationStore operations, CancellationToken ct) => operations.GetRenewalHistoryAsync(id, ct));
        group.MapGet(CertificateApiRoutes.CollectionPattern, (RelaxKonOS.Server.Certificate.ICertificateManager manager, CancellationToken ct) => manager.ListAsync(ct));
        group.MapGet(CertificateApiRoutes.ByIdPattern, async (Guid id, RelaxKonOS.Server.Certificate.ICertificateManager manager, CancellationToken ct) =>
            await manager.GetAsync(id, ct) is { } certificate ? Results.Ok(certificate) : Results.NotFound());
        group.MapPost(CertificateApiRoutes.PreflightPattern, (CertificatePreflightRequest request, RelaxKonOS.Server.Certificate.ICertificateManager manager, CancellationToken ct) => manager.PreflightAsync(request, ct));
        group.MapPost(CertificateApiRoutes.CollectionPattern, async (RequestCertificateRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Certificate.ICertificateManager manager, CancellationToken ct) =>
            await StartAuthorizedAsync(context, elevations, HostElevationCapability.CertificateIssue, "certificates/issue", key => manager.RequestAsync(key, request, Actor(context), ct)));
        group.MapPost(CertificateApiRoutes.SelfSignedPattern, async (CreateSelfSignedCertificateRequest request, HttpContext context,
            IHostElevationSessionStore elevations, RelaxKonOS.Server.Certificate.ICertificateManager manager, CancellationToken ct) =>
        {
            if (!elevations.IsGranted(context.User, HostElevationCapability.CertificateCreateSelfSigned, "certificates/self-signed"))
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    type: "https://relaxkonos.app/problems/elevation-required",
                    extensions: new Dictionary<string, object?> { ["problemCode"] = "elevation-required" });
            return await StartAsync(context, key => manager.CreateSelfSignedAsync(key, request, Actor(context), ct));
        });
        group.MapGet(CertificateApiRoutes.DeployPattern, (Guid id, RelaxKonOS.Server.Certificate.ICertificateManager manager, CancellationToken ct) =>
            manager.GetKestrelDeploymentAsync(id, ct));
        group.MapPost(CertificateApiRoutes.DeployPattern, async (Guid id, HttpContext context, IHostElevationSessionStore elevations,
            RelaxKonOS.Server.Certificate.ICertificateManager manager, CancellationToken ct) =>
        {
            if (!elevations.IsGranted(context.User, HostElevationCapability.CertificateReplaceServerHttps, $"certificates/{id:D}/server-https"))
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    type: "https://relaxkonos.app/problems/elevation-required",
                    extensions: new Dictionary<string, object?> { ["problemCode"] = "elevation-required" });
            return await StartAsync(context, key => manager.DeployKestrelAsync(id, key, Actor(context), ct));
        });
        group.MapPost(CertificateApiRoutes.RenewPattern, async (Guid id, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Certificate.ICertificateManager manager, CancellationToken ct) =>
            await StartAuthorizedAsync(context, elevations, HostElevationCapability.CertificateRenew, $"certificates/{id:D}/renew", key => manager.RenewAsync(id, key, Actor(context), ct)));
        group.MapDelete(CertificateApiRoutes.DeletePattern, async (Guid id, [Microsoft.AspNetCore.Mvc.FromBody] DeleteCertificateRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Certificate.ICertificateManager manager, CancellationToken ct) =>
            await StartAuthorizedAsync(context, elevations, HostElevationCapability.CertificateDelete, $"certificates/{id:D}/delete", key => manager.DeleteAsync(id, key, request, Actor(context), ct)));
        group.MapPost(CertificateApiRoutes.RevokePattern, async (Guid id, RevokeCertificateRequest request, HttpContext context, IHostElevationSessionStore elevations, RelaxKonOS.Server.Certificate.ICertificateManager manager, CancellationToken ct) =>
            await StartAuthorizedAsync(context, elevations, HostElevationCapability.CertificateRevoke, $"certificates/{id:D}/revoke", key => manager.RevokeAsync(id, key, request, Actor(context), ct)));
        group.MapGet(CertificateApiRoutes.OperationsPattern, async (Guid operationId, RelaxKonOS.Server.Certificate.CertificateOperationStore operations, CancellationToken ct) =>
            await operations.GetAsync(operationId, ct) is { } operation ? Results.Ok(operation) : Results.NotFound());
        group.MapGet(CertificateApiRoutes.ObserveOrderPattern, (Guid operationId, CertificateOperationStore operations, HttpContext context) =>
            ResidentScopedResponse.ExecuteAsync(context, operations.AdmitOrderObservationAsync, async (services, ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return await operations.ObserveOrderUnderLeaseAsync(operationId, services.GetRequiredService<IAcmeService>, ct) is { } observation
                    ? Results.Ok(observation) : Results.NotFound();
            }));
        group.MapGet(CertificateApiRoutes.VerifyOrderCertificatePattern, (Guid operationId, CertificateOperationStore operations, HttpContext context) =>
            ResidentScopedResponse.ExecuteAsync(context, operations.AdmitOrderObservationAsync, async (services, ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return await operations.VerifyOrderCertificateUnderLeaseAsync(operationId, services.GetRequiredService<IAcmeService>, ct) is { } proof
                    ? Results.Ok(proof) : Results.NotFound();
            }));
        group.MapGet(CertificateApiRoutes.PlanKestrelRecoveryPattern, (Guid operationId, CertificateOperationStore operations,
            HttpContext context, IHostElevationSessionStore elevations) =>
            ResidentScopedResponse.ExecuteAsync(context, operations.AdmitOrderObservationAsync, async (services, ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (await operations.GetAsync(operationId, ct) is not { } receipt) return Results.NotFound();
                if (!elevations.IsGranted(context.User, HostElevationCapability.CertificateReplaceServerHttps,
                    $"certificates/{receipt.CertificateId:D}/server-https"))
                    return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                        extensions: new Dictionary<string, object?> { ["problemCode"] = "elevation-required" });
                var subject = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                    ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(subject)) return Results.Forbid();
                return await operations.PlanKestrelRecoveryUnderLeaseAsync(operationId, subject,
                    services.GetRequiredService<IAcmeService>, ct) is { } plan ? Results.Ok(plan) : Results.NotFound();
            }));
        group.MapPost(CertificateApiRoutes.CancelOperationPattern, async (Guid operationId, RelaxKonOS.Server.Certificate.CertificateOperationStore operations, CancellationToken ct) =>
            await operations.CancelAsync(operationId, ct) is { } operation ? Results.Ok(operation) : Results.NotFound());
        return app;
    }

    private static Task<IResult> StartAuthorizedAsync(HttpContext context, IHostElevationSessionStore elevations,
        HostElevationCapability capability, string target, Func<string, Task<CertificateOperationDto>> start) =>
        elevations.IsGranted(context.User, capability, target) ? StartAsync(context, start) : Task.FromResult<IResult>(
            Results.Problem(statusCode: StatusCodes.Status403Forbidden, type: "https://relaxkonos.app/problems/elevation-required",
                extensions: new Dictionary<string, object?> { ["problemCode"] = "elevation-required" }));

    private static async Task<IResult> StartAsync(HttpContext context, Func<string, Task<CertificateOperationDto>> start)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            return Results.BadRequest(new { problemCode = "certificate.idempotency_key_required" });
        var operation = await start(key);
        if (operation.OperationId == Guid.Empty)
        {
            var status = operation.ProblemCode.EndsWith("elevation_required", StringComparison.Ordinal)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status409Conflict;
            return Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["problemCode"] = operation.ProblemCode });
        }
        return new ResidentOperationResult(ServerModules.CertificatesId, Results.Accepted($"{CertificateApiRoutes.Certificates}/operations/{operation.OperationId}", operation));
    }

    private static string? Actor(HttpContext context) => context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Name)?.Value
        ?? context.User.Identity?.Name;
}
