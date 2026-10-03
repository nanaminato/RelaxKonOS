using System.Text.Json;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>Device-local metadata only. Credentials belong to ISshCredentialStore.</summary>
public sealed class LoginTunnelStore
{
    private readonly string _path;
    public LoginTunnelStore(string? directory = null) => _path = Path.Combine(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS"), "login-tunnels.json");

    public IReadOnlyList<SshLoginTunnelProfile> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<SshLoginTunnelProfile>>(File.ReadAllText(_path)) ?? [])
                .Select(p => SshLoginTunnelProfile.Create(p.Host, p.Port, p.UserName, p.RemoteUrl)).ToArray();
        }
        catch (Exception e) when (e is JsonException or ArgumentException or IOException) { return []; }
    }

    public void Save(SshLoginTunnelProfile profile)
    {
        var profiles = Load().Where(p => p.ServiceId != profile.ServiceId).Prepend(profile).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(profiles));
        File.Move(_path + ".tmp", _path, true);
    }
}
