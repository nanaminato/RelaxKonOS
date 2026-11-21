using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Terminal;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ExternalTerminalWorkspacePreferences(ExternalServerConnection connection,
    ClientPresentationPreferences preferences, ITerminalSettingsClient client) : ITerminalWorkspacePreferences
{
    public TerminalSettingsDto Appearance => connection.IsCurrent ? preferences.Terminal : TerminalSettingsDto.Default;
    public TerminalPaddingSettings Padding => connection.IsCurrent ? preferences.TerminalPadding : TerminalPaddingSettings.Default;

    public IDisposable Observe(Action changed) => new Observer(preferences, () =>
    {
        if (connection.IsCurrent) changed();
    });

    public Task LoadAsync() => connection.IsCurrent ? preferences.EnsureLoadedAsync() : Task.CompletedTask;

    public async Task SaveAsync(TerminalSettingsDto appearance)
    {
        if (!connection.IsCurrent || connection.Session is not
            { EffectiveBaseUrl: { } url, Tokens: { } tokens, CurrentWorkspace.Id: var workspace })
            return;
        using var lifetime = new CancellationTokenSource();
        using var invalidation = connection.ObserveInvalidation(lifetime.Cancel);
        lifetime.Token.ThrowIfCancellationRequested();
        await client.SaveAsync(url, tokens.AccessToken, workspace, appearance, lifetime.Token);
    }

    private sealed class Observer : IDisposable
    {
        private ClientPresentationPreferences? _preferences;
        private readonly EventHandler _changed;

        public Observer(ClientPresentationPreferences preferences, Action changed)
        {
            _preferences = preferences;
            _changed = (_, _) => changed();
            preferences.Changed += _changed;
            preferences.Activate();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _preferences, null) is not { } preferences) return;
            preferences.Changed -= _changed;
            preferences.Deactivate();
        }
    }
}
