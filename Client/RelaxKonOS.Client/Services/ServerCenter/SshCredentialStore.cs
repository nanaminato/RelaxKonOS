using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>已保存的 SSH 凭据种类。</summary>
public enum SshCredentialKind { Password, PrivateKey }

/// <summary>
/// 一条已保存的 SSH 凭据。密码、私钥文本与口令只在平台安全存储中存在，
/// 绝不进入日志、诊断导出或工作区同步；本记录只在本进程内短暂承载它们。
/// </summary>
public sealed record SshCredentialRecord(
    string Host,
    int Port,
    string UserName,
    SshCredentialKind Kind,
    string Secret,
    string? Passphrase,
    DateTimeOffset SavedAtUtc)
{
    /// <summary>与 SSH 端点相同的三元组键；同一宿主的不同 SSH 用户互不覆盖。</summary>
    public string IdentityKey => CredentialIdentity(Host, Port, UserName);

    public static string CredentialIdentity(string host, int port, string userName) =>
        $"{ServerHostTrustRules.EndpointKey(host, port)}\u001f{userName.Trim()}";

    /// <summary>还原为可用的认证材料。它只在本次连接的内存中存在。</summary>
    public ServerCenterSshCredential ToCredential() => Kind switch
    {
        SshCredentialKind.Password => new ServerCenterSshCredential.Password(Secret),
        SshCredentialKind.PrivateKey => new ServerCenterSshCredential.PrivateKey(Secret, Passphrase),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
    };

    /// <summary>由用户本次输入的认证材料生成待保存记录。只应在用户显式选择保存时调用。</summary>
    public static SshCredentialRecord From(
        ServerCenterSshEndpoint endpoint, ServerCenterSshCredential credential, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return credential switch
        {
            ServerCenterSshCredential.Password password => new SshCredentialRecord(
                endpoint.Host, endpoint.Port, endpoint.UserName,
                SshCredentialKind.Password, password.Secret, null, nowUtc),
            ServerCenterSshCredential.PrivateKey key => new SshCredentialRecord(
                endpoint.Host, endpoint.Port, endpoint.UserName,
                SshCredentialKind.PrivateKey, key.PrivateKeyText, key.Passphrase, nowUtc),
            _ => throw new ArgumentOutOfRangeException(nameof(credential))
        };
    }
}

/// <summary>SSH 凭据保存结果。安全存储不可用时绝不退化为明文文件。</summary>
public enum SshCredentialSaveResult
{
    Saved,

    /// <summary>平台安全存储不可用（例如 Linux 上缺少 Secret Service）。凭据没有被保存。</summary>
    SecureStorageUnavailable,

    /// <summary>写入失败（磁盘、权限或序列化错误）。</summary>
    WriteFailed
}

/// <summary>
/// 已保存 SSH 凭据的仓库。它使用**独立于 RelaxKonOS 登录凭据**的平台安全存储槽：
/// 两者的 service/schema 身份与加密熵都不同，因此不会互相读取，也不因用户名或密码看起来相同而复用记录。
/// </summary>
public interface ISshCredentialStore
{
    Task<IReadOnlyList<SshCredentialRecord>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>按 SSH 端点（含用户）查找已保存凭据。</summary>
    Task<SshCredentialRecord?> FindAsync(
        ServerCenterSshEndpoint endpoint, CancellationToken cancellationToken = default);

    /// <summary>保存或替换一条凭据。同一三元组只保留一条记录。</summary>
    Task<SshCredentialSaveResult> SaveAsync(
        SshCredentialRecord record, CancellationToken cancellationToken = default);

    /// <summary>删除一条凭据（用户显式「不再保存该宿主的 SSH 凭据」）。</summary>
    Task<bool> ForgetAsync(string host, int port, string userName, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 平台实现：Windows 使用 DPAPI（<see cref="DataProtectionScope.CurrentUser"/>），
/// macOS 使用 Keychain，Linux 使用 Secret Service。三者都不产生明文凭据文件。
/// </summary>
public sealed class SshCredentialStore : ISshCredentialStore
{
    private static readonly byte[] Entropy = "RelaxKonOS.ServerCenter.SshCredentials.v1"u8.ToArray();
    private static readonly IPlatformSecretStore MacKeychainStore =
        new MacKeychainStore(PlatformSecretSlot.MacKeychain("RelaxKonOS.Client.SshCredentials"));
    private static readonly IPlatformSecretStore LinuxSecretStore =
        new LinuxSecretServiceStore(PlatformSecretSlot.LinuxSecret(
            "com.relaxkonos.client.ssh-credentials", "application", "RelaxKonOS.Client.SshCredentials"));

    private readonly string _windowsFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SshCredentialStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RelaxKonOS",
            "servercenter");
        _windowsFilePath = Path.Combine(root, "ssh-credentials.bin");
    }

    public async Task<IReadOnlyList<SshCredentialRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SshCredentialRecord?> FindAsync(
        ServerCenterSshEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var key = SshCredentialRecord.CredentialIdentity(endpoint.Host, endpoint.Port, endpoint.UserName);
        var records = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return records.FirstOrDefault(r => string.Equals(r.IdentityKey, key, StringComparison.Ordinal));
    }

    public async Task<SshCredentialSaveResult> SaveAsync(
        SshCredentialRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!ServerHostTargetRules.IsValidEndpoint(record.Host, record.Port, record.UserName))
            throw new ArgumentException("An SSH host, port and user name are required.", nameof(record));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var remaining = records.Where(r => !string.Equals(r.IdentityKey, record.IdentityKey, StringComparison.Ordinal));
            return await WriteUnlockedAsync([.. remaining, record], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ForgetAsync(
        string host, int port, string userName, CancellationToken cancellationToken = default)
    {
        var key = SshCredentialRecord.CredentialIdentity(host, port, userName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var remaining = records.Where(r => !string.Equals(r.IdentityKey, key, StringComparison.Ordinal)).ToList();
            if (remaining.Count == records.Count) return false;
            await WriteUnlockedAsync(remaining, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            }
        }
        catch (IOException)
        {
            // A stale credential is harmless; the next explicit save replaces it.
        }
        catch (UnauthorizedAccessException)
        {
            // A stale credential is harmless; the next explicit save replaces it.
        }

        await Task.CompletedTask;
    }

    private async Task<IReadOnlyList<SshCredentialRecord>> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        string? payload;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                payload = await ReadWindowsAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (OperatingSystem.IsMacOS())
            {
                payload = MacKeychainStore.TryRead(out var macValue) ? macValue : null;
            }
            else if (OperatingSystem.IsLinux())
            {
                payload = LinuxSecretStore.TryRead(out var linuxValue) ? linuxValue : null;
            }
            else
            {
                return [];
            }
        }
        catch (CryptographicException)
        {
            // A payload this OS user can no longer decrypt is treated as "no saved SSH credential":
            // the user types the credential again rather than being handed a broken record.
            await ClearAsync(cancellationToken).ConfigureAwait(false);
            return [];
        }

        if (string.IsNullOrEmpty(payload)) return [];
        try
        {
            return JsonSerializer.Deserialize<SshCredentialCollection>(
                Convert.FromBase64String(payload), RelaxKonOSJsonOptions.Default)?.Records ?? [];
        }
        catch (FormatException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<SshCredentialSaveResult> WriteUnlockedAsync(
        IReadOnlyList<SshCredentialRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0) return await ClearAndReportAsync(cancellationToken).ConfigureAwait(false);

        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new SshCredentialCollection([.. records]), RelaxKonOSJsonOptions.Default));

        try
        {
            if (OperatingSystem.IsWindows())
            {
                await WriteWindowsAsync(payload, cancellationToken).ConfigureAwait(false);
                return SshCredentialSaveResult.Saved;
            }

            if (OperatingSystem.IsMacOS())
                return MacKeychainStore.TryWrite(payload)
                    ? SshCredentialSaveResult.Saved
                    : SshCredentialSaveResult.SecureStorageUnavailable;

            if (OperatingSystem.IsLinux())
                return LinuxSecretStore.TryWrite(payload)
                    ? SshCredentialSaveResult.Saved
                    : SshCredentialSaveResult.SecureStorageUnavailable;
        }
        catch (CryptographicException)
        {
            return SshCredentialSaveResult.WriteFailed;
        }
        catch (IOException)
        {
            return SshCredentialSaveResult.WriteFailed;
        }
        catch (UnauthorizedAccessException)
        {
            return SshCredentialSaveResult.WriteFailed;
        }

        return SshCredentialSaveResult.SecureStorageUnavailable;
    }

    private async Task<SshCredentialSaveResult> ClearAndReportAsync(CancellationToken cancellationToken)
    {
        await ClearAsync(cancellationToken).ConfigureAwait(false);
        return SshCredentialSaveResult.Saved;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task<string?> ReadWindowsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_windowsFilePath)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(_windowsFilePath, cancellationToken).ConfigureAwait(false);
        var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task WriteWindowsAsync(string payload, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_windowsFilePath)!);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(payload), Entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_windowsFilePath, protectedBytes, cancellationToken).ConfigureAwait(false);
    }

    private sealed record SshCredentialCollection(IReadOnlyList<SshCredentialRecord> Records);
}