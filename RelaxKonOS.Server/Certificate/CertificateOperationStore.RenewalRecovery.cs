using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Server.Certificate;

internal sealed record RenewalRecoveryBaseline(string Version, string Fingerprint, string[] HostNames, bool IsDefault);
internal sealed record RenewalRecoveryPlan(Guid OperationId, Guid CertificateId, string Actor, string Target,
    RenewalRecoveryBaseline Baseline, string IssuedFingerprint);

internal sealed partial class CertificateOperationStore
{
    internal async Task<RenewalRecoveryBaseline?> ReadRenewalBaselineAsync(StoredCertificate current, CancellationToken ct)
    {
        if (_kestrel is null || _deployments?.IsPersistent != true || current.Kind != CertificateKind.Acme
            || current.Status == CertificateStatus.Revoked || current.FingerprintSha256 is not { } storedFingerprint)
            return null;
        var selector = _kestrel.Snapshot(current.Id);
        if (!selector.Registered || selector.FingerprintSha256 is not { } fingerprint
            || !string.Equals(fingerprint, storedFingerprint.Replace(":", ""), StringComparison.OrdinalIgnoreCase)
            || !current.Domains.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(selector.HostNames))
            return null;
        if (_certificates is not FileCertificateStore material || !await material.MatchesMaterialIndexAsync(current, ct)) return null;

        try
        {
            var deployed = (await _deployments.ListKestrelAsync(ct)).SingleOrDefault(item => item.CertificateId == current.Id);
            return deployed is not null && deployed.CurrentVersion == current.Version
                && deployed.LastSuccessfulVersion == current.Version && deployed.IsDefault == selector.IsDefault
                ? new(current.Version, fingerprint, selector.HostNames.ToArray(), selector.IsDefault) : null;
        }
        catch (SqliteException error)
        {
            _logger.LogWarning(error, "Certificate recovery binding could not be read. CertificateId={CertificateId}", current.Id);
            return null;
        }
    }

    internal async Task<CertificateOrderRecoveryPlanDto?> PlanKestrelRecoveryUnderLeaseAsync(Guid operationId, string actor,
        Func<IAcmeService> resolveAcme, CancellationToken ct)
    {
        PersistedOperation? operation;
        await _gate.WaitAsync(ct);
        try { RequireReady(); operation = _operations.GetValueOrDefault(operationId); }
        finally { _gate.Release(); }
        if (operation is null) return null;
        var receipt = operation.ToDto();
        if (operation is not { Kind: "renew", State: CertificateOperationState.Failed, ProblemCode: InterruptedProblem,
                Challenges: null, Order: { } order, ProtectedRenewalBaseline: { } protectedBaseline } || _protection is null)
            return new(receipt, "baseline-unavailable");

        var protector = OrderKeyProtector(_protection, operationId, operation.CertificateId, order);
        RenewalRecoveryBaseline? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<RenewalRecoveryBaseline>(
                protector.CreateProtector("renewal-baseline").Unprotect(protectedBaseline), Json);
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        { return new(receipt, "baseline-unavailable"); }
        if (baseline is null) return new(receipt, "baseline-unavailable");

        return await UnderCertificateGateAsync(operation.CertificateId, async execution =>
        {
            if (!await MatchesActiveAsync(execution)) return new(receipt, "active-version-changed");
            var verified = await VerifyOrderCertificateUnderLeaseAsync(operationId, resolveAcme, execution);
            if (verified is not { Status: "verified", FingerprintSha256: { } issued })
                return new CertificateOrderRecoveryPlanDto(receipt, verified?.Status ?? "identity-unavailable");
            if (!await MatchesActiveAsync(execution)) return new(receipt, "active-version-changed");

            const string target = "kestrel";
            var plan = new RenewalRecoveryPlan(operationId, operation.CertificateId, actor, target, baseline, issued);
            var token = protector.CreateProtector("renewal-plan").Protect(JsonSerializer.Serialize(plan, Json));
            return new(receipt, "ready", target, baseline.Version, baseline.Fingerprint, issued, token);
        }, ct);

        async Task<bool> MatchesActiveAsync(CancellationToken token)
        {
            var current = await _certificates.GetAsync(operation.CertificateId, token);
            return current is not null && await ReadRenewalBaselineAsync(current, token) is { } observed
                && observed.Version == baseline.Version && observed.Fingerprint == baseline.Fingerprint
                && observed.IsDefault == baseline.IsDefault && observed.HostNames.SequenceEqual(baseline.HostNames);
        }
    }
}
