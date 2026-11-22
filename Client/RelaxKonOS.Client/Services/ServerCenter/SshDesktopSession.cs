using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.AppSDK;
using Renci.SshNet;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>The SSH desktop's in-memory connection identity. Each terminal opens its own SSH channel.</summary>
public sealed partial class SshDesktopSession(IServerCenterConnectionResolver connections, ShellSettings settings, string? preferencesDirectory = null)
    : ISshFileSession
{
    private readonly object _gate = new();
    private RelaxKonOS.Protocol.Workspace.WorkspacePreferencesDto? _previousPreferences;
    public string PreferencesDirectory { get; } = preferencesDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS", "ssh-desktop");
    public DesktopDevicePreferences DevicePreferences { get; } = new(Path.Combine(
        preferencesDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS", "ssh-desktop"), "device.json"));
    private string PreferencesPath => Path.Combine(PreferencesDirectory, "preferences.json");

    /// <summary>Isolated SFTP identity from an already verified SSH connection; never changes desktop preferences.</summary>
    public static SshDesktopSession ForFileBrowser(ServerCenterHostSession connection, string password)
    {
        var observation = connection.ObservedHostKey ?? throw new InvalidOperationException("SSH host key is unavailable.");
        return new(null!, null!)
        {
            Endpoint = ServerCenterSshEndpoint.Create(connection.Target.SshHost, connection.Target.SshPort, connection.Target.SshUserName),
            Password = password,
            HostKeyFingerprint = observation.Fingerprint
        };
    }

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

    internal (ServerCenterSshEndpoint Endpoint, string Password, string Fingerprint) GetFileConnection(
        ServerCenterSshEndpoint? expectedEndpoint)
    {
        lock (_gate)
        {
            if (Endpoint is not { } endpoint || Password is not { } password || HostKeyFingerprint is not { } fingerprint
                || (expectedEndpoint is not null && !ReferenceEquals(endpoint, expectedEndpoint)))
                throw new InvalidOperationException("SSH session ended.");
            return (endpoint, password, fingerprint);
        }
    }

    ISshFileConnection? ISshFileSession.Connection => Endpoint;

    private SftpClient OpenBoundFileClient(ISshFileConnection connection) =>
        connection is ServerCenterSshEndpoint endpoint
            ? OpenFileClient(endpoint)
            : throw new InvalidOperationException("SSH session ended.");

    internal SftpClient OpenFileClient(ServerCenterSshEndpoint? expectedEndpoint)
    {
        var (endpoint, password, fingerprint) = GetFileConnection(expectedEndpoint);
        var client = new SftpClient(endpoint.Host, endpoint.Port, endpoint.UserName, password);
        client.HostKeyReceived += (_, args) =>
            args.CanTrust = string.Equals(ServerHostTrustRules.Fingerprint(args.HostKey), fingerprint, StringComparison.Ordinal);
        try
        {
            client.Connect();
            _ = GetFileConnection(endpoint);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Task<(string Path, SshDirectoryItem[] Items)> ReadDirectoryAsync(
        ISshFileConnection endpoint, string path, bool resolvePath = false) => Task.Run(() =>
    {
        try
        {
            using var client = OpenBoundFileClient(endpoint);
            if (resolvePath)
            {
                client.ChangeDirectory(path);
                path = client.WorkingDirectory;
            }
            var items = client.ListDirectory(path).Where(file => file.Name is not ("." or ".."))
                .Select(file => new SshDirectoryItem(file.Name, file.FullName, file.IsDirectory,
                    file.IsSymbolicLink, file.Length, file.LastWriteTime)).ToArray();
            return (path, items);
        }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException error)
        {
            throw new SshDirectoryAccessDeniedException(error);
        }
    });

    public Task<bool> CreateDirectoryAsync(ISshFileConnection endpoint, string path) => Task.Run(() =>
    {
        using var client = OpenBoundFileClient(endpoint);
        if (client.Exists(path)) return false;
        client.CreateDirectory(path);
        return true;
    });

    public Task<bool> RenamePathAsync(ISshFileConnection endpoint, string path, string target) => Task.Run(() =>
    {
        using var client = OpenBoundFileClient(endpoint);
        if (client.Exists(target)) return false;
        client.RenameFile(path, target);
        return true;
    });

    public Task<bool> UploadFileAsync(ISshFileConnection endpoint, Stream input, string target,
        Action<ulong> progress) => Task.Run(() =>
    {
        using var client = OpenBoundFileClient(endpoint);
        if (client.Exists(target)) return false;
        client.UploadFile(input, target, canOverride: false, progress);
        return true;
    });

    public Task DownloadFileAsync(ISshFileConnection endpoint, string path, Stream output,
        Action<ulong> progress) => Task.Run(() =>
    {
        using var client = OpenBoundFileClient(endpoint);
        client.DownloadFile(path, output, progress);
    });

    public Task DeletePathsAsync(ISshFileConnection endpoint, IReadOnlyList<(string Path, bool Directory)> paths) => Task.Run(() =>
    {
        using var client = OpenBoundFileClient(endpoint);
        foreach (var (path, directory) in paths)
            Delete(path, directory);

        void Delete(string path, bool directory)
        {
            if (!directory) { client.DeleteFile(path); return; }
            foreach (var child in client.ListDirectory(path).Where(file => file.Name is not ("." or "..")))
                Delete(child.FullName, child.IsDirectory && !child.IsSymbolicLink);
            client.DeleteDirectory(path);
        }
    });

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
