using RelaxKonOS.Server.Observability;

namespace RelaxKonOS.Server.Startup;

internal static class ObservabilityServiceRegistration
{
    public static RelaxKonOS.Server.EventAlerts.EventAlertsOptions AddServerObservability(this WebApplicationBuilder builder, bool isDevelopment, bool includeEventAlerts = true)
    {
        // Critical privileged operations fail closed when their security audit cannot be persisted. A
        // Rider/dotnet-run development process has no installer-managed audit path, so give it a stable,
        // user-writable location beside the Debug output, including runtime logs for failed requests.
        // Production paths are installer-managed and unaffected by these development defaults.
        if (isDevelopment)
        {
            var developmentObservability = new Dictionary<string, string?>();
            if (string.IsNullOrWhiteSpace(builder.Configuration["Observability:AuditDatabasePath"]))
                developmentObservability["Observability:AuditDatabasePath"] = Path.Combine(AppContext.BaseDirectory, "data", "security-audit.db");
            if (string.IsNullOrWhiteSpace(builder.Configuration["Observability:LogDirectory"]))
                developmentObservability["Observability:LogDirectory"] = Path.Combine(AppContext.BaseDirectory, "data", "logs");
            builder.Configuration.AddInMemoryCollection(developmentObservability);
        }
        var observabilityOptions = builder.Configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        observabilityOptions.Validate(!isDevelopment);
        builder.Services.AddSingleton(observabilityOptions);
        builder.Services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        builder.Services.AddSingleton<IObservabilitySanitizer, ObservabilitySanitizer>();
        builder.Services.AddSingleton<IRuntimeLogSink, JsonRuntimeLogSink>();
        builder.Services.AddSingleton<IEventLogger, EventLogger>();
        builder.Services.AddSingleton<ISecurityAuditWriter, SecurityAuditWriter>();
        builder.Services.AddHostedService<ObservabilityStartupValidationService>();
        if (!includeEventAlerts) return new() { Enabled = false };
        var eventAlertsOptions = builder.Configuration.GetSection(RelaxKonOS.Server.EventAlerts.EventAlertsOptions.SectionName)
            .Get<RelaxKonOS.Server.EventAlerts.EventAlertsOptions>() ?? new RelaxKonOS.Server.EventAlerts.EventAlertsOptions();
        eventAlertsOptions.Validate();
        builder.Services.AddSingleton(eventAlertsOptions);
        builder.Services.AddSingleton<RelaxKonOS.Server.EventAlerts.EventAlertStore>();
        builder.Services.AddSingleton<RelaxKonOS.Server.EventAlerts.IOperationalEventPublisher, RelaxKonOS.Server.EventAlerts.OperationalEventPublisher>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Hubs.EventAlertNotificationHub>();
        if (eventAlertsOptions.Enabled)
            builder.Services.AddHostedService<RelaxKonOS.Server.EventAlerts.EventAlertRetentionService>();
        return eventAlertsOptions;
    }
}
