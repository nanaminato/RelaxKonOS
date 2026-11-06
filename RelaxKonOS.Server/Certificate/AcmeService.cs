using System.Security.Cryptography;
using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Certify.ACME.Anvil;
using Certify.ACME.Anvil.Acme;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Server.Certificate;

internal interface IAcmeService
{
    Task<CertificateMaterial> IssueAsync(Guid certificateId, IReadOnlyList<string> domains, CertificateChallengeType challengeType,
        CertificateKeyAlgorithm keyAlgorithm, string contactEmail, CancellationToken cancellationToken);
    Task RevokeAsync(X509Certificate2 certificate, string contactEmail, CancellationToken cancellationToken);
}

internal sealed record AcmeRenewalWindow(DateTimeOffset Start, DateTimeOffset End);

internal interface IAcmeRenewalInfoProvider
{
    Task<AcmeRenewalWindow?> GetRenewalWindowAsync(X509Certificate2 certificate, string contactEmail, CancellationToken cancellationToken);
    Task MarkReplacedAsync(X509Certificate2 certificate, string contactEmail, CancellationToken cancellationToken);
}

/// <summary>ACME v2 adapter. Anvil types are confined here so the manager and API remain SDK-independent.</summary>
internal sealed class AnvilAcmeService(FileHttp01ChallengeStore webRootChallenges, DirectHttp01ChallengeStore directChallenges, CertificateOptions options,
    CertificateMetadataRepository metadata, ILogger<AnvilAcmeService> logger,
    Func<Uri, IAcmeHttpClient>? createHttpClient = null) : IAcmeService, IAcmeRenewalInfoProvider
{
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    public async Task RevokeAsync(X509Certificate2 certificate, string contactEmail, CancellationToken cancellationToken)
    {
        try
        {
            var context = await LoadContextAsync(ValidateDirectoryUrl(options.DirectoryUrl), contactEmail, cancellationToken);
            await AcmeCallLifetime.AwaitAsync(() => context.RevokeCertificate(certificate.RawData, Certify.ACME.Anvil.Acme.Resource.RevocationReason.Unspecified, context.AccountKey), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (CertificateOperationException) { throw; }
        catch { throw new CertificateOperationException("certificate.revocation_failed"); }
    }
    public async Task MarkReplacedAsync(X509Certificate2 certificate, string contactEmail, CancellationToken cancellationToken)
    {
        try
        {
            var certificateId = CreateAriCertificateId(certificate);
            if (certificateId is null) return;
            var context = await LoadContextAsync(ValidateDirectoryUrl(options.DirectoryUrl), contactEmail, cancellationToken);
            await AcmeCallLifetime.AwaitAsync(() => context.UpdateRenewalInfo(certificateId, true), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* ARI update is advisory and must not roll back a successfully deployed replacement. */ }
    }
    public async Task<AcmeRenewalWindow?> GetRenewalWindowAsync(X509Certificate2 certificate, string contactEmail, CancellationToken cancellationToken)
    {
        try
        {
            var certificateId = CreateAriCertificateId(certificate);
            if (certificateId is null) return null;
            var context = await LoadContextAsync(ValidateDirectoryUrl(options.DirectoryUrl), contactEmail, cancellationToken);
            var info = await AcmeCallLifetime.AwaitAsync(() => context.GetRenewalInfo(certificateId), cancellationToken);
            return info?.SuggestedWindow is { Start: { } start, End: { } end }
                ? new AcmeRenewalWindow(start, end)
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; } // ARI is optional; the renewal worker applies the documented NotAfter fallback.
    }
    public async Task<CertificateMaterial> IssueAsync(Guid certificateId, IReadOnlyList<string> domains, CertificateChallengeType challengeType,
        CertificateKeyAlgorithm keyAlgorithm, string contactEmail, CancellationToken cancellationToken)
    {
        if (challengeType == CertificateChallengeType.Dns01)
            throw new CertificateOperationException("certificate.challenge_mode_not_available");
        IHttp01ChallengeStore challengesStore = challengeType == CertificateChallengeType.DirectHttp01 ? directChallenges : webRootChallenges;
        var directoryUri = ValidateDirectoryUrl(options.DirectoryUrl);
        logger.LogInformation("ACME issuance started. CertificateId={CertificateId} Directory={Directory} ChallengeType={ChallengeType} KeyAlgorithm={KeyAlgorithm} Domains={Domains}",
            certificateId, directoryUri, challengeType, keyAlgorithm, string.Join(',', domains));
        var acme = await LoadContextAsync(directoryUri, contactEmail, cancellationToken);
        logger.LogInformation("ACME account context loaded. CertificateId={CertificateId}", certificateId);
        var order = await AcmeCallLifetime.AwaitAsync(() => acme.NewOrder(domains.ToList()), cancellationToken);
        logger.LogInformation("ACME order created. CertificateId={CertificateId}", certificateId);
        var challenges = new List<IChallengeContext>();
        Exception? issuanceFailure = null;
        try
        {
            var authorizations = (await AcmeCallLifetime.AwaitAsync(() => order.Authorizations(), cancellationToken)).ToArray();
            logger.LogInformation("ACME order authorizations retrieved. CertificateId={CertificateId} AuthorizationCount={AuthorizationCount}",
                certificateId, authorizations.Length);
            foreach (var authorization in authorizations)
            {
                var challenge = await AcmeCallLifetime.AwaitAsync(() => authorization.Http(), cancellationToken);
                if (challenge is null) throw new CertificateOperationException("certificate.http01_not_offered");
                challenges.Add(challenge);
                await challengesStore.PutAsync(challenge.Token, challenge.KeyAuthz, cancellationToken);
                logger.LogInformation("ACME HTTP-01 challenge published. CertificateId={CertificateId} TokenLength={TokenLength}", certificateId, challenge.Token.Length);
            }
            foreach (var challenge in challenges)
            {
                await AcmeCallLifetime.AwaitAsync(() => challenge.Validate(), cancellationToken);
                logger.LogInformation("ACME HTTP-01 validation requested. CertificateId={CertificateId} TokenLength={TokenLength}", certificateId, challenge.Token.Length);
            }

            // Validation is asynchronous. Poll the order through Anvil instead of treating a
            // successful challenge POST as authorization success.
            var ready = false;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                var resource = await AcmeCallLifetime.AwaitAsync(() => order.Resource(), cancellationToken);
                logger.LogInformation("ACME order validation status. CertificateId={CertificateId} Attempt={Attempt} Status={Status}", certificateId, attempt + 1, resource.Status);
                if (resource.Status == Certify.ACME.Anvil.Acme.Resource.OrderStatus.Ready) { ready = true; break; }
                if (resource.Status == Certify.ACME.Anvil.Acme.Resource.OrderStatus.Invalid) throw new CertificateOperationException("certificate.validation_failed");
                // Anvil surfaces the CA's Retry-After response on the order context. Respect
                // it during validation polling, with a bounded fallback for CAs that omit it.
                var delay = order.RetryAfter is { } retryAfter
                    ? TimeSpan.FromSeconds(Math.Clamp(retryAfter, 1, 300))
                    : TimeSpan.FromSeconds(5);
                await Task.Delay(delay, cancellationToken);
            }
            if (!ready) throw new CertificateOperationException("certificate.validation_timeout");
            var certificateKey = keyAlgorithm == CertificateKeyAlgorithm.Rsa2048
                ? KeyFactory.NewKey(KeyAlgorithm.RS256, 2048)
                : KeyFactory.NewKey(KeyAlgorithm.ES256);
            logger.LogInformation("ACME certificate key created. CertificateId={CertificateId} KeyAlgorithm={KeyAlgorithm}", certificateId, keyAlgorithm);
            var certificate = await AcmeCallLifetime.AwaitAsync(() => order.Generate(new CsrInfo { CommonName = domains[0] }, certificateKey), cancellationToken);
            logger.LogInformation("ACME certificate issued. CertificateId={CertificateId}", certificateId);
            return new CertificateMaterial(certificateId, domains, challengeType, keyAlgorithm, contactEmail, certificate.ToPem(), certificateKey.ToPem(), DateTimeOffset.UtcNow);
        }
        catch (CertificateOperationException error) { issuanceFailure = error; throw; }
        catch (OperationCanceledException error) { issuanceFailure = error; throw; }
        catch (NullReferenceException exception)
        {
            issuanceFailure = exception;
            // Anvil 3.x can surface an incomplete account/directory response as a null reference.
            // Keep the implementation detail out of the API, but retain the stack for diagnosis.
            logger.LogError(exception, "The ACME client returned an incomplete response while issuing certificate {CertificateId} for {Domains}.",
                certificateId, string.Join(",", domains));
            throw new CertificateOperationException("certificate.acme_response_invalid");
        }
        catch (Exception exception)
        {
            issuanceFailure = exception;
            logger.LogError(exception, "ACME certificate issuance failed for certificate {CertificateId} and domains {Domains}.",
                certificateId, string.Join(",", domains));
            throw new CertificateOperationException("certificate.acme_request_failed");
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            foreach (var challenge in challenges)
            {
                try
                {
                    await challengesStore.RemoveAsync(challenge.Token, CancellationToken.None);
                    logger.LogInformation("ACME HTTP-01 challenge removed. CertificateId={CertificateId} TokenLength={TokenLength}", certificateId, challenge.Token.Length);
                }
                catch (Exception error)
                {
                    cleanupFailures.Add(error);
                    logger.LogError(error, "Unable to remove ACME HTTP-01 challenge. CertificateId={CertificateId} TokenLength={TokenLength}", certificateId, challenge.Token.Length);
                }
            }
            if (cleanupFailures.Count != 0)
            {
                if (issuanceFailure is not null) cleanupFailures.Insert(0, issuanceFailure);
                throw new CertificateChallengeCleanupException(cleanupFailures);
            }
        }
    }

    private async Task<AcmeContext> LoadContextAsync(Uri directoryUri, string contactEmail, CancellationToken cancellationToken)
    {
        await _accountGate.WaitAsync(cancellationToken);
        try
        {
        var accountKeyPath = AccountKeyPath(directoryUri);
        var accountDirectory = Path.GetDirectoryName(accountKeyPath)!;
        var acmeDirectory = Path.GetDirectoryName(accountDirectory)!;
        var storageRoot = Path.GetDirectoryName(acmeDirectory)!;
        CreatePrivateDirectory(storageRoot);
        CreatePrivateDirectory(acmeDirectory);
        CreatePrivateDirectory(accountDirectory);
        if (File.Exists(accountKeyPath))
        {
            logger.LogInformation("Loading existing ACME account key. Directory={Directory}", directoryUri);
            var key = KeyFactory.FromPem(await File.ReadAllTextAsync(accountKeyPath, cancellationToken));
            var existing = new AcmeContext(directoryUri, key, createHttpClient?.Invoke(directoryUri));
            await AcmeCallLifetime.AwaitAsync(() => existing.Account(), cancellationToken);
            var accountUri = await AcmeCallLifetime.AwaitAsync(() => existing.GetAccountUri(), cancellationToken);
            await metadata.UpsertAcmeAccountAsync(directoryUri, contactEmail, accountKeyPath, accountUri?.AbsoluteUri, cancellationToken);
            return existing;
        }

        var accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var created = new AcmeContext(directoryUri, accountKey, createHttpClient?.Invoke(directoryUri));
        logger.LogInformation("Creating ACME account. Directory={Directory}", directoryUri);
        await AcmeCallLifetime.AwaitAsync(() => created.NewAccount(contactEmail, true), cancellationToken);
        var temporary = accountKeyPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, accountKey.ToPem(), new UTF8Encoding(false), cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, accountKeyPath, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var createdAccountUri = await AcmeCallLifetime.AwaitAsync(() => created.GetAccountUri(), cancellationToken);
        await metadata.UpsertAcmeAccountAsync(directoryUri, contactEmail, accountKeyPath, createdAccountUri?.AbsoluteUri, cancellationToken);
        logger.LogInformation("ACME account created and stored. Directory={Directory} AccountUri={AccountUri}", directoryUri, createdAccountUri);
        return created;
        }
        finally { _accountGate.Release(); }
    }

    private string AccountKeyPath(Uri directoryUri)
    {
        var root = options.StorageRoot ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS", "certificates")
            : "/var/lib/relaxkonos/certificates");
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directoryUri.AbsoluteUri)))[..24].ToLowerInvariant();
        return Path.Combine(Path.GetFullPath(root), "acme", id, "account.key");
    }

    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static Uri ValidateDirectoryUrl(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new CertificateOperationException("certificate.acme_directory_invalid");
        return uri;
    }

    private static string? CreateAriCertificateId(X509Certificate2 certificate)
    {
        // RFC 9773: base64url(AKI key identifier) + "." + base64url(serial in network byte order).
        var aki = certificate.Extensions.Cast<X509Extension>().FirstOrDefault(extension => extension.Oid?.Value == "2.5.29.35");
        if (aki is null) return null;
        byte[] keyId;
        try
        {
            var reader = new AsnReader(aki.RawData, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            keyId = [];
            while (sequence.HasData)
            {
                var tag = sequence.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 0)
                    keyId = sequence.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));
                else sequence.ReadEncodedValue();
            }
            reader.ThrowIfNotEmpty();
        }
        catch (AsnContentException) { return null; }
        if (keyId.Length == 0) return null;
        var serial = certificate.GetSerialNumber();
        Array.Reverse(serial);
        return $"{Base64Url(keyId)}.{Base64Url(serial)}";
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class CertificateOperationException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
