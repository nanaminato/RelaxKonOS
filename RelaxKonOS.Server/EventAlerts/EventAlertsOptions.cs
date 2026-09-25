namespace RelaxKonOS.Server.EventAlerts;

/// <summary>Bounded local storage policy for the Event & Alert Center.</summary>
public sealed class EventAlertsOptions
{
    public const string SectionName = "EventAlerts";
    public bool Enabled { get; set; } = true;
    public string DatabasePath { get; set; } = "data/event-alerts.db";
    public int EventRetentionDays { get; set; } = 90;
    public int ResolvedAlertRetentionDays { get; set; } = 365;
    public int MaximumPageSize { get; set; } = 100;
    public int MaximumEvidenceLength { get; set; } = 512;
    public int MaximumSuppressionHours { get; set; } = 168;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath) || Path.IsPathFullyQualified(DatabasePath)
            || EventRetentionDays < 90 || ResolvedAlertRetentionDays < 365 || MaximumPageSize is < 1 or > 200
            || MaximumEvidenceLength is < 1 or > 1024 || MaximumSuppressionHours is < 1 or > 720)
            throw new InvalidOperationException("EventAlerts configuration is invalid.");
    }
}
