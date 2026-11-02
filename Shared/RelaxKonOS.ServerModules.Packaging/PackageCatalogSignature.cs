using System.Security.Cryptography;
using System.Text;

namespace RelaxKonOS.ModulePackaging;

public sealed class PackageCatalogVerificationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
public sealed record PackageCatalogKeys(IReadOnlyDictionary<string, string> Hashes, string[] RetiredIds);
public sealed record VerifiedPackageCatalogSignature(byte[] Payload, PackageCatalogKeys Keys);

/// <summary>Shared package-store signature/key-retirement primitive. Domain/schema context is fixed by the caller.</summary>
public static class PackageCatalogSignature
{
    public static PackageCatalogKeys CheckKeys(IReadOnlyDictionary<string, string> active,
        IReadOnlyDictionary<string, string> previous, IReadOnlyCollection<string> retiredIds)
    {
        if (active is not { Count: > 0 and <= 32 } || previous.Count > 256 || retiredIds.Count > 256)
            throw new PackageCatalogVerificationException("key-invalid");
        var hashes = new Dictionary<string, string>(previous, StringComparer.Ordinal);
        var retired = new HashSet<string>(retiredIds, StringComparer.Ordinal);
        foreach (var prior in previous.Keys)
            if (!active.ContainsKey(prior)) retired.Add(prior);
        foreach (var item in active)
        {
            if (!ModulePackageRules.Id(item.Key) || retired.Contains(item.Key) || item.Value is not { Length: > 0 and <= 512 })
                throw new PackageCatalogVerificationException("key-invalid");
            try
            {
                var bytes = Convert.FromBase64String(item.Value);
                using var crypto = ECDsa.Create();
                crypto.ImportSubjectPublicKeyInfo(bytes, out var consumed);
                if (consumed != bytes.Length || crypto.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                    throw new PackageCatalogVerificationException("key-invalid");
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (hashes.TryGetValue(item.Key, out var prior) && prior != hash)
                    throw new PackageCatalogVerificationException("key-id-reused");
                hashes[item.Key] = hash;
            }
            catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException)
            { throw new PackageCatalogVerificationException("key-invalid"); }
        }
        if (hashes.Count > 256 || retired.Count > 256) throw new PackageCatalogVerificationException("history-capacity");
        return new(hashes, retired.Order(StringComparer.Ordinal).ToArray());
    }

    public static VerifiedPackageCatalogSignature Verify(string keyId, string payloadBase64, string signatureBase64,
        IReadOnlyDictionary<string, string> active, IReadOnlyDictionary<string, string> previous,
        IReadOnlyCollection<string> retired, string signatureContext, int maximumPayloadBytes)
    {
        var keys = CheckKeys(active, previous, retired);
        byte[] payload, signature;
        try
        {
            if (!ModulePackageRules.Id(keyId) || payloadBase64 is not { Length: > 0 }
                || payloadBase64.Length > ((maximumPayloadBytes + 2L) / 3) * 4 || signatureBase64 is not { Length: 88 })
                throw new PackageCatalogVerificationException("signature-envelope-invalid");
            payload = Convert.FromBase64String(payloadBase64);
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException) { throw new PackageCatalogVerificationException("signature-envelope-invalid"); }
        if (payload.Length > maximumPayloadBytes || signature.Length != 64)
            throw new PackageCatalogVerificationException("signature-envelope-invalid");
        if (!active.TryGetValue(keyId, out var publicKey)) throw new PackageCatalogVerificationException("signature-invalid");
        using var crypto = ECDsa.Create();
        crypto.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        var domain = Encoding.UTF8.GetBytes(signatureContext);
        var signed = new byte[domain.Length + payload.Length];
        domain.CopyTo(signed, 0); payload.CopyTo(signed, domain.Length);
        try
        {
            if (!crypto.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new PackageCatalogVerificationException("signature-invalid");
        }
        catch (CryptographicException) { throw new PackageCatalogVerificationException("signature-invalid"); }
        return new(payload, keys);
    }
}
