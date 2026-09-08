using System.Security.Cryptography;
using System.Text.Json;

namespace RelaxKonOS.Client.Services.Developer;

/// <summary>Persists the local Developer Mode switch and its pairing secret.</summary>
public sealed class DeveloperModeService
{
    public const int BridgePort = 45321;
    private readonly string _path;
    private readonly object _gate = new();
    private DeveloperModeState _state;

    public DeveloperModeService()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS");
        _path = Path.Combine(root, "developer-mode.json");
        _state = Load(_path);
    }

    public event EventHandler? Changed;

    public bool IsEnabled
    {
        get { lock (_gate) return _state.Enabled; }
    }

    /// <summary>Secret used by local tools in the <c>X-RemoteOS-Dev-Token</c> request header.</summary>
    public string PairingToken
    {
        get { lock (_gate) return _state.PairingToken; }
    }

    public string Endpoint => $"http://127.0.0.1:{BridgePort}/api/developer/v1.0/";

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _state = _state with { Enabled = enabled, PairingToken = EnsureToken(_state.PairingToken) };
            Save(_path, _state);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RegeneratePairingToken()
    {
        lock (_gate)
        {
            _state = _state with { PairingToken = CreateToken() };
            Save(_path, _state);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static DeveloperModeState Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<DeveloperModeState>(File.ReadAllText(path)) is { } value
                    ? value with { PairingToken = EnsureToken(value.PairingToken) }
                    : new DeveloperModeState(false, CreateToken());
        }
        catch (JsonException) { }
        catch (IOException) { }

        return new DeveloperModeState(false, CreateToken());
    }

    private static void Save(string path, DeveloperModeState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string EnsureToken(string? token) => string.IsNullOrWhiteSpace(token) ? CreateToken() : token;

    private static string CreateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record DeveloperModeState(bool Enabled, string PairingToken);
}
