using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Identity;

public sealed partial class AliasPasswordService
{
    public const int MinimumIterations = 220_000;
    private readonly PasswordHasher<AliasCredential> hasher = new(Options.Create(new PasswordHasherOptions
    { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3, IterationCount = MinimumIterations }));
    private readonly AliasCredential dummy = new();
    private readonly string dummyHash;
    public AliasPasswordService() => dummyHash = hasher.HashPassword(dummy, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
        { "root", "admin", "administrator", "system", "guest", "anonymous", "support", "relaxkonos" };
    private static readonly HashSet<string> Weak = new(StringComparer.OrdinalIgnoreCase)
        { "passwordpassword", "123456789012345", "1234567890123456", "qwertyuiopasdfgh", "letmeinletmeinletmein", "correct horse battery staple" };
    [GeneratedRegex("\\A[a-z][a-z0-9._-]{2,31}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();
    public static bool ValidAlias(string? alias) => alias is not null && AliasPattern().IsMatch(alias) && !Reserved.Contains(alias);
    public static bool ValidInput(string? password) => password is not null && password.Length > 0 && password.Length <= 1024
        && Encoding.UTF8.GetByteCount(password) <= 1024 && !password.Contains('\0');
    public static bool ValidNewPassword(string? password)
    {
        if (!ValidInput(password) || Weak.Contains(password!)) return false;
        var remaining = password.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != System.Buffers.OperationStatus.Done) return false;
            remaining = remaining[consumed..]; count++;
        }
        return count is >= 15 and <= 128 && password!.EnumerateRunes().Distinct().Count() > 1;
    }
    public string Hash(AliasCredential credential, string password)
    {
        if (!ValidNewPassword(password)) throw new AliasAuthenticationException(400, "invalid-input");
        var hash = hasher.HashPassword(credential, password);
        if (hasher.VerifyHashedPassword(credential, hash, password) == PasswordVerificationResult.Failed)
            throw new AliasAuthenticationException(503, "authentication-unavailable");
        return hash;
    }
    public PasswordVerificationResult Verify(AliasCredential credential, string password)
    {
        if (!ValidInput(password) || credential.PasswordHash is not { Length: <= 256 } hash) return PasswordVerificationResult.Failed;
        Span<byte> data = stackalloc byte[192];
        if (!Convert.TryFromBase64String(hash, data, out var size) || size != 61 || data[0] != 1
            || BinaryPrimitives.ReadUInt32BigEndian(data[1..5]) != 2
            || BinaryPrimitives.ReadUInt32BigEndian(data[5..9]) is < MinimumIterations or > 2_000_000
            || BinaryPrimitives.ReadUInt32BigEndian(data[9..13]) != 16) return PasswordVerificationResult.Failed;
        try { return hasher.VerifyHashedPassword(credential, hash, password); }
        catch (FormatException) { return PasswordVerificationResult.Failed; }
    }
    public void Dummy(string password) => hasher.VerifyHashedPassword(dummy, dummyHash, password);
}

public sealed class AliasAuthenticationException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Single-host authentication transactions: no password request queue. Held through final issuance.</summary>
public sealed class AuthenticationGate
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public IDisposable Enter()
    {
        if (!gate.Wait(0)) throw new AliasAuthenticationException(429, "login-rate-limited");
        return new Lease(gate);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
}
