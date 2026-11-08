using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Server.Certificate;

internal sealed partial class CertificateOperationStore
{
    internal async Task<CertificateOrderVerificationDto?> VerifyOrderCertificateUnderLeaseAsync(Guid id,
        Func<IAcmeService> resolveAcme, CancellationToken ct)
    {
        PersistedOperation? operation;
        await _gate.WaitAsync(ct);
        try { RequireReady(); operation = _operations.GetValueOrDefault(id); }
        finally { _gate.Release(); }
        if (operation is null) return null;
        return (await ReadVerifiedOrderMaterialAsync(operation, resolveAcme, ct)).Proof;
    }

    private async Task<(CertificateOrderVerificationDto Proof, string? Pem, string? Key)> ReadVerifiedOrderMaterialAsync(
        PersistedOperation operation, Func<IAcmeService> resolveAcme, CancellationToken ct)
    {
        var receipt = operation.ToDto();
        if (operation is not { State: CertificateOperationState.Failed, ProblemCode: InterruptedProblem,
                Order: { } order, Challenges: null })
            return (new(receipt, "identity-unavailable"), null, null);
        if (_protection is null || operation.ProtectedOrderKey is not { } protectedKey)
            return (new(receipt, "key-unavailable"), null, null);

        var issued = await resolveAcme().ReadIssuedCertificateAsync(order, ct);
        if (issued.Status != "valid") return (new(receipt, issued.Status), null, null);
        if (issued is not { ChainPem: { Length: > 0 and <= 1048576 } pem, DnsNames: { Length: > 0 } names })
            return (new(receipt, "invalid-certificate"), null, null);

        string privateKey;
        try { privateKey = OrderKeyProtector(_protection, operation.OperationId, operation.CertificateId, order).Unprotect(protectedKey); }
        catch (CryptographicException) { return (new(receipt, "key-unavailable"), null, null); }
        var proof = VerifyChain(receipt, pem, names, privateKey);
        return proof.Status == "verified" ? (proof, pem, privateKey) : (proof, null, null);
    }

    private static CertificateOrderVerificationDto VerifyChain(CertificateOperationDto receipt, string pem, string[] names, string privateKey)
    {
        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPem(pem);
            if (certificates.Count is 0 or > 16) return new(receipt, "invalid-certificate");
            var leaf = certificates[0];
            var extension = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault();
            if (extension is null || extension.EnumerateIPAddresses().Any()
                || !names.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(extension.EnumerateDnsNames()))
                return new(receipt, "identity-mismatch");
            try
            {
                using var paired = X509Certificate2.CreateFromPem(leaf.ExportCertificatePem(), privateKey);
                if (!paired.HasPrivateKey) return new(receipt, "identity-mismatch");
            }
            catch (CryptographicException) { return new(receipt, "identity-mismatch"); }

            using var chain = new X509Chain();
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            // CA order provenance is checked by the authenticated download. Check the supplied links here
            // even when the CA omits its root. Deployment has its own trust and authorization checks.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            for (var index = 1; index < certificates.Count; index++) chain.ChainPolicy.ExtraStore.Add(certificates[index]);
            if (!chain.Build(leaf) || chain.ChainElements.Count < Math.Max(2, certificates.Count)
                || Enumerable.Range(0, certificates.Count).Any(index => !chain.ChainElements[index].Certificate.Equals(certificates[index])))
                return new(receipt, "invalid-certificate");
            return new(receipt, "verified", leaf.GetCertHashString(HashAlgorithmName.SHA256));
        }
        catch (Exception error) when (error is CryptographicException or InvalidOperationException)
        { return new(receipt, "invalid-certificate"); }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }
}
