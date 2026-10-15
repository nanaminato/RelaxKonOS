using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>The SSH desktop's in-memory connection identity. Each terminal opens its own SSH channel.</summary>
public sealed class SshDesktopSession(IServerCenterConnectionResolver connections, ShellSettings settings, string? preferencesDirectory = null)
{
    private readonly object _gate = new();
    private RelaxKonOS.Protocol.Workspace.WorkspacePreferencesDto? _previousPreferences;
    public string PreferencesDirectory { get; } = preferencesDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS", "ssh-desktop");
    private string PreferencesPath => Path.Combine(PreferencesDirectory, "preferences.json");

    public void SavePreferences()
    {
        if (!IsConnected) return;
        var temporary = PreferencesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
        try
        {
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(settings.ToPreferences([])));
            File.Move(temporary, PreferencesPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void LoadPreferences()
    {
        _previousPreferences ??= settings.ToPreferences([]);
        var preferences = RelaxKonOS.Protocol.Workspace.WorkspacePreferencesDto.Default;
        try { preferences = System.Text.Json.JsonSerializer.Deserialize<RelaxKonOS.Protocol.Workspace.WorkspacePreferencesDto>(File.ReadAllText(PreferencesPath)) ?? preferences; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Text.Json.JsonException) { }
        settings.Apply(preferences);
        var wallpaper = Path.Combine(Path.GetDirectoryName(PreferencesPath)!, "wallpaper");
        if (preferences.WallpaperKey == "custom:ssh-local" && File.Exists(wallpaper))
        {
            try { settings.SetCustomWallpaper("custom:ssh-local", new Avalonia.Media.Imaging.Bitmap(wallpaper)); }
            catch (Exception error) when (error is IOException or ArgumentException) { }
        }
    }

    private long _connectionGeneration;
    public ServerCenterSshEndpoint? Endpoint { get; private set; }
    public string? Password { get; private set; }
    public string? HostKeyFingerprint { get; private set; }
    public bool IsConnected => Endpoint is not null;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    public async Task ConnectAsync(ServerHostTarget target, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long generation;
        lock (_gate) generation = ++_connectionGeneration;
        var endpoint = ServerCenterSshEndpoint.Create(target.SshHost, target.SshPort, target.SshUserName);
        await using var verification = await connections.ConnectAsync(
            target, new ServerCenterSshCredential.Password(password),
            await connections.PrepareHostKeyGuardAsync(target, cancellationToken), cancellationToken);
        var observed = verification.ObservedHostKey
            ?? throw new InvalidOperationException("SSH did not return a host key.");
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _connectionGeneration) throw new OperationCanceledException("SSH connection was superseded.");
            Endpoint = endpoint;
            Password = password;
            HostKeyFingerprint = observed.Fingerprint;
            LoadPreferences();
        }
        Connected?.Invoke(this, EventArgs.Empty);
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            _connectionGeneration++;
            if (Endpoint is null) return;
            Endpoint = null;
            Password = null;
            HostKeyFingerprint = null;
            if (_previousPreferences is { } previous) settings.Apply(previous);
            _previousPreferences = null;
        }
        Disconnected?.Invoke(this, EventArgs.Empty);
    }
}
