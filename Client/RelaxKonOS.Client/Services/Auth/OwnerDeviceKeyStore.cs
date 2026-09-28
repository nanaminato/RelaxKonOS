using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>Private half of an enrolled owner-device P-256 key. It is never sent to the Server.</summary>
public sealed record OwnerDeviceKeyMaterial(
    string ServiceId,
    Guid DeviceId,
    string DeviceName,
    string Platform,
    string ClientVersion,
    byte[] PrivateKeyPkcs8)
{
    public string PublicKeySpki
    {
        get
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(PrivateKeyPkcs8, out _);
            return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
    }
}

public enum OwnerDeviceKeyStorageKind
{
    WindowsDpapi,
    MacKeychain,
    LinuxSecretService,
    LinuxPassphraseFile,
}

public sealed class OwnerDeviceKeyPassphraseRequiredException : InvalidOperationException
{
    public OwnerDeviceKeyPassphraseRequiredException()
        : base("This Linux device has no usable desktop keyring. Enter the owner-device key passphrase to unlock its encrypted key file.") { }
}

/// <summary>
/// Persists owner-device private keys. Linux uses Secret Service when present. Headless/minimal
/// desktops have an explicit passphrase-encrypted file fallback; the passphrase is deliberately
/// never persisted, so an unlocked desktop session is not silently equivalent to key possession.
/// </summary>
public interface IOwnerDeviceKeyStore
{
    Task<OwnerDeviceKeyMaterial?> LoadAsync(string serviceId, string? passphrase, CancellationToken ct = default);
    Task<OwnerDeviceKeyMaterial> CreateAsync(string serviceId, string deviceName, string platform, string clientVersion,
        string? passphrase, CancellationToken ct = default);
    Task SaveAsync(OwnerDeviceKeyMaterial material, string? passphrase, CancellationToken ct = default);
    Task RemoveAsync(string serviceId, string? passphrase, CancellationToken ct = default);
    OwnerDeviceKeyStorageKind GetStorageKind(string serviceId);
}

public sealed class OwnerDeviceKeyStore : IOwnerDeviceKeyStore
{
    private const int PassphraseIterations = 600_000;
    private static readonly byte[] DpapiEntropy = "RelaxKonOS.OwnerDeviceKey.v1"u8.ToArray();
    private static readonly IPlatformSecretStore MacKeychain = new MacKeychainStore(
        PlatformSecretSlot.MacKeychain("RelaxKonOS.Client.OwnerDeviceKey"));
    private static readonly IPlatformSecretStore LinuxSecretService = new LinuxSecretServiceStore(
        PlatformSecretSlot.LinuxSecret("com.relaxkonos.client.owner-device-key", "application", "RelaxKonOS.Client",
            "RelaxKonOS owner device key"));
    private readonly string windowsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS", "owner-device-keys.bin");
    private readonly string linuxPassphraseFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS", "owner-device-keys.encrypted.json");

    public async Task<OwnerDeviceKeyMaterial?> LoadAsync(string serviceId, string? passphrase, CancellationToken ct = default)
    {
        var keys = await LoadCollectionAsync(passphrase, ct);
        return keys.SingleOrDefault(key => string.Equals(key.ServiceId, serviceId, StringComparison.Ordinal));
    }

    public async Task<OwnerDeviceKeyMaterial> CreateAsync(string serviceId, string deviceName, string platform, string clientVersion,
        string? passphrase, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceId)) throw new ArgumentException("A server identity is required.", nameof(serviceId));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var material = new OwnerDeviceKeyMaterial(serviceId, Guid.Empty, deviceName.Trim(), platform.Trim().ToLowerInvariant(),
            clientVersion.Trim(), key.ExportPkcs8PrivateKey());
        await SaveAsync(material, passphrase, ct);
        return material;
    }

    public async Task SaveAsync(OwnerDeviceKeyMaterial material, string? passphrase, CancellationToken ct = default)
    {
        IReadOnlyList<OwnerDeviceKeyMaterial> existing;
        try { existing = await LoadCollectionAsync(passphrase, ct); }
        catch (OwnerDeviceKeyPassphraseRequiredException) when (OperatingSystem.IsLinux()
            && !File.Exists(linuxPassphraseFile) && !string.IsNullOrWhiteSpace(passphrase))
        {
            // First enrollment on a minimal/headless Linux desktop: the passphrase will protect
            // the new file, so there is no previous collection to unlock.
            existing = Array.Empty<OwnerDeviceKeyMaterial>();
        }
        var keys = existing.ToList();
        keys.RemoveAll(key => string.Equals(key.ServiceId, material.ServiceId, StringComparison.Ordinal));
        keys.Add(material);
        await SaveCollectionAsync(keys, passphrase, ct);
    }

    public async Task RemoveAsync(string serviceId, string? passphrase, CancellationToken ct = default)
    {
        // Deleting an enrolled key must be explicit; it is not used as a recovery mechanism.
        var keys = (await LoadCollectionAsync(passphrase, ct)).ToList();
        keys.RemoveAll(key => string.Equals(key.ServiceId, serviceId, StringComparison.Ordinal));
        await SaveCollectionAsync(keys, passphrase, ct);
    }

    public OwnerDeviceKeyStorageKind GetStorageKind(string serviceId)
    {
        if (OperatingSystem.IsWindows()) return OwnerDeviceKeyStorageKind.WindowsDpapi;
        if (OperatingSystem.IsMacOS()) return OwnerDeviceKeyStorageKind.MacKeychain;
        if (OperatingSystem.IsLinux())
            return File.Exists(linuxPassphraseFile) ? OwnerDeviceKeyStorageKind.LinuxPassphraseFile : OwnerDeviceKeyStorageKind.LinuxSecretService;
        throw new PlatformNotSupportedException("Owner device keys are supported on Windows, Linux, and macOS clients.");
    }

    private async Task<IReadOnlyList<OwnerDeviceKeyMaterial>> LoadCollectionAsync(string? passphrase, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? payload;
        if (OperatingSystem.IsWindows()) payload = await LoadWindowsAsync(ct);
        else if (OperatingSystem.IsMacOS())
        {
            if (!MacKeychain.TryRead(out payload)) throw new InvalidOperationException("macOS Keychain is unavailable.");
        }
        else if (OperatingSystem.IsLinux())
        {
            // Once the user chose the no-keyring fallback, keep honoring it even if a keyring
            // later appears. Silently moving it would weaken the deliberate passphrase boundary.
            if (File.Exists(linuxPassphraseFile)) payload = await LoadLinuxPassphraseFileAsync(passphrase, ct);
            else if (LinuxSecretService.TryRead(out payload)) { }
            else throw new OwnerDeviceKeyPassphraseRequiredException();
        }
        else throw new PlatformNotSupportedException("Owner device keys are unsupported on this platform.");

        return string.IsNullOrEmpty(payload)
            ? Array.Empty<OwnerDeviceKeyMaterial>()
            : JsonSerializer.Deserialize<IReadOnlyList<OwnerDeviceKeyMaterial>>(payload, RelaxKonOSJsonOptions.Default)
                ?? Array.Empty<OwnerDeviceKeyMaterial>();
    }

    private async Task SaveCollectionAsync(IReadOnlyList<OwnerDeviceKeyMaterial> keys, string? passphrase, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(keys, RelaxKonOSJsonOptions.Default);
        if (OperatingSystem.IsWindows()) { await SaveWindowsAsync(payload, ct); return; }
        if (OperatingSystem.IsMacOS())
        {
            if (!MacKeychain.TryWrite(payload)) throw new InvalidOperationException("macOS Keychain is unavailable.");
            return;
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Owner device keys are unsupported on this platform.");

        if (!File.Exists(linuxPassphraseFile) && LinuxSecretService.TryWrite(payload))
        {
            return;
        }
        await SaveLinuxPassphraseFileAsync(payload, passphrase, ct);
    }

    [SupportedOSPlatform("windows")]
    private async Task<string?> LoadWindowsAsync(CancellationToken ct)
    {
        if (!File.Exists(windowsFile)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(windowsFile, ct);
        var plain = ProtectedData.Unprotect(protectedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    [SupportedOSPlatform("windows")]
    private async Task SaveWindowsAsync(string payload, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(windowsFile)!);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(payload), DpapiEntropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(windowsFile, protectedBytes, ct);
    }

    [SupportedOSPlatform("linux")]
    private async Task<string> LoadLinuxPassphraseFileAsync(string? passphrase, CancellationToken ct)
    {
        var file = await File.ReadAllTextAsync(linuxPassphraseFile, ct);
        var encrypted = JsonSerializer.Deserialize<LinuxEncryptedKeyFile>(file, RelaxKonOSJsonOptions.Default)
            ?? throw new CryptographicException("The owner-device key file is invalid.");
        if (encrypted.Version != 1) throw new CryptographicException("The owner-device key file version is unsupported.");
        var password = RequirePassphrase(passphrase);
        var salt = Convert.FromBase64String(encrypted.Salt);
        var nonce = Convert.FromBase64String(encrypted.Nonce);
        var ciphertext = Convert.FromBase64String(encrypted.Ciphertext);
        var tag = Convert.FromBase64String(encrypted.Tag);
        var plain = new byte[ciphertext.Length];
        var derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, PassphraseIterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(derived, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task SaveLinuxPassphraseFileAsync(string payload, string? passphrase, CancellationToken ct)
    {
        var password = RequirePassphrase(passphrase);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(payload);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[16];
        var derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, PassphraseIterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using (var aes = new AesGcm(derived, tag.Length)) aes.Encrypt(nonce, plain, ciphertext, tag);
            var data = JsonSerializer.Serialize(new LinuxEncryptedKeyFile(1, Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)), RelaxKonOSJsonOptions.Default);
            var directory = Path.GetDirectoryName(linuxPassphraseFile)!;
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var temporary = linuxPassphraseFile + ".tmp";
            await File.WriteAllTextAsync(temporary, data, Encoding.UTF8, ct);
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, linuxPassphraseFile, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] RequirePassphrase(string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12)
            throw new OwnerDeviceKeyPassphraseRequiredException();
        return Encoding.UTF8.GetBytes(passphrase);
    }

    private sealed record LinuxEncryptedKeyFile(int Version, string Salt, string Nonce, string Ciphertext, string Tag);
}
