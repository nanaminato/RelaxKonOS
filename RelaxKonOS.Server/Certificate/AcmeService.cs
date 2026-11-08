using System.Security.Cryptography.X509Certificates;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Certificate;

/// <summary>Host-owned ports and account gate for the bundled ACME executor.</summary>
internal sealed class AnvilAcmeService : IAcmeService, IAcmeRenewalInfoProvider
{
    private static readonly SocketsHttpHandler AccountTransport = new();
    private readonly AcmeExecutionPorts ports;
    private readonly ILogger logger;
    private readonly AcmeExecution execution;

    public AnvilAcmeService(FileHttp01ChallengeStore webRoot, DirectHttp01ChallengeStore direct, CertificateOptions options,
        CertificateMetadataRepository metadata, ILogger<AnvilAcmeService> logger, Func<Uri, HttpClient>? createHttpClient = null)
        : this(webRoot, direct, options, metadata, logger, AcmeExecution.Shared, createHttpClient) { }

    internal AnvilAcmeService(FileHttp01ChallengeStore webRoot, DirectHttp01ChallengeStore direct, CertificateOptions options,
        CertificateMetadataRepository metadata, ILogger<AnvilAcmeService> logger, AcmeExecution execution, Func<Uri, HttpClient>? createHttpClient = null)
    {
        this.execution = execution;
        this.logger = logger;
        ports = new(options.DirectoryUrl, options.StorageRoot, new(1, 1), metadata.UpsertAcmeAccountAsync, webRoot.RequireVacant,
            (isDirect, token, value, ct) => Store(isDirect).PutAsync(token, value, ct),
            (isDirect, token, owned, ct) => owned is null ? Store(isDirect).RemoveAsync(token, ct) : webRoot.RemoveOwnedAsync(owned, ct),
            AccountTransport, createHttpClient);

        IHttp01ChallengeStore Store(bool isDirect) => isDirect ? direct : webRoot;
    }

    public Task<CertificateMaterial> IssueAsync(Guid certificateId, IReadOnlyList<string> domains, CertificateChallengeType challengeType,
        CertificateKeyAlgorithm keyAlgorithm, string contactEmail, CancellationToken cancellationToken,
        Func<AcmeOrderIdentity, Task>? recordOrder = null, Func<WebRootChallengeFile, Task>? recordChallenge = null,
        Func<string, Task>? recordCertificateKey = null) =>
        InvokeAsync(acme => acme.IssueAsync(certificateId, domains, challengeType, keyAlgorithm, contactEmail, cancellationToken,
            recordOrder is null ? null : identity => CheckpointAsync(recordOrder, identity),
            recordChallenge is null ? null : file => CheckpointAsync(recordChallenge, file),
            recordCertificateKey is null ? null : key => CheckpointAsync(recordCertificateKey, key)), cancellationToken);

    public Task<string> ObserveOrderAsync(AcmeOrderIdentity identity, CancellationToken ct) =>
        InvokeAsync(acme => acme.ObserveOrderAsync(identity, ct), ct);

    public Task RevokeAsync(X509Certificate2 certificate, string contactEmail, CancellationToken ct) =>
        InvokeAsync(async acme => { await acme.RevokeAsync(certificate, contactEmail, ct); return true; }, ct);

    public Task<AcmeRenewalWindow?> GetRenewalWindowAsync(X509Certificate2 certificate, string contactEmail, CancellationToken ct) =>
        InvokeAsync(acme => ((IAcmeRenewalInfoProvider)acme).GetRenewalWindowAsync(certificate, contactEmail, ct), ct);

    public Task MarkReplacedAsync(X509Certificate2 certificate, string contactEmail, CancellationToken ct) =>
        InvokeAsync(async acme => { await ((IAcmeRenewalInfoProvider)acme).MarkReplacedAsync(certificate, contactEmail, ct); return true; }, ct);

    private static async Task CheckpointAsync<T>(Func<T, Task> persist, T value)
    {
        try { await persist(value); }
        catch (ResidentWorkCompletionException) { throw new AcmeCheckpointException(); }
    }

    private async Task<T> InvokeAsync<T>(Func<IAcmeService, Task<T>> run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var lease = execution.Acquire(ports, logger);
        try { return await run(lease.Executor); }
        catch (AcmeCleanupException error)
        {
            throw new CertificateChallengeCleanupException([new CertificateOperationException(error.OwnershipConflict
                ? "certificate.challenge_ownership_unconfirmed" : "certificate.challenge_cleanup_unconfirmed")]);
        }
        catch (AcmeCheckpointException) { throw new ResidentWorkCompletionException(new InvalidOperationException("ACME checkpoint could not be persisted.")); }
        catch (CertificateOperationException error) { throw new CertificateOperationException(error.ProblemCode); }
        catch (OperationCanceledException) { throw new OperationCanceledException(ct); }
        catch (Exception error)
        {
            logger.LogWarning("ACME execution failed at the module boundary. ErrorType={ErrorType}", error.GetType().Name);
            throw new CertificateOperationException("certificate.operation_failed");
        }
    }
}
