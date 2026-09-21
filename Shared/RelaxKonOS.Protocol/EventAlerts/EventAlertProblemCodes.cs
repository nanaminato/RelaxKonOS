namespace RelaxKonOS.Protocol.EventAlerts;

/// <summary>Stable, non-sensitive problem codes owned by the Event & Alert Center.</summary>
public static class EventAlertProblemCodes
{
    public const string InvalidRequest = "event-alerts.invalid_request";
    public const string AlertNotFound = "event-alerts.alert_not_found";
    public const string InvalidCursor = "event-alerts.invalid_cursor";
    public const string InvalidTransition = "event-alerts.invalid_transition";
    public const string ManualResolutionNotAllowed = "event-alerts.manual_resolution_not_allowed";
    public const string SuppressionInvalid = "event-alerts.suppression_invalid";
    public const string AuditUnavailable = "event-alerts.audit_unavailable";
    public const string StoreUnavailable = "event-alerts.store_unavailable";
    public const string SourceDegraded = "event-center.source_degraded";
}
