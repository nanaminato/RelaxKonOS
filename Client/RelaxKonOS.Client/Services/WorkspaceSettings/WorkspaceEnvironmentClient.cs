using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.WorkspaceSettings;

public sealed record WorkspaceEnvironmentConnection(HostSettingsConnection Login, Guid WorkspaceId);

public interface IWorkspaceEnvironmentClient
{
    WorkspaceEnvironmentConnection CaptureConnection();
    bool IsCurrent(WorkspaceEnvironmentConnection connection);
    Task<WorkspaceEnvironmentSnapshot> ReadAsync(WorkspaceEnvironmentConnection connection, CancellationToken ct = default);
    Task<WorkspaceEnvironmentSnapshot> SaveAsync(WorkspaceEnvironmentConnection connection, WorkspaceEnvironmentUpdate update, CancellationToken ct = default);
}

/// <summary>Connection-bound writes with no authentication replay or redirects.</summary>
public sealed class WorkspaceEnvironmentClient(HttpClient http, IAuthSession session)
    : HostSettingsService(http, session), IWorkspaceEnvironmentClient
{
    public new WorkspaceEnvironmentConnection CaptureConnection() => new(base.CaptureConnection(),
        Session.CurrentWorkspace?.Id ?? throw new InvalidOperationException("settings.workspace_not_found"));
    public bool IsCurrent(WorkspaceEnvironmentConnection connection) => base.IsCurrent(connection.Login)
        && Session.CurrentWorkspace?.Id == connection.WorkspaceId;
    public Task<WorkspaceEnvironmentSnapshot> ReadAsync(WorkspaceEnvironmentConnection connection, CancellationToken ct = default)
        => SendWorkspaceAsync(connection, HttpMethod.Get, null, ct);
    public Task<WorkspaceEnvironmentSnapshot> SaveAsync(WorkspaceEnvironmentConnection connection, WorkspaceEnvironmentUpdate update, CancellationToken ct = default)
        => SendWorkspaceAsync(connection, HttpMethod.Put, update, ct);
    private async Task<WorkspaceEnvironmentSnapshot> SendWorkspaceAsync(WorkspaceEnvironmentConnection connection,
        HttpMethod method, WorkspaceEnvironmentUpdate? update, CancellationToken ct)
    {
        Check(connection);
        var result = await SendAsync<WorkspaceEnvironmentSnapshot>(connection.Login, method,
            WorkspaceApiRoutes.Environment.Replace("{id}", connection.WorkspaceId.ToString("D")), update, ct, () => IsCurrent(connection));
        Check(connection);
        if (result.WorkspaceId != connection.WorkspaceId) throw new InvalidOperationException("settings.connection_changed");
        return result;
    }
    private void Check(WorkspaceEnvironmentConnection connection)
    {
        if (!IsCurrent(connection)) throw new InvalidOperationException("settings.connection_changed");
    }
}
