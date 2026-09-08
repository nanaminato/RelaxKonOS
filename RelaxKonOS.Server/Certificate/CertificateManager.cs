using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Server.WebServer;

namespace RelaxKonOS.Server.Certificate;

internal interface ICertificateManager
{
    Task<IReadOnlyList<CertificateDto>> ListAsync(CancellationToken cancellationToken);
    Task<CertificateDto?> GetAsync(Guid certificateId, CancellationToken cancellationToken);
    Task<CertificatePreflightResultDto> PreflightAsync(CertificatePreflightRequest request, CancellationToken cancellationToken);
    Task<CertificateOperationDto> RequestAsync(string idempotencyKey, RequestCertificateRequest request, string? actor, CancellationToken cancellationToken);
    Task<CertificateOperationDto> CreateSelfSignedAsync(string idempotencyKey, CreateSelfSignedCertificateRequest request, string? actor, CancellationToken cancellationToken);
    Task<CertificateOperationDto> RenewAsync(Guid certificateId, string idempotencyKey, string? actor, CancellationToken cancellationToken);
    Task<CertificateOperationDto> DeployKestrelAsync(Guid certificateId, string idempotencyKey, string? actor, CancellationToken cancellationToken);
    Task<CertificateOperationDto> DeleteAsync(Guid certificateId, string idempotencyKey, DeleteCertificateRequest request, string? actor, CancellationToken cancellationToken);
    Task<CertificateOperationDto> RevokeAsync(Guid certificateId, string idempotencyKey, RevokeCertificateRequest request, string? actor, CancellationToken cancellationToken);
}

internal sealed class CertificateManager(ICertificateStore certificates, IAcmeService acme, IAcmeRenewalInfoProvider renewalInfo, CertificateOperationStore operations,
    KestrelCertificateRegistry kestrel, CertificateDeploymentRepository deployments, IServer server, IHostPrivilegeService privileges, IHostApplicationLifetime lifetime,
    ILogger<CertificateManager> logger) : ICertificateManager
{
    public async Task<IReadOnlyList<CertificateDto>> ListAsync(CancellationToken cancellationToken)
        => (await certificates.ListAsync(cancellationToken)).Select(ToDto).ToArray();

    public async Task<CertificateDto?> GetAsync(Guid certificateId, CancellationToken cancellationToken)
        => (await certificates.GetAsync(certificateId, cancellationToken)) is { } item ? ToDto(item) : null;

    public async Task<CertificatePreflightResultDto> PreflightAsync(CertificatePreflightRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.ChallengeType))
            return new CertificatePreflightResultDto(false, null, false, [], "certificate.challenge_mode_invalid");
        if (!TryNormalizeDomains(request.Domains, request.ChallengeType, out var domains, out var problem))
            return new CertificatePreflightResultDto(false, null, request.ChallengeType == CertificateChallengeType.DirectHttp01, [], problem);
        if (request.ChallengeType == CertificateChallengeType.Dns01)
            return new CertificatePreflightResultDto(false, null, false, [], "certificate.challenge_mode_not_available");

        var domainResults = new List<CertificateDomainPreflightDto>();
        foreach (var domain in domains)
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken);
                var ipv4 = addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork).Select(address => address.ToString()).Distinct().ToArray();
                var ipv6 = addresses.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6).Select(address => address.ToString()).Distinct().ToArray();
                domainResults.Add(new CertificateDomainPreflightDto(domain, ipv4, ipv6, addresses.Length == 0 ? "certificate.dns_no_records" : ""));
            }
            catch (SocketException) { domainResults.Add(new CertificateDomainPreflightDto(domain, [], [], "certificate.dns_lookup_failed")); }
        }

        bool? port80Available = request.ChallengeType == CertificateChallengeType.DirectHttp01
            ? privileges.IsAdministrator ? CanBindPort80() : null
            : null;
        var domainIssue = domainResults.FirstOrDefault(item => !string.IsNullOrEmpty(item.ProblemCode))?.ProblemCode;
        var issue = domainIssue ?? (request.ChallengeType == CertificateChallengeType.DirectHttp01 && !privileges.IsAdministrator ? "certificate.port80_elevation_required"
            : request.ChallengeType == CertificateChallengeType.DirectHttp01 && port80Available is false ? "certificate.port80_unavailable"
            : "");
        return new CertificatePreflightResultDto(string.IsNullOrEmpty(issue), port80Available, request.ChallengeType == CertificateChallengeType.DirectHttp01,
            domainResults, issue);
    }

    public async Task<CertificateOperationDto> RequestAsync(string idempotencyKey, RequestCertificateRequest request, string? actor, CancellationToken cancellationToken)
    {
        logger.LogInformation("Certificate issuance requested. Actor={Actor} ChallengeType={ChallengeType} KeyAlgorithm={KeyAlgorithm} DomainCount={DomainCount}",
            actor, request?.ChallengeType, request?.KeyAlgorithm, request?.Domains?.Count ?? 0);
        if (request is null)
            return Failure("issue", "certificate.request_invalid");
        if (!privileges.IsAdministrator)
            return Failure("issue", "certificate.admin_required");
        if (!request.AcceptedTerms)
            return Failure("issue", "certificate.terms_not_accepted");
        if (!Enum.IsDefined(request.ChallengeType))
            return Failure("issue", "certificate.challenge_mode_invalid");
        if (!Enum.IsDefined(request.KeyAlgorithm))
            return Failure("issue", "certificate.key_algorithm_invalid");
        if (!TryNormalizeDomains(request.Domains, request.ChallengeType, out var domains, out var problem))
            return Failure("issue", problem);
        if (!IsValidEmail(request.ContactEmail))
            return Failure("issue", "certificate.contact_invalid");
        var preflight = await PreflightAsync(new CertificatePreflightRequest(domains, request.ChallengeType), cancellationToken);
        if (!preflight.CanProceed)
        {
            logger.LogWarning("Certificate issuance preflight failed. ChallengeType={ChallengeType} Domains={Domains} ProblemCode={ProblemCode}",
                request.ChallengeType, string.Join(',', domains), preflight.ProblemCode);
            return Failure("issue", preflight.ProblemCode);
        }
        if (preflight.RequiresPublicReachabilityConfirmation && !request.PublicReachabilityConfirmed)
            return Failure("issue", "certificate.public_reachability_confirmation_required");

        var certificateId = Guid.NewGuid();
        logger.LogInformation("Certificate issuance accepted. CertificateId={CertificateId} ChallengeType={ChallengeType} KeyAlgorithm={KeyAlgorithm} Domains={Domains}",
            certificateId, request.ChallengeType, request.KeyAlgorithm, string.Join(',', domains));
        return await operations.StartAsync(idempotencyKey, certificateId, "issue", actor, async ct =>
        {
            logger.LogInformation("Certificate issuance ACME step started. CertificateId={CertificateId}", certificateId);
            var material = await acme.IssueAsync(certificateId, domains, request.ChallengeType, request.KeyAlgorithm, request.ContactEmail, ct);
            await certificates.SaveAsync(material, ct);
            logger.LogInformation("Certificate issuance material saved. CertificateId={CertificateId}", certificateId);
            return "";
        }, lifetime.ApplicationStopping);
    }

    public async Task<CertificateOperationDto> CreateSelfSignedAsync(string idempotencyKey, CreateSelfSignedCertificateRequest request, string? actor, CancellationToken cancellationToken)
    {
        if (request is null) return Failure("create-self-signed", "certificate.request_invalid");
        if (!privileges.IsAdministrator) return Failure("create-self-signed", "certificate.admin_required");
        if (!Enum.IsDefined(request.KeyAlgorithm)) return Failure("create-self-signed", "certificate.key_algorithm_invalid");
        if (request.ValidityDays is < 1 or > 825) return Failure("create-self-signed", "certificate.validity_days_invalid");
        if (!TryNormalizeSelfSignedIdentifiers(request.Domains, out var domains, out var problem))
            return Failure("create-self-signed", problem);

        var certificateId = Guid.NewGuid();
        return await operations.StartAsync(idempotencyKey, certificateId, "create-self-signed", actor, async ct =>
        {
            var material = CreateSelfSignedMaterial(certificateId, domains, request.KeyAlgorithm, request.ValidityDays);
            await certificates.SaveAsync(material, ct);
            return "";
        }, lifetime.ApplicationStopping);
    }

    public async Task<CertificateOperationDto> DeployKestrelAsync(Guid certificateId, string idempotencyKey, string? actor, CancellationToken cancellationToken)
    {
        if (!privileges.IsAdministrator) return Failure("deploy-kestrel", "certificate.deployment_elevation_required");
        var record = await certificates.GetAsync(certificateId, cancellationToken);
        if (record is null) return Failure("deploy-kestrel", "certificate.not_found");
        if (!HasHttpsBinding()) return Failure("deploy-kestrel", "certificate.kestrel_https_not_configured");
        return await operations.StartAsync(idempotencyKey, certificateId, "deploy-kestrel", actor, async ct =>
        {
            var certificate = await certificates.LoadCurrentAsync(certificateId, ct);
            if (certificate is null)
            {
                await deployments.RecordKestrelAsync(record, false, "certificate.material_unavailable", ct);
                return "certificate.material_unavailable";
            }
            if (!kestrel.Activate(certificateId, certificate, record.Domains))
            {
                certificate.Dispose();
                await deployments.RecordKestrelAsync(record, false, "certificate.kestrel_activation_failed", ct);
                return "certificate.kestrel_activation_failed";
            }
            await deployments.RecordKestrelAsync(record, true, null, ct);
            return "";
        }, lifetime.ApplicationStopping);
    }

    public async Task<CertificateOperationDto> RenewAsync(Guid certificateId, string idempotencyKey, string? actor, CancellationToken cancellationToken)
    {
        if (!privileges.IsAdministrator) return Failure("renew", "certificate.admin_required");
        var existing = await certificates.GetAsync(certificateId, cancellationToken);
        if (existing is null) return Failure("renew", "certificate.not_found");
        if (existing.Kind == CertificateKind.SelfSigned) return Failure("renew", "certificate.self_signed_not_renewable");
        if (existing.Status == CertificateStatus.Revoked) return Failure("renew", "certificate.revoked");
        if (string.IsNullOrWhiteSpace(existing.ContactEmail)) return Failure("renew", "certificate.contact_unavailable");
        return await operations.StartAsync(idempotencyKey, certificateId, "renew", actor, async ct =>
        {
            using var oldMaterial = await certificates.LoadCurrentAsync(certificateId, ct);
            var material = await acme.IssueAsync(certificateId, existing.Domains, existing.ChallengeType, existing.KeyAlgorithm, existing.ContactEmail, ct);
            await certificates.SaveAsync(material with { LastRenewalAt = DateTimeOffset.UtcNow }, ct);
            if (oldMaterial is not null)
                await renewalInfo.MarkReplacedAsync(oldMaterial, existing.ContactEmail, ct);
            if (kestrel.IsActive(certificateId))
            {
                var replacementRecord = await certificates.GetAsync(certificateId, ct);
                if (replacementRecord is null) return "certificate.material_unavailable";
                var replacement = await certificates.LoadCurrentAsync(certificateId, ct);
                if (replacement is null || !kestrel.Activate(certificateId, replacement, replacementRecord.Domains))
                {
                    replacement?.Dispose();
                    await deployments.RecordKestrelAsync(replacementRecord, false, "certificate.kestrel_activation_failed", ct);
                    return "certificate.kestrel_activation_failed";
                }
                await deployments.RecordKestrelAsync(replacementRecord, true, null, ct);
            }
            return "";
        }, lifetime.ApplicationStopping);
    }

    public async Task<CertificateOperationDto> DeleteAsync(Guid certificateId, string idempotencyKey, DeleteCertificateRequest request, string? actor, CancellationToken cancellationToken)
    {
        if (!privileges.IsAdministrator) return Failure("delete", "certificate.admin_required");
        if (!request.Confirmed) return Failure("delete", "certificate.confirmation_required");
        if (await certificates.GetAsync(certificateId, cancellationToken) is null) return Failure("delete", "certificate.not_found");
        return await operations.StartAsync(idempotencyKey, certificateId, "delete", actor, async ct =>
        {
            // A deleted certificate can never remain selected for new Kestrel connections.
            kestrel.Deactivate(certificateId);
            await deployments.RemoveKestrelAsync(certificateId, ct);
            await certificates.DeleteAsync(certificateId, ct);
            return "";
        }, lifetime.ApplicationStopping);
    }

    public async Task<CertificateOperationDto> RevokeAsync(Guid certificateId, string idempotencyKey, RevokeCertificateRequest request, string? actor, CancellationToken cancellationToken)
    {
        if (!privileges.IsAdministrator) return Failure("revoke", "certificate.admin_required");
        if (!request.Confirmed) return Failure("revoke", "certificate.confirmation_required");
        var existing = await certificates.GetAsync(certificateId, cancellationToken);
        if (existing is null) return Failure("revoke", "certificate.not_found");
        if (existing.Kind == CertificateKind.SelfSigned) return Failure("revoke", "certificate.self_signed_not_revocable");
        if (string.IsNullOrWhiteSpace(existing.ContactEmail)) return Failure("revoke", "certificate.contact_unavailable");
        return await operations.StartAsync(idempotencyKey, certificateId, "revoke", actor, async ct =>
        {
            using var material = await certificates.LoadCurrentAsync(certificateId, ct);
            if (material is null) return "certificate.material_unavailable";
            await acme.RevokeAsync(material, existing.ContactEmail, ct);
            kestrel.Deactivate(certificateId);
            await deployments.RemoveKestrelAsync(certificateId, ct);
            await certificates.UpdateStatusAsync(certificateId, CertificateStatus.Revoked, ct);
            return "";
        }, lifetime.ApplicationStopping);
    }

    private static CertificateOperationDto Failure(string kind, string problem)
        => new(Guid.Empty, null, kind, CertificateOperationState.Failed, "validation", problem, null, DateTimeOffset.UtcNow);

    private static CertificateDto ToDto(StoredCertificate item)
    {
        var now = DateTimeOffset.UtcNow;
        var status = item.NotAfter <= now ? CertificateStatus.Expired : item.Status == CertificateStatus.Pending ? CertificateStatus.Active : item.Status;
        var fallbackStart = item.NotAfter.AddDays(-30);
        return new CertificateDto(item.Id, item.PrimaryDomain, item.Domains, item.Issuer, item.SerialNumber, item.Thumbprint,
            item.NotBefore, item.NotAfter, status, item.ChallengeType, item.KeyAlgorithm, item.RenewalWindowStart ?? fallbackStart, item.RenewalWindowEnd ?? item.NotAfter, item.LastRenewalAt,
            item.LastRenewalProblemCode, item.CreatedAt, item.UpdatedAt, item.Kind, item.FingerprintSha256);
    }

    private static CertificateMaterial CreateSelfSignedMaterial(Guid certificateId, IReadOnlyList<string> domains, CertificateKeyAlgorithm keyAlgorithm, int validityDays)
    {
        var now = DateTimeOffset.UtcNow;
        var subject = new X500DistinguishedName($"CN={domains[0]}");
        using AsymmetricAlgorithm key = keyAlgorithm == CertificateKeyAlgorithm.Rsa2048
            ? RSA.Create(2048)
            : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = CreateRequest(subject, key, keyAlgorithm);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var domain in domains)
        {
            if (IPAddress.TryParse(domain, out var address)) san.AddIpAddress(address);
            else san.AddDnsName(domain);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(keyAlgorithm == CertificateKeyAlgorithm.Rsa2048
            ? X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(validityDays));
        var privateKeyPem = keyAlgorithm == CertificateKeyAlgorithm.Rsa2048
            ? certificate.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem()
            : certificate.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem();
        return new CertificateMaterial(certificateId, domains, CertificateChallengeType.Dns01, keyAlgorithm, null,
            certificate.ExportCertificatePem(), privateKeyPem, now, Kind: CertificateKind.SelfSigned);
    }

    private static CertificateRequest CreateRequest(X500DistinguishedName subject, AsymmetricAlgorithm key, CertificateKeyAlgorithm algorithm)
        => algorithm == CertificateKeyAlgorithm.Rsa2048
            ? new CertificateRequest(subject, (RSA)key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(subject, (ECDsa)key, HashAlgorithmName.SHA256);

    /// <summary>Self-signed certificates are useful on private networks, where an IP address is
    /// often the connection identity. IP literals are valid SAN values but must not be submitted
    /// to the DNS-only ACME validation path.</summary>
    private static bool TryNormalizeSelfSignedIdentifiers(IReadOnlyList<string>? requested, out string[] identifiers, out string problemCode)
    {
        if (requested is null || requested.Count is < 1 or > 100)
        {
            identifiers = [];
            problemCode = "certificate.domains_invalid";
            return false;
        }

        var ipAddresses = new List<string>();
        var names = new List<string>();
        foreach (var raw in requested)
        {
            var candidate = raw?.Trim();
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 253 || candidate.Any(char.IsControl))
            {
                identifiers = [];
                problemCode = "certificate.domains_invalid";
                return false;
            }
            if (IPAddress.TryParse(candidate, out var address))
            {
                ipAddresses.Add(address.ToString());
                continue;
            }
            if (!TryNormalizeDomains([candidate], CertificateChallengeType.Dns01, out var normalized, out problemCode))
            {
                identifiers = [];
                return false;
            }
            names.Add(normalized[0]);
        }
        identifiers = names.Concat(ipAddresses).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        problemCode = "";
        return true;
    }

    private static bool TryNormalizeDomains(IReadOnlyList<string>? requested, CertificateChallengeType challengeType,
        out string[] domains, out string problemCode)
    {
        domains = [];
        problemCode = "";
        if (requested is null || requested.Count is < 1 or > 100) { problemCode = "certificate.domains_invalid"; return false; }
        var mapping = new IdnMapping();
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in requested)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > 253 || raw.Any(char.IsControl)) { problemCode = "certificate.domains_invalid"; return false; }
            var wildcard = raw.StartsWith("*.", StringComparison.Ordinal);
            if (wildcard && challengeType != CertificateChallengeType.Dns01) { problemCode = "certificate.wildcard_requires_dns01"; return false; }
            var candidate = wildcard ? raw[2..] : raw;
            try { candidate = mapping.GetAscii(candidate.Trim().TrimEnd('.')).ToLowerInvariant(); }
            catch (ArgumentException) { problemCode = "certificate.domains_invalid"; return false; }
            if (Uri.CheckHostName(candidate) != UriHostNameType.Dns || candidate.Length > 253) { problemCode = "certificate.domains_invalid"; return false; }
            normalized.Add(wildcard ? $"*.{candidate}" : candidate);
        }
        domains = normalized.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        return true;
    }

    private static bool IsValidEmail(string value)
    {
        try { return new System.Net.Mail.MailAddress(value).Address.Equals(value, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool CanBindPort80()
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, 80);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool HasHttpsBinding()
        => server.Features.Get<IServerAddressesFeature>()?.Addresses.Any(address =>
            Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) == true;
}
