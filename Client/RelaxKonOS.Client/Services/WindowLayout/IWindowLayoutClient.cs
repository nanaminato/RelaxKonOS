using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.WindowLayout;

public interface IWindowLayoutClient
{
    Task<WorkspaceWindowLayoutDto> GetAsync(string serverUrl, string accessToken, Guid workspaceId, CancellationToken ct = default);
    Task<WorkspaceWindowLayoutDto> SaveAsync(string serverUrl, string accessToken, Guid workspaceId, WorkspaceWindowLayoutDto layouts, CancellationToken ct = default);
}
