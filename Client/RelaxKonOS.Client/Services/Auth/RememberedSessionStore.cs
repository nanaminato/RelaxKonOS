using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>Stores opted-in credentials in the platform's encrypted credential vault.</summary>
public interface IRememberedSessionStore
{
    Task<IReadOnlyList<SavedLoginProfile>> LoadAsync(CancellationToken ct = default);
    Task<RememberedProfileSaveResult> RemoveAsync(string serviceId, string identifier, CancellationToken ct = default);
    Task<RememberedProfileSaveResult> UpsertAsync(SavedLoginProfile profile, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>Outcome of saving a remembered connection. A failed credential-vault write must never fail a remote login.</summary>
public enum RememberedProfileSaveResult
{
    Saved,
    /// <summary>Linux metadata was saved, but the desktop Secret Service could not store the password.</summary>
    SecureStorageUnavailable,
    /// <summary>The platform credential vault was unavailable before any connection data could be persisted.</summary>
    CredentialStoreUnavailable,
    LocalStorageWriteFailed,
}

/// <summary>
/// A saved login addressed by its stable <c>(serviceId, identifier)</c> pair.
/// <para><see cref="ServiceId"/> is the canonical server URL for a direct login and the verified
/// installation id for a managed tunnel. A temporary loopback address is never persisted, so changing a
/// tunnel's local port cannot create a second record or orphan the credential.</para>
/// <para>Password, when opted into, only ever exists in encrypted OS credential storage.</para>
/// </summary>
public sealed record SavedLoginProfile(string ServiceId, string Identifier, string? Password, DateTimeOffset LastUsedAt)
{
    public bool HasPassword => !string.IsNullOrWhiteSpace(Password);

    public ServerServiceIdKind ServiceIdKind => ServerInstallationId.IsValid(ServiceId)
        ? ServerServiceIdKind.ManagedInstallation
        : ServerServiceIdKind.DirectUrl;

    /// <summary>Direct profiles may refill the address field; managed profiles must first resolve an SSH tunnel.</summary>
    public string? DirectServerUrl => ServiceIdKind == ServerServiceIdKind.DirectUrl ? ServiceId : null;

    /// <summary>The key shared by the login record and its credential-vault entry; a tunnel rebind never changes it.</summary>
    public string CredentialKey => ServerConnectionIdentityRules.CredentialKey(ServiceId, Identifier);

    /// <summary>
    /// Text the editable server picker shows for this profile. A direct profile shows its canonical URL;
    /// a managed profile has no address to show until its SSH tunnel is resolved, so it falls back to the
    /// verified installation id rather than rendering an empty row. Never exposes credentials.
    /// </summary>
    public string DisplayText => DirectServerUrl ?? ServiceId;

    public override string ToString() => DisplayText;

    public static bool SameProfile(string leftServiceId, string leftIdentifier, string rightServiceId, string rightIdentifier)
        => string.Equals(leftServiceId, rightServiceId, StringComparison.Ordinal)
           && string.Equals(leftIdentifier, rightIdentifier, StringComparison.Ordinal);
}

internal sealed record SavedLoginProfileCollection(IReadOnlyList<SavedLoginProfile> Profiles);

// Previous releases wrote this payload as one encrypted session. Keep this private shape only for a one-time migration.
internal sealed record LegacyRememberedSession(string ServerUrl, AuthTokens Tokens, UserDto User, WorkspaceDto Workspace,
    SessionDto Session, DeviceDto Device, DeviceRole AssignedRole, string? Password);

/// <summary>
/// Uses DPAPI on Windows, Keychain on macOS, and the Secret Service API on Linux.
/// No unencrypted credential file is created on any platform.
/// </summary>
public sealed class RememberedSessionStore : IRememberedSessionStore
{
    private static readonly byte[] Entropy = "RelaxKonOS.RememberedSession.v2"u8.ToArray();
    private static readonly IPlatformSecretStore MacKeychainStore =
        new MacKeychainStore(PlatformSecretSlot.MacKeychain("RelaxKonOS.Client.RememberedSession"));
    private static readonly IPlatformSecretStore LinuxSecretStore =
        new LinuxSecretServiceStore(PlatformSecretSlot.LinuxSecret(
            "com.relaxkonos.client.remembered-session", "application", "RelaxKonOS.Client",
            "RelaxKonOS saved connection"));

    private readonly string _windowsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS",
        "remembered-session.bin");
    private readonly string _linuxProfilesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RelaxKonOS",
        "remembered-connections.json");

    public async Task<IReadOnlyList<SavedLoginProfile>> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var payload = OperatingSystem.IsWindows()
                ? await LoadWindowsAsync(ct)
                : OperatingSystem.IsMacOS()
                    ? MacKeychainStore.TryRead(out var macValue) ? macValue : null
                    : OperatingSystem.IsLinux()
                        ? await LoadLinuxAsync(ct)
                        : null;
            return payload is null ? Array.Empty<SavedLoginProfile>() : Deserialize(payload);
        }
        catch (CryptographicException)
        {
            await ClearAsync(ct);
            return Array.Empty<SavedLoginProfile>();
        }
        catch (JsonException)
        {
            await ClearAsync(ct);
            return Array.Empty<SavedLoginProfile>();
        }
        catch (FormatException)
        {
            await ClearAsync(ct);
            return Array.Empty<SavedLoginProfile>();
        }
    }

    public async Task<RememberedProfileSaveResult> UpsertAsync(SavedLoginProfile profile, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var profiles = (await LoadAsync(ct)).ToList();
        profiles.RemoveAll(item => SavedLoginProfile.SameProfile(item.ServiceId, item.Identifier, profile.ServiceId, profile.Identifier));
        profiles.Add(profile with { ServiceId = profile.ServiceId.Trim(), Identifier = profile.Identifier.Trim() });
        return await SaveProfilesAsync(profiles, ct);
    }

    private async Task<RememberedProfileSaveResult> SaveProfilesAsync(List<SavedLoginProfile> profiles, CancellationToken ct)
    {
        var payload = Serialize(new SavedLoginProfileCollection(
            profiles.OrderByDescending(item => item.LastUsedAt).ToArray()));

        try
        {
            if (OperatingSystem.IsWindows())
            {
                await SaveWindowsAsync(payload, ct);
                return RememberedProfileSaveResult.Saved;
            }

            if (OperatingSystem.IsMacOS())
                return MacKeychainStore.TryWrite(payload)
                    ? RememberedProfileSaveResult.Saved
                    : RememberedProfileSaveResult.CredentialStoreUnavailable;

            if (OperatingSystem.IsLinux())
                return await SaveLinuxAsync(profiles, payload, ct)
                    ? RememberedProfileSaveResult.Saved
                    : RememberedProfileSaveResult.SecureStorageUnavailable;
        }
        catch (CryptographicException)
        {
            return RememberedProfileSaveResult.LocalStorageWriteFailed;
        }
        catch (IOException)
        {
            return RememberedProfileSaveResult.LocalStorageWriteFailed;
        }
        catch (UnauthorizedAccessException)
        {
            return RememberedProfileSaveResult.LocalStorageWriteFailed;
        }

        return RememberedProfileSaveResult.CredentialStoreUnavailable;
    }

    public async Task<RememberedProfileSaveResult> RemoveAsync(string serviceId, string identifier, CancellationToken ct = default)
    {
        var profiles = (await LoadAsync(ct)).ToList();
        profiles.RemoveAll(item => SavedLoginProfile.SameProfile(item.ServiceId, item.Identifier, serviceId, identifier));
        return await SaveProfilesAsync(profiles, ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(_windowsFilePath)) File.Delete(_windowsFilePath);
            }
            else if (OperatingSystem.IsMacOS())
            {
                MacKeychainStore.TryClear();
            }
            else if (OperatingSystem.IsLinux())
            {
                LinuxSecretStore.TryClear();
                if (File.Exists(_linuxProfilesPath)) File.Delete(_linuxProfilesPath);
            }
        }
        catch (IOException)
        {
            // A stale credential is harmless; the next successful login will replace it.
        }
        catch (UnauthorizedAccessException)
        {
            // A stale credential is harmless; the next successful login will replace it.
        }

        await Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private async Task<string?> LoadWindowsAsync(CancellationToken ct)
    {
        if (!File.Exists(_windowsFilePath)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(_windowsFilePath, ct);
        var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    [SupportedOSPlatform("windows")]
    private async Task SaveWindowsAsync(string payload, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_windowsFilePath)!);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(payload), Entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_windowsFilePath, protectedBytes, ct);
    }

    [SupportedOSPlatform("linux")]
    private async Task<string?> LoadLinuxAsync(CancellationToken ct)
    {
        SavedLoginProfileCollection metadata;
        if (File.Exists(_linuxProfilesPath))
        {
            await using var stream = File.OpenRead(_linuxProfilesPath);
            metadata = await JsonSerializer.DeserializeAsync<SavedLoginProfileCollection>(stream, RelaxKonOSJsonOptions.Default, ct)
                ?? new SavedLoginProfileCollection(Array.Empty<SavedLoginProfile>());
        }
        else
        {
            metadata = new SavedLoginProfileCollection(Array.Empty<SavedLoginProfile>());
        }

        // Password-bearing profiles remain exclusively in the desktop Secret Service.
        // Older releases stored the complete payload there, so merging also migrates them
        // into the new always-available connection metadata file on the next successful login.
        if (!LinuxSecretStore.TryRead(out var protectedPayload) || protectedPayload is null)
            return Serialize(metadata);

        var protectedProfiles = Deserialize(protectedPayload);
        var merged = metadata.Profiles.Select(profile =>
        {
            var secret = protectedProfiles.FirstOrDefault(candidate => SavedLoginProfile.SameProfile(
                candidate.ServiceId, candidate.Identifier, profile.ServiceId, profile.Identifier));
            return profile with { Password = secret?.Password };
        }).ToList();

        foreach (var secret in protectedProfiles.Where(secret =>
                     merged.All(profile => !SavedLoginProfile.SameProfile(
                         profile.ServiceId, profile.Identifier, secret.ServiceId, secret.Identifier))))
            merged.Add(secret);

        return Serialize(new SavedLoginProfileCollection(
            merged.OrderByDescending(profile => profile.LastUsedAt).ToArray()));
    }

    [SupportedOSPlatform("linux")]
    private async Task<bool> SaveLinuxAsync(IReadOnlyList<SavedLoginProfile> profiles, string protectedPayload, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_linuxProfilesPath)!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var metadata = new SavedLoginProfileCollection(profiles
            .OrderByDescending(profile => profile.LastUsedAt)
            .Select(profile => profile with { Password = null })
            .ToArray());
        await using (var stream = new FileStream(_linuxProfilesPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, metadata, RelaxKonOSJsonOptions.Default, ct);
        File.SetUnixFileMode(_linuxProfilesPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        if (profiles.Any(profile => profile.HasPassword))
        {
            if (LinuxSecretStore.TryWrite(protectedPayload)) return true;
            // Do not leave an older password associated with newly updated metadata.
            LinuxSecretStore.TryClear();
            return false;
        }

        LinuxSecretStore.TryClear();
        return true;
    }

    private static string Serialize(SavedLoginProfileCollection profiles)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(profiles, RelaxKonOSJsonOptions.Default));

    private static IReadOnlyList<SavedLoginProfile> Deserialize(string payload)
    {
        var bytes = Convert.FromBase64String(payload);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.TryGetProperty("profiles", out _))
        {
            var profiles = JsonSerializer.Deserialize<SavedLoginProfileCollection>(bytes, RelaxKonOSJsonOptions.Default)?.Profiles
                ?? Array.Empty<SavedLoginProfile>();
            // A record without a stable identity cannot address any login; drop it instead of surfacing
            // an entry whose credential key would be empty.
            return profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.ServiceId)).ToArray();
        }

        var legacy = JsonSerializer.Deserialize<LegacyRememberedSession>(bytes, RelaxKonOSJsonOptions.Default);
        return legacy is null
            ? Array.Empty<SavedLoginProfile>()
            : [new SavedLoginProfile(CanonicalServiceId(legacy.ServerUrl), legacy.User.Username, legacy.Password, DateTimeOffset.UtcNow)];
    }

    /// <summary>A legacy record only carries a URL, so its canonical form becomes the stable service id.</summary>
    private static string CanonicalServiceId(string serverUrl)
    {
        try { return ServerConnectionIdentityRules.NormalizeServerUrl(serverUrl); }
        catch (ArgumentException) { return serverUrl.Trim(); }
    }
}
