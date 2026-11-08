using System.Security.Cryptography;
using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Certify.ACME.Anvil;
using Certify.ACME.Anvil.Acme;
using RelaxKonOS.Protocol.Certificates;
using Microsoft.Extensions.Logging;

namespace RelaxKonOS.Server.Certificate;

/// <summary>ACME v2 adapter. Anvil types are confined here so the manager and API remain SDK-independent.</summary>
public sealed class AnvilAcmeExecutor(AcmeExecutionPorts ports, ILogger logger) : IAcmeService, IAcmeRenewalInfoProvider, IDisposable
{
    private HttpClient? ownedAccountClient;

    public async Task RevokeAsync(X509Certificate2 certificate, string contactEmail, CancellationToken cancellationToken)
    {
        try
        {
            var context = await LoadContextAsync(ValidateDirectoryUrl(ports.DirectoryUrl), contactEmail, cancellationToken);
            await AcmeCallLifetime.AwaitAsync(() => context.RevokeCertificate(certificate.RawData,
                Certify.ACME.Anvil.Acme.Resource.RevocationReason.Unspecified, certificatePrivateKey: null), cancellationToken);
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
            var context = await LoadContextAsync(ValidateDirectoryUrl(ports.DirectoryUrl), contactEmail, cancellationToken);
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
            var context = await LoadContextAsync(ValidateDirectoryUrl(ports.DirectoryUrl), contactEmail, cancellationToken);
            var info = await AcmeCallLifetime.AwaitAsync(() => context.GetRenewalInfo(certificateId), cancellationToken);
            return info?.SuggestedWindow is { Start: { } start, End: { } end }
                ? new AcmeRenewalWindow(start, end)
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; } // ARI is optional; the renewal worker applies the documented NotAfter fallback.
    }
    public async Task<CertificateMaterial> IssueAsync(Guid certificateId, IReadOnlyList<string> domains, CertificateChallengeType challengeType,
        CertificateKeyAlgorithm keyAlgorithm, string contactEmail, CancellationToken cancellationToken,
        Func<AcmeOrderIdentity, Task>? recordOrder = null, Func<WebRootChallengeFile, Task>? recordChallenge = null,
        Func<string, Task>? recordCertificateKey = null)
    {
        if (challengeType == CertificateChallengeType.Dns01)
            throw new CertificateOperationException("certificate.challenge_mode_not_available");
        var direct = challengeType == CertificateChallengeType.DirectHttp01;
        var directoryUri = ValidateDirectoryUrl(ports.DirectoryUrl);
        logger.LogInformation("ACME issuance started. CertificateId={CertificateId} Directory={Directory} ChallengeType={ChallengeType} KeyAlgorithm={KeyAlgorithm} Domains={Domains}",
            certificateId, directoryUri, challengeType, keyAlgorithm, string.Join(',', domains));
        var acme = await LoadContextAsync(directoryUri, contactEmail, cancellationToken);
        logger.LogInformation("ACME account context loaded. CertificateId={CertificateId}", certificateId);
        var order = await ObserveOrderAsync(() => acme.NewOrder(domains.ToList()), cancellationToken, checkCompletionCancellation: false);
        var orderRecorded = false;
        if (recordOrder is not null && directoryUri.Query.Length == 0 && await acme.GetAccountUri() is { } account
            && IsSameOrigin(directoryUri, account) && IsSameOrigin(directoryUri, order.Location))
        {
            await recordOrder(new(directoryUri, account, acme.AccountKey.Thumbprint(), order.Location));
            orderRecorded = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("ACME order created. CertificateId={CertificateId}", certificateId);
        var challenges = new List<IChallengeContext>();
        var ownedFiles = new List<WebRootChallengeFile>();
        Exception? issuanceFailure = null;
        try
        {
            var authorizations = (await ObserveOrderAsync(() => order.Authorizations(), cancellationToken)).ToArray();
            logger.LogInformation("ACME order authorizations retrieved. CertificateId={CertificateId} AuthorizationCount={AuthorizationCount}",
                certificateId, authorizations.Length);
            foreach (var authorization in authorizations)
            {
                var resource = await ObserveOrderAsync(() => authorization.Resource(), cancellationToken);
                if (resource.Status == Certify.ACME.Anvil.Acme.Resource.AuthorizationStatus.Valid) continue;
                var challenge = await ObserveOrderAsync(() => authorization.Http(), cancellationToken);
                if (challenge is null) throw new CertificateOperationException("certificate.http01_not_offered");
                if (challengeType == CertificateChallengeType.WebRootHttp01 && recordChallenge is not null)
                {
                    ports.RequireVacantChallenge(challenge.Token);
                    var owned = WebRootChallengeFile.Describe(challenge.Token, challenge.KeyAuthz);
                    await recordChallenge(owned);
                    ownedFiles.Add(owned);
                }
                challenges.Add(challenge);
                await ports.PutChallenge(direct, challenge.Token, challenge.KeyAuthz, cancellationToken);
                logger.LogInformation("ACME HTTP-01 challenge published. CertificateId={CertificateId} TokenLength={TokenLength}", certificateId, challenge.Token.Length);
            }
            foreach (var challenge in challenges)
            {
                await ObserveOrderAsync(() => challenge.Validate(), cancellationToken);
                logger.LogInformation("ACME HTTP-01 validation requested. CertificateId={CertificateId} TokenLength={TokenLength}", certificateId, challenge.Token.Length);
            }

            // Validation is asynchronous. Poll the order through Anvil instead of treating a
            // successful challenge POST as authorization success.
            var ready = false;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                var resource = await ObserveOrderAsync(() => order.Resource(), cancellationToken);
                logger.LogInformation("ACME order validation status. CertificateId={CertificateId} Attempt={Attempt} Status={Status}", certificateId, attempt + 1, resource.Status.ToString());
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
            var privateKey = certificateKey.ToPem();
            if (orderRecorded && recordCertificateKey is not null) await recordCertificateKey(privateKey);
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation("ACME certificate key created. CertificateId={CertificateId} KeyAlgorithm={KeyAlgorithm}", certificateId, keyAlgorithm);
            var certificate = await ObserveOrderAsync(() => order.Generate(new CsrInfo { CommonName = domains[0] }, certificateKey), cancellationToken);
            logger.LogInformation("ACME certificate issued. CertificateId={CertificateId}", certificateId);
            return new CertificateMaterial(certificateId, domains, challengeType, keyAlgorithm, contactEmail, certificate.ToPem(), privateKey, DateTimeOffset.UtcNow);
        }
        catch (AcmeCheckpointException error) { issuanceFailure = error; throw; }
        catch (CertificateOperationException error) { issuanceFailure = error; throw; }
        catch (OperationCanceledException error) { issuanceFailure = error; throw; }
        catch (NullReferenceException exception)
        {
            issuanceFailure = exception;
            // Anvil 3.x can surface an incomplete account/directory response as a null reference.
            // Keep the implementation detail out of the API, but retain the stack for diagnosis.
            logger.LogError("The ACME client returned an incomplete response while issuing certificate {CertificateId} for {Domains}. ErrorType={ErrorType}",
                certificateId, string.Join(",", domains), exception.GetType().Name);
            throw new CertificateOperationException("certificate.acme_response_invalid");
        }
        catch (Exception exception)
        {
            issuanceFailure = exception;
            logger.LogError("ACME certificate issuance failed for certificate {CertificateId} and domains {Domains}. ErrorType={ErrorType}",
                certificateId, string.Join(",", domains), exception.GetType().Name);
            throw new CertificateOperationException("certificate.acme_request_failed");
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            foreach (var challenge in challenges)
            {
                try
                {
                    var owned = ownedFiles.FirstOrDefault(file => file.Token == challenge.Token);
                    await ports.RemoveChallenge(direct, challenge.Token, owned, CancellationToken.None);
                    logger.LogInformation("ACME HTTP-01 challenge removed. CertificateId={CertificateId} TokenLength={TokenLength}", certificateId, challenge.Token.Length);
                }
                catch (Exception error)
                {
                    cleanupFailures.Add(error);
                    logger.LogError("Unable to remove ACME HTTP-01 challenge. CertificateId={CertificateId} TokenLength={TokenLength} ErrorType={ErrorType}", certificateId, challenge.Token.Length, error.GetType().Name);
                }
            }
            if (cleanupFailures.Count != 0)
            {
                if (issuanceFailure is not null) cleanupFailures.Insert(0, issuanceFailure);
                throw new AcmeCleanupException(cleanupFailures.Any(failure => failure is CertificateOperationException { ProblemCode: "certificate.challenge_ownership_unconfirmed" }));
            }
        }
    }

    public async Task<string> ObserveOrderAsync(AcmeOrderIdentity identity, CancellationToken ct)
    {
        try
        {
            var directory = ValidateDirectoryUrl(ports.DirectoryUrl);
            if (directory != identity.Directory || !IsSameOrigin(directory, identity.Account) || !IsSameOrigin(directory, identity.Order))
                return "identity-unavailable";
            var key = KeyFactory.FromPem(await File.ReadAllTextAsync(AccountKeyPath(directory), ct));
            if (!string.Equals(key.Thumbprint(), identity.AccountThumbprint, StringComparison.Ordinal))
                return "identity-unavailable";

            // With the existing account URI Anvil can sign POST-as-GET directly. Restrict
            // this transport to directory/nonce reads and the exact original order request.
            using var http = new HttpClient(new OrderReadHandler(directory, identity.Order)) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RelaxKonOS/1.0");
            var context = new AcmeContext(directory, key, new AcmeHttpClient(directory, http), accountUri: identity.Account);
            var order = await AcmeCallLifetime.AwaitAsync(() => context.Order(identity.Order).Resource(), ct);
            return order.Status switch
            {
                Certify.ACME.Anvil.Acme.Resource.OrderStatus.Pending => "pending",
                Certify.ACME.Anvil.Acme.Resource.OrderStatus.Ready => "ready",
                Certify.ACME.Anvil.Acme.Resource.OrderStatus.Processing => "processing",
                Certify.ACME.Anvil.Acme.Resource.OrderStatus.Valid => "valid",
                Certify.ACME.Anvil.Acme.Resource.OrderStatus.Invalid => "invalid",
                _ => "unavailable"
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (FileNotFoundException) { return "identity-unavailable"; }
        catch (DirectoryNotFoundException) { return "identity-unavailable"; }
        catch (Exception error)
        {
            logger.LogWarning("ACME order observation unavailable. ErrorType={ErrorType}", error.GetType().Name);
            return "unavailable";
        }
    }

    private static bool IsSameOrigin(Uri directory, Uri? target) =>
        target is { IsAbsoluteUri: true, Scheme: "https", UserInfo.Length: 0, Fragment.Length: 0, Query.Length: 0 }
        && string.Equals(directory.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase) && directory.Port == target.Port;

    private sealed class OrderReadHandler(Uri directory, Uri order) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!IsSameOrigin(directory, request.RequestUri)
                || !(request.Method == HttpMethod.Head || request.Method == HttpMethod.Get && request.RequestUri == directory
                    || request.Method == HttpMethod.Post && request.RequestUri == order))
                throw new HttpRequestException("ACME order observation attempted a request outside its read-only authority.");
            return base.SendAsync(request, ct);
        }
    }

    private static async Task<T> ObserveOrderAsync<T>(Func<Task<T>> operation, CancellationToken ct, bool checkCompletionCancellation = true)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var result = await operation();
            if (checkCompletionCancellation) ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (HasUnknownOrderOutcome(error, ct))
        {
            throw new CertificateOperationException("certificate.operation_interrupted", error);
        }
    }

    private static bool HasUnknownOrderOutcome(Exception error, CancellationToken ct)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is HttpIOException or TimeoutException
                || cause is HttpRequestException request && (request.StatusCode is null || (int)request.StatusCode >= 500)
                || cause is OperationCanceledException && !ct.IsCancellationRequested)
                return true;
        }
        return false;
    }

    private async Task<AcmeContext> LoadContextAsync(Uri directoryUri, string contactEmail, CancellationToken cancellationToken)
    {
        await ports.AccountGate.WaitAsync(cancellationToken);
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
            var existing = new AcmeContext(directoryUri, key, CreateHttpClient(directoryUri));
            await AcmeCallLifetime.AwaitAsync(() => existing.Account(), cancellationToken);
            var accountUri = await AcmeCallLifetime.AwaitAsync(() => existing.GetAccountUri(), cancellationToken);
            await ports.RecordAccount(directoryUri, contactEmail, accountKeyPath, accountUri?.AbsoluteUri, cancellationToken);
            return existing;
        }

        var accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var created = new AcmeContext(directoryUri, accountKey, CreateHttpClient(directoryUri));
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
        await ports.RecordAccount(directoryUri, contactEmail, accountKeyPath, createdAccountUri?.AbsoluteUri, cancellationToken);
        logger.LogInformation("ACME account created and stored. Directory={Directory} AccountUri={AccountUri}", directoryUri, createdAccountUri);
        return created;
        }
        finally { ports.AccountGate.Release(); }
    }

    private IAcmeHttpClient CreateHttpClient(Uri uri) => new AcmeHttpClient(uri, ports.CreateHttpClient is { } create
        ? create(uri) : ownedAccountClient ??= new HttpClient(ports.AccountTransport, disposeHandler: false));

    public void Dispose() => ownedAccountClient?.Dispose();

    private string AccountKeyPath(Uri directoryUri)
    {
        var root = ports.StorageRoot ?? (OperatingSystem.IsWindows()
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
