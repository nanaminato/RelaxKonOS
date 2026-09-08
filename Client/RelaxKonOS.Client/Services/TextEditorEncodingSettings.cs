using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Client.Apps.Settings;
using RelaxKonOS.Client.Apps.TextEditor;
using RelaxKonOS.Client.Services.Auth;

namespace RelaxKonOS.Client.Services;

/// <summary>Workspace-backed default encodings for the built-in text editors.</summary>
public sealed class TextEditorEncodingSettings
{
    private readonly ShellSettings _settings;
    private readonly IWorkspaceSettingsService _client;
    private readonly IAuthSession _session;
    private readonly DefaultAppRegistry _defaultApps;

    public TextEditorEncodingSettings(
        ShellSettings settings,
        IWorkspaceSettingsService client,
        IAuthSession session,
        DefaultAppRegistry defaultApps)
    {
        _settings = settings;
        _client = client;
        _session = session;
        _defaultApps = defaultApps;
    }

    public string NotepadDefaultEncoding => _settings.NotepadDefaultEncoding;
    public string CodeEditorDefaultEncoding => _settings.CodeEditorDefaultEncoding;

    public Task SetNotepadDefaultEncodingAsync(string encoding) => SetAsync(encoding, isNotepad: true);
    public Task SetCodeEditorDefaultEncodingAsync(string encoding) => SetAsync(encoding, isNotepad: false);

    private async Task SetAsync(string encoding, bool isNotepad)
    {
        if (!TextFileEncodings.IsSupported(encoding)) return;
        if (isNotepad) _settings.NotepadDefaultEncoding = encoding;
        else _settings.CodeEditorDefaultEncoding = encoding;

        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
            return;

        try
        {
            await _client.SaveAsync(url, tokens.AccessToken, workspace.Id,
                _settings.ToPreferences(_defaultApps.Snapshot));
        }
        catch
        {
            // Keep the updated local default; a later preference update retries with the current value.
        }
    }
}
