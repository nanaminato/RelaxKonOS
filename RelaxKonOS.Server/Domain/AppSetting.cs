using RelaxKonOS.Protocol.AppSettings;

namespace RelaxKonOS.Server.Domain;

/// <summary>One application-private JSON configuration document. ScopeId identifies its user, workspace, or device owner.</summary>
public sealed class AppSetting
{
    public Guid UserId { get; set; }
    public AppSettingsScope Scope { get; set; }
    public Guid ScopeId { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}
