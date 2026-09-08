namespace RelaxKonOS.Protocol.Hubs;

/// <summary>Invalidation only. Values are retrieved through the authorized domain endpoint.</summary>
public sealed record WorkspaceSettingsChanged(Guid WorkspaceId, long Revision, long? PersistedRevision)
{
    public string SettingId => "workspace.preferences";
    public RelaxKonOS.Protocol.Settings.SettingsScope Scope => RelaxKonOS.Protocol.Settings.SettingsScope.Workspace;
}

public interface ISettingsChangesClient
{
    Task OnWorkspaceSettingsChanged(WorkspaceSettingsChanged change);
}

public static class SettingsChangesMethods
{
    public const string Subscribe = "Subscribe";
    public const string Changed = nameof(ISettingsChangesClient.OnWorkspaceSettingsChanged);
}
