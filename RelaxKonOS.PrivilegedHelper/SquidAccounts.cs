using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal static partial class SquidOperations
{
    private const string AccountsPath = DirectoryPath + "/relaxkonos-users.json";
    private const string PasswordPath = DirectoryPath + "/relaxkonos.passwd";
    private const string AuthenticationHelper = "/usr/lib/squid/basic_ncsa_auth";
    private const UnixFileMode PrivateRead = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode ProxyRead = PrivateRead | UnixFileMode.GroupRead;
    private sealed record AccountSecret(string Username, bool Enabled, string PasswordHash);
    private static readonly object CryptGate = new();

    [SupportedOSPlatform("linux")]
    private static async Task<List<AccountSecret>> ReadAccountsAsync()
    {
        EnsurePlainProtectedPath(DirectoryPath);
        EnsurePlainProtectedPath(AccountsPath);
        EnsurePlainProtectedPath(PasswordPath);
        if (!File.Exists(AccountsPath)) return [];
        if (new FileInfo(AccountsPath).Length > 256 * 1024) throw new IOException();
        var accounts = JsonSerializer.Deserialize<List<AccountSecret>>(await File.ReadAllTextAsync(AccountsPath))
            ?? throw new IOException();
        if (accounts.Count > 64 || accounts.Select(x => x.Username).Distinct(StringComparer.Ordinal).Count() != accounts.Count
            || accounts.Any(x => !SquidAccountValidation.ValidUsername(x.Username) || x.PasswordHash is null || !PasswordHashPattern().IsMatch(x.PasswordHash)))
            throw new IOException();
        return accounts;
    }

    [SupportedOSPlatform("linux")]
    private static async Task<string?> CheckAuthenticationAsync(SquidConfiguration? configuration)
    {
        if (configuration is null || configuration.AccessMode == SquidAccessMode.IpWhitelist) return null;
        EnsurePlainProtectedPath(AuthenticationHelper);
        if (!File.Exists(AuthenticationHelper)) return "squid.authentication.helper_unavailable";
        var accounts = await ReadAccountsAsync();
        return accounts.Any(x => x.Enabled) && File.Exists(PasswordPath) ? null : "squid.authentication.no_enabled_accounts";
    }

    [SupportedOSPlatform("linux")]
    private static async Task<PrivilegedOperationResult> MutateAccountAsync(SquidAccountMutation? request)
    {
        if (!SquidAccountValidation.Valid(request)) return Failure("squid.accounts.invalid", PrivilegedProblemCode.InvalidRequest);
        var accounts = await ReadAccountsAsync();
        var index = accounts.FindIndex(x => x.Username == request!.Username);
        switch (request!.Action)
        {
            case SquidAccountAction.Create:
                if (index >= 0) return Failure("squid.accounts.exists", PrivilegedProblemCode.InvalidRequest);
                if (accounts.Count >= 64) return Failure("squid.accounts.limit", PrivilegedProblemCode.InvalidRequest);
                accounts.Add(new(request.Username, true, HashPassword(request.Password!)));
                break;
            case SquidAccountAction.SetPassword:
                if (index < 0) return Failure("squid.accounts.not_found", PrivilegedProblemCode.InvalidRequest);
                accounts[index] = accounts[index] with { PasswordHash = HashPassword(request.Password!) };
                break;
            case SquidAccountAction.SetEnabled:
                if (index < 0) return Failure("squid.accounts.not_found", PrivilegedProblemCode.InvalidRequest);
                accounts[index] = accounts[index] with { Enabled = request.Enabled!.Value };
                break;
            case SquidAccountAction.Delete:
                if (index < 0) return Failure("squid.accounts.not_found", PrivilegedProblemCode.InvalidRequest);
                accounts.RemoveAt(index);
                break;
            default: return Failure("squid.accounts.invalid", PrivilegedProblemCode.InvalidRequest);
        }
        var authActive = File.Exists(ConfigurationPath) && (await File.ReadAllTextAsync(ConfigurationPath))
            .Contains("acl rk_authenticated proxy_auth REQUIRED", StringComparison.Ordinal);
        if (authActive && !accounts.Any(x => x.Enabled)) return Failure("squid.accounts.last_enabled_required");
        return await PublishAccountsAsync(accounts.OrderBy(x => x.Username, StringComparer.Ordinal).ToList());
    }

    private static string HashPassword(string password)
    {
        // SHA-512 crypt is supported by the distribution's NCSA helper via libcrypt.
        // A random per-password salt and an explicit work factor are stored, never plaintext.
        var salt = "$6$rounds=100000$" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + "$";
        lock (CryptGate)
        {
            var pointer = crypt(password, salt);
            var hash = pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
            if (hash is null || !PasswordHashPattern().IsMatch(hash)) throw new InvalidOperationException("squid.accounts.hash_failed");
            return hash;
        }
    }

    [DllImport("libcrypt.so.1", SetLastError = true)]
    private static extern IntPtr crypt([MarshalAs(UnmanagedType.LPUTF8Str)] string password, [MarshalAs(UnmanagedType.LPUTF8Str)] string salt);

    [GeneratedRegex(@"\A\$6\$rounds=100000\$[a-f0-9]{16}\$[./0-9A-Za-z]{86}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordHashPattern();

    [SupportedOSPlatform("linux")]
    private static async Task<PrivilegedOperationResult> PublishAccountsAsync(List<AccountSecret> accounts)
    {
        var jsonStage = AccountsPath + "." + Guid.NewGuid().ToString("N");
        var passwordStage = PasswordPath + "." + Guid.NewGuid().ToString("N");
        var originalJson = File.Exists(AccountsPath) ? await File.ReadAllTextAsync(AccountsPath) : null;
        var originalPasswords = File.Exists(PasswordPath) ? await File.ReadAllTextAsync(PasswordPath) : null;
        var wasRunning = (await RunAsync(Systemctl, ["is-active", "--quiet", "squid.service"])).Success;
        try
        {
            await WriteAccountFileAsync(jsonStage, JsonSerializer.Serialize(accounts), false);
            await WriteAccountFileAsync(passwordStage, string.Concat(accounts.Where(x => x.Enabled).Select(x => x.Username + ":" + x.PasswordHash + "\n")), true);
            // Backups contain hashes and are root-only, including the NCSA backup.
            foreach (var (path, content) in new[] { (AccountsPath, originalJson), (PasswordPath, originalPasswords) })
            {
                var backup = path + ".previous";
                EnsurePlainProtectedPath(backup);
                if (content is not null)
                {
                    var stage = backup + "." + Guid.NewGuid().ToString("N");
                    try { await WriteAccountFileAsync(stage, content, false); File.Move(stage, backup, true); }
                    finally { File.Delete(stage); }
                }
                else File.Delete(backup);
            }
            await WriteNewAsync(PendingPath, "accounts: restore relaxkonos-users.json.previous and relaxkonos.passwd.previous (or remove absent originals), then verify and reload\n");
            File.Move(passwordStage, PasswordPath, true);
            File.Move(jsonStage, AccountsPath, true);
            var reload = wasRunning ? await RunAsync(Systemctl, ["reload", "squid.service"]) : new PrivilegedOperationResult(true);
            if (reload.Success) { File.Delete(PendingPath); return new(true); }
            await RestoreAccountFileAsync(AccountsPath, originalJson, false);
            await RestoreAccountFileAsync(PasswordPath, originalPasswords, true);
            var restored = (await RunAsync(Systemctl, ["reload", "squid.service"])).Success
                && (await RunAsync(Systemctl, ["is-active", "--quiet", "squid.service"])).Success;
            if (restored) File.Delete(PendingPath);
            return Failure(restored ? "squid.accounts.failed_rolled_back" : "squid.recovery.required");
        }
        finally { File.Delete(jsonStage); File.Delete(passwordStage); }
    }

    [SupportedOSPlatform("linux")]
    private static async Task RestoreAccountFileAsync(string path, string? content, bool proxyReadable)
    {
        if (content is null) { File.Delete(path); return; }
        var stage = path + ".restore-" + Guid.NewGuid().ToString("N");
        try { await WriteAccountFileAsync(stage, content, proxyReadable); File.Move(stage, path, true); }
        finally { File.Delete(stage); }
    }

    [SupportedOSPlatform("linux")]
    private static async Task WriteAccountFileAsync(string path, string content, bool proxyReadable)
    {
        await using (var stream = new FileStream(path, new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = PrivateRead }))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
            stream.Flush(flushToDisk: true);
        }
        if (proxyReadable)
        {
            var group = await RunAsync("/usr/bin/chgrp", ["proxy", path]);
            if (!group.Success) throw new IOException();
            File.SetUnixFileMode(path, ProxyRead);
        }
        else File.SetUnixFileMode(path, PrivateRead);
    }
}
