using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.EventAlerts;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.EventAlerts;
using RelaxKonOS.Server.Startup;

internal static class EventAlertChecks
{
    internal static async Task VerifyReadEndpointsAsync(string root)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = root, Args = [] });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ProgressTestAuthentication>("test", _ => { });
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAuthorization(AuthenticationServiceRegistration.AddEventAlertPolicies);
        var sanitizer = new ObservabilitySanitizer(new ObservabilityOptions());
        var options = new EventAlertsOptions { DatabasePath = "event-alerts-http.db" };
        var store = new EventAlertStore(builder.Environment, options, sanitizer);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IObservabilitySanitizer>(sanitizer);
        builder.Services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        builder.Services.AddSingleton<ISecurityAuditWriter>(_ => throw new InvalidOperationException("An alert read invoked the mutation audit fixture."));
        await using var app = builder.Build();
        app.MapEventAlertEndpoints();
        await app.StartAsync();
        var resource = Guid.NewGuid();
        var publisher = new OperationalEventPublisher(store, sanitizer);
        await publisher.PublishAsync(new("http-failure", "deployment.operation_failed", resource, Guid.NewGuid(), "deployment.build_failed"));
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var anonymous = await client.GetAsync(EventAlertApiRoutes.Summary);
        TestAssert.Assert(anonymous.StatusCode == HttpStatusCode.Unauthorized, "An anonymous request read the alert summary.");
        client.DefaultRequestHeaders.Add("X-Test-Role", "observer");
        var summary = await client.GetFromJsonAsync<EventAlertSummaryDto>(EventAlertApiRoutes.Summary, RelaxKonOSJsonOptions.Default);
        var alerts = await client.GetFromJsonAsync<EventAlertPageDto<OperationalAlertDto>>(EventAlertApiRoutes.Alerts, RelaxKonOSJsonOptions.Default);
        TestAssert.Assert(summary is { OpenCount: 1 } && alerts?.Items is [{ Status: OperationalAlertStatus.Open, ProblemCode: "deployment.build_failed" }],
            "The observer did not receive the published alert from the desktop read routes.");
        using var denied = await client.PostAsJsonAsync(EventAlertApiRoutes.Alerts + "/" + alerts!.Items[0].AlertId + "/acknowledgement",
            new { note = "test" });
        TestAssert.Assert(denied.StatusCode == HttpStatusCode.Forbidden, "An observer could acknowledge an alert.");
        await app.StopAsync();
    }

    internal static async Task VerifyAppendProjectionAndRecoveryAsync(string root)
    {
        var options = new EventAlertsOptions { DatabasePath = "event-alerts-test.db" };
        options.Validate();
        var sanitizer = new ObservabilitySanitizer(new ObservabilityOptions { AuditHmacKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        var store = new EventAlertStore(new TestHostEnvironment(root), options, sanitizer);
        var publisher = new OperationalEventPublisher(store, sanitizer);
        var resourceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        await publisher.PublishAsync(new("deployment-failure-1", "deployment.operation_failed", resourceId, correlationId,
            "deployment.build_failed", Evidence: "token=should-not-be-visible"));
        var first = await store.ListAlertsAsync(10, null, null, null, CancellationToken.None);
        TestAssert.Assert(first.Items.Count == 1 && first.Items[0].Status == OperationalAlertStatus.Open,
            "A failure signal did not open its projected alert.");
        TestAssert.Assert(first.Items[0].Severity == EventAlertSeverity.Error, "Catalog default severity was not used.");
        var detail = await store.GetDetailAsync(first.Items[0].AlertId, CancellationToken.None) ?? throw new InvalidOperationException("Projected alert disappeared.");
        TestAssert.Assert(detail.Events[0].Evidence?.Contains("[redacted]", StringComparison.Ordinal) == true,
            "Operational event evidence bypassed the sanitizer.");

        var acknowledged = await store.AcknowledgeAsync(first.Items[0].AlertId, "test-actor", "Investigating", CancellationToken.None);
        TestAssert.Assert(acknowledged?.Status == OperationalAlertStatus.Acknowledged && acknowledged.AcknowledgedByReference?.StartsWith("hmac:v1:", StringComparison.Ordinal) == true,
            "Acknowledgement did not retain a safe actor reference.");
        await publisher.PublishAsync(new("deployment-failure-2", "deployment.operation_failed", resourceId, correlationId,
            "deployment.build_failed"));
        var repeated = await store.GetDetailAsync(first.Items[0].AlertId, CancellationToken.None) ?? throw new InvalidOperationException("Repeated alert disappeared.");
        TestAssert.Assert(repeated.Alert.Status == OperationalAlertStatus.Acknowledged && repeated.Alert.OccurrenceCount == 2,
            "Repeated failure created a new alert or cleared acknowledgement.");

        await publisher.PublishAsync(new("deployment-recovery-1", "deployment.operation_failed", resourceId, correlationId,
            "deployment.recovered", IsRecovery: true));
        var resolved = await store.GetDetailAsync(first.Items[0].AlertId, CancellationToken.None) ?? throw new InvalidOperationException("Resolved alert disappeared.");
        TestAssert.Assert(resolved.Alert.Status == OperationalAlertStatus.Resolved && resolved.Alert.ResolutionReason == "source-recovered",
            "A recovery signal did not resolve the matching open alert.");
        var events = await store.ListEventsAsync(10, null, null, null, null, CancellationToken.None);
        TestAssert.Assert(events.Items.Count == 3, "The immutable event timeline lost a projected signal.");

        // A deployment recovery is a later operation, so it has a different operation ID but
        // must target the same application-level alert.
        var applicationId = Guid.NewGuid();
        await publisher.PublishAsync(new("deployment-application-failure", "deployment.operation_failed", applicationId, correlationId,
            "deployment.build_failed", OperationId: Guid.NewGuid()));
        var applicationAlert = (await store.ListAlertsAsync(10, null, null, null, CancellationToken.None)).Items
            .Single(item => item.RemediationTarget.ResourceId == applicationId);
        await publisher.PublishAsync(new("deployment-application-recovery", "deployment.operation_failed", applicationId, correlationId,
            "deployment.recovered", OperationId: Guid.NewGuid(), IsRecovery: true));
        var recoveredApplicationAlert = await store.GetDetailAsync(applicationAlert.AlertId, CancellationToken.None)
            ?? throw new InvalidOperationException("Application-level alert disappeared.");
        TestAssert.Assert(recoveredApplicationAlert.Alert.Status == OperationalAlertStatus.Resolved,
            "A later successful deployment did not resolve the failed application's alert.");

        // Suppression only hides a still-active condition. A verified recovery must close it
        // instead of letting the expiry task reopen a stale alert.
        var suppressedResourceId = Guid.NewGuid();
        await publisher.PublishAsync(new("suppressed-failure", "deployment.operation_failed", suppressedResourceId, correlationId,
            "deployment.build_failed"));
        var suppressedAlert = (await store.ListAlertsAsync(10, null, null, null, CancellationToken.None)).Items
            .Single(item => item.RemediationTarget.ResourceId == suppressedResourceId);
        await store.SuppressAsync(suppressedAlert.AlertId, "test-actor", "known maintenance", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        await publisher.PublishAsync(new("suppressed-recovery", "deployment.operation_failed", suppressedResourceId, correlationId,
            "deployment.recovered", IsRecovery: true));
        var recoveredSuppressedAlert = await store.GetDetailAsync(suppressedAlert.AlertId, CancellationToken.None)
            ?? throw new InvalidOperationException("Suppressed alert disappeared.");
        TestAssert.Assert(recoveredSuppressedAlert.Alert.Status == OperationalAlertStatus.Resolved
            && recoveredSuppressedAlert.Alert.ResolutionReason == "source-recovered",
            "A recovery signal did not close a suppressed alert.");

        var backupApplicationId = Guid.NewGuid();
        await publisher.PublishAsync(new("backup-definition-failed", "backup.definition_failed", backupApplicationId, Guid.NewGuid(),
            "backup-recovery.store_unavailable", OperationId: Guid.NewGuid()));
        var backupAlert = (await store.ListAlertsAsync(20, null, null, null, CancellationToken.None)).Items
            .Single(item => item.Type == "backup.definition_failed" && item.RemediationTarget.ResourceId == backupApplicationId);
        await publisher.PublishAsync(new("backup-definition-recovered", "backup.definition_failed", backupApplicationId, Guid.NewGuid(),
            "backup-recovery.recovered", OperationId: Guid.NewGuid(), IsRecovery: true));
        var recoveredBackupAlert = await store.GetDetailAsync(backupAlert.AlertId, CancellationToken.None)
            ?? throw new InvalidOperationException("Backup alert disappeared.");
        TestAssert.Assert(recoveredBackupAlert.Alert.Status == OperationalAlertStatus.Resolved,
            "A verified backup did not resolve the application backup failure alert.");
    }
}
