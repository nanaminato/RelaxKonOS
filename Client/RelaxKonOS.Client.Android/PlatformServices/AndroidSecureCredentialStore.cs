using Android.Content;

namespace RelaxKonOS.Client.Android;

/// <summary>
/// Deliberately narrow Android credential boundary. M0 does not persist passwords or refresh tokens;
/// M1 will back this interface with Android Keystore-encrypted storage rather than file paths or DPAPI.
/// </summary>
public interface IAndroidSecureCredentialStore
{
    Task<string?> ReadPasswordAsync(string serverUrl, string identifier, CancellationToken cancellationToken = default);
    Task WritePasswordAsync(string serverUrl, string identifier, string password, CancellationToken cancellationToken = default);
    Task RemovePasswordAsync(string serverUrl, string identifier, CancellationToken cancellationToken = default);
}

public sealed class AndroidSecureCredentialStore(Context context) : IAndroidSecureCredentialStore
{
    public Task<string?> ReadPasswordAsync(string serverUrl, string identifier, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Android Keystore persistence is introduced with M1; M0 never stores credentials.");

    public Task WritePasswordAsync(string serverUrl, string identifier, string password, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Android Keystore persistence is introduced with M1; M0 never stores credentials.");

    public Task RemovePasswordAsync(string serverUrl, string identifier, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
