using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>Loads and saves terminal appearance preferences in the active workspace.</summary>
public interface ITerminalSettingsClient
{
    Task<TerminalSettingsDto> GetAsync(string serverUrl, string accessToken, Guid workspaceId, CancellationToken ct = default);
    Task<TerminalSettingsDto> SaveAsync(string serverUrl, string accessToken, Guid workspaceId, TerminalSettingsDto settings, CancellationToken ct = default);
}
