using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Developer;

namespace RelaxKonOS.Client.Services.Developer;

/// <summary>Persists the local Developer Mode switch and its pairing secret.</summary>
public sealed class DeveloperModeService
{
    private readonly string _path;
    private readonly object _gate = new();
    private DeveloperModeState _state;

    public DeveloperModeService()
        : this(Path.Combine(ClientDataPaths.Root, "developer-mode.json"))
    {
#if DEBUG
        if (!IsEnabled && Environment.GetEnvironmentVariable("RELAXKONOS_DEV_ENABLE") == "1")
            SetEnabled(true);
#endif
    }

    /// <summary>Store-path seam. Rendering checks must be able to switch Developer Mode on without
    /// rewriting the developer's own <c>developer-mode.json</c>.</summary>
    internal DeveloperModeService(string storePath)
    {
        _path = storePath;
        _state = Load(_path);
    }

    public event EventHandler? Changed;

    public bool IsEnabled
    {
        get { lock (_gate) return _state.Enabled; }
    }

    /// <summary>Secret used by local tools in the <c>X-RelaxKonOS-Dev-Token</c> request header.</summary>
    public string PairingToken
    {
        get { lock (_gate) return _state.PairingToken; }
    }

    public int Port => DeveloperProfile.Port;
    public string Endpoint => DeveloperBridgeTarget.Endpoint(Port);

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
