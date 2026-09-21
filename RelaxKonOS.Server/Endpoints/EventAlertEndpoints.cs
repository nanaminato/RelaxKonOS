using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.EventAlerts;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Server.EventAlerts;
using RelaxKonOS.Server.Observability;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>Authenticated query and controlled action API for durable operational alerts.</summary>
public static class EventAlertEndpoints
{
    public const string ReadPolicy = "EventsRead";
    public const string ManagePolicy = "EventsManage";
    public const string CriticalSuppressPolicy = "EventsCriticalSuppress";

    public static IEndpointRouteBuilder MapEventAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(EventAlertApiRoutes.Root).RequireAuthorization().WithTags("EventAlerts");
        group.MapGet("/events", async (int? pageSize, string? cursor, string? type, EventAlertSeverity? severity,
            OperationalEventSource? source, EventAlertStore store, CancellationToken ct) =>
                await HandleAsync(() => store.ListEventsAsync(pageSize ?? 50, cursor, type, severity, source, ct)))
            .RequireAuthorization(ReadPolicy);
        group.MapGet("/alerts", async (int? pageSize, string? cursor, OperationalAlertStatus? status,
            EventAlertSeverity? severity, EventAlertStore store, CancellationToken ct) =>
                await HandleAsync(() => store.ListAlertsAsync(pageSize ?? 50, cursor, status, severity, ct)))
            .RequireAuthorization(ReadPolicy);
        group.MapGet("/alerts/{alertId:guid}", async (Guid alertId, HttpContext http, EventAlertStore store,
            ISecurityAuditWriter audit, ICorrelationContextAccessor correlation, CancellationToken ct) =>
        {
            var detail = await store.GetDetailAsync(alertId, ct);
            if (detail is null) return Results.NotFound();
            if (!await AuditAsync(audit, correlation, "event-alerts.detail.read", http.User, alertId, ct)) return Problem(EventAlertProblemCodes.AuditUnavailable, 503);
            return Results.Ok(detail);
        }).RequireAuthorization(ReadPolicy);
        group.MapGet("/summary", (EventAlertStore store, CancellationToken ct) => store.GetSummaryAsync(ct)).RequireAuthorization(ReadPolicy);
        group.MapPost("/alerts/{alertId:guid}/acknowledgement", async (Guid alertId, AcknowledgeAlertRequest request,
            HttpContext http, EventAlertStore store, IObservabilitySanitizer sanitizer, ISecurityAuditWriter audit,
            ICorrelationContextAccessor correlation, CancellationToken ct) =>
        {
            if (!TryNote(request.Note, sanitizer, required: false, out var note)) return Problem(EventAlertProblemCodes.InvalidRequest, 400);
            if (!await AuditAsync(audit, correlation, "event-alerts.acknowledge", http.User, alertId, ct)) return Problem(EventAlertProblemCodes.AuditUnavailable, 503);
            return await MutationResultAsync(() => store.AcknowledgeAsync(alertId, Actor(http.User), note, ct));
        }).RequireAuthorization(ManagePolicy);
        group.MapPost("/alerts/{alertId:guid}/resolve", async (Guid alertId, ResolveAlertRequest request,
            HttpContext http, EventAlertStore store, IObservabilitySanitizer sanitizer, ISecurityAuditWriter audit,
            ICorrelationContextAccessor correlation, CancellationToken ct) =>
        {
            if (!TryNote(request.Reason, sanitizer, required: true, out var reason)) return Problem(EventAlertProblemCodes.InvalidRequest, 400);
            var detail = await store.GetDetailAsync(alertId, ct);
            if (detail is null) return Results.NotFound();
            if (!EventAlertCatalog.TryGet(detail.Alert.Type, out var definition) || !definition.AllowsManualResolution)
                return Problem(EventAlertProblemCodes.ManualResolutionNotAllowed, 409);
            if (!await AuditAsync(audit, correlation, "event-alerts.resolve", http.User, alertId, ct)) return Problem(EventAlertProblemCodes.AuditUnavailable, 503);
            return await MutationResultAsync(() => store.ResolveAsync(alertId, Actor(http.User), reason!, ct));
        }).RequireAuthorization(ManagePolicy);
        group.MapPost("/alerts/{alertId:guid}/suppression", async (Guid alertId, SuppressAlertRequest request,
            HttpContext http, EventAlertStore store, EventAlertsOptions options, IObservabilitySanitizer sanitizer,
            ISecurityAuditWriter audit, ICorrelationContextAccessor correlation, IAuthorizationService authorization, CancellationToken ct) =>
        {
            if (!TryNote(request.Reason, sanitizer, required: true, out var reason)
                || request.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1) || request.ExpiresAt > DateTimeOffset.UtcNow.AddHours(options.MaximumSuppressionHours))
                return Problem(EventAlertProblemCodes.SuppressionInvalid, 400);
            var detail = await store.GetDetailAsync(alertId, ct);
            if (detail is null) return Results.NotFound();
            if (detail.Alert.Severity == EventAlertSeverity.Critical && !(await authorization.AuthorizeAsync(http.User, null, CriticalSuppressPolicy)).Succeeded)
                return Results.Forbid();
            if (!await AuditAsync(audit, correlation, "event-alerts.suppress", http.User, alertId, ct)) return Problem(EventAlertProblemCodes.AuditUnavailable, 503);
            return await MutationResultAsync(() => store.SuppressAsync(alertId, Actor(http.User), reason!, request.ExpiresAt, ct));
        }).RequireAuthorization(ManagePolicy);
        group.MapDelete("/alerts/{alertId:guid}/suppression", async (Guid alertId, HttpContext http, EventAlertStore store,
            ISecurityAuditWriter audit, ICorrelationContextAccessor correlation, CancellationToken ct) =>
        {
            if (!await AuditAsync(audit, correlation, "event-alerts.unsuppress", http.User, alertId, ct)) return Problem(EventAlertProblemCodes.AuditUnavailable, 503);
            return await MutationResultAsync(() => store.RemoveSuppressionAsync(alertId, Actor(http.User), ct));
        }).RequireAuthorization(ManagePolicy);
        return app;
    }

    private static async Task<IResult> HandleAsync<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (ArgumentException exception) when (exception.Message == EventAlertProblemCodes.InvalidCursor) { return Problem(EventAlertProblemCodes.InvalidCursor, 400); }
        catch (SqliteException) { return Problem(EventAlertProblemCodes.StoreUnavailable, 503); }
    }
    private static async Task<IResult> MutationResultAsync(Func<Task<OperationalAlertDto?>> action)
    {
        try { return await action() is { } alert ? Results.Ok(alert) : Results.NotFound(); }
        catch (InvalidOperationException exception) when (exception.Message == EventAlertProblemCodes.InvalidTransition) { return Problem(EventAlertProblemCodes.InvalidTransition, 409); }
        catch (SqliteException) { return Problem(EventAlertProblemCodes.StoreUnavailable, 503); }
    }
    private static bool TryNote(string? input, IObservabilitySanitizer sanitizer, bool required, out string? note)
    {
        note = null;
        if (string.IsNullOrWhiteSpace(input)) return !required;
        if (input.Length is < 1 or > 512) return false;
        note = sanitizer.SanitizeSummary(input, 512);
        return note.Length > 0;
    }
    private static string Actor(ClaimsPrincipal user) => user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "authenticated-user";
    private static async Task<bool> AuditAsync(ISecurityAuditWriter audit, ICorrelationContextAccessor correlation, string action, ClaimsPrincipal user, Guid alertId, CancellationToken ct) =>
        await audit.TryWriteAsync(new SecurityAuditEvent(1400, "configuration.changed", ObservabilityOutcome.Succeeded, "event-alerts",
            correlation.Current?.CorrelationId ?? Guid.NewGuid(), DateTimeOffset.UtcNow, "server", action, ActorReference: Actor(user),
            ResourceType: "event-alert", ResourceReference: alertId.ToString("D")), ct);
    private static IResult Problem(string code, int status) => Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["problemCode"] = code });
}
