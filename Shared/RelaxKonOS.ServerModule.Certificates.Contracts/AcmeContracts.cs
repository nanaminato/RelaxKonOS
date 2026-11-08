using System.Security.Cryptography;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using RelaxKonOS.Protocol.Certificates;

namespace RelaxKonOS.Server.Certificate;

public sealed record AcmeExecutionPorts(string DirectoryUrl, string? StorageRoot, SemaphoreSlim AccountGate,
    Func<Uri, string, string, string?, CancellationToken, Task> RecordAccount,
    Action<string> RequireVacantChallenge,
    Func<bool, string, string, CancellationToken, Task> PutChallenge,
    Func<bool, string, WebRootChallengeFile?, CancellationToken, Task> RemoveChallenge,
    HttpMessageHandler AccountTransport,
    Func<Uri, HttpClient>? CreateHttpClient);

public sealed class AcmeCheckpointException() : Exception("ACME checkpoint could not be persisted.");
public sealed class AcmeCleanupException(bool ownershipConflict) : Exception("ACME challenge cleanup could not be confirmed.")
{
    public bool OwnershipConflict { get; } = ownershipConflict;
}

public sealed class CertificateOperationException(string problemCode, Exception? inner = null) : Exception(problemCode, inner)
{
    public string ProblemCode { get; } = problemCode;
}

public interface IAcmeService
{
    Task<CertificateMaterial> IssueAsync(Guid certificateId, IReadOnlyList<string> domains, CertificateChallengeType challengeType,
        CertificateKeyAlgorithm keyAlgorithm, string contactEmail, CancellationToken cancellationToken,
        Func<AcmeOrderIdentity, Task>? recordOrder = null, Func<WebRootChallengeFile, Task>? recordChallenge = null,
        Func<string, Task>? recordCertificateKey = null);
    Task<string> ObserveOrderAsync(AcmeOrderIdentity identity, CancellationToken cancellationToken);
    Task<AcmeIssuedCertificate> ReadIssuedCertificateAsync(AcmeOrderIdentity identity, CancellationToken cancellationToken);
    Task RevokeAsync(X509Certificate2 certificate, string contactEmail, CancellationToken cancellationToken);
}

public sealed record AcmeRenewalWindow(DateTimeOffset Start, DateTimeOffset End);
public sealed record AcmeOrderIdentity(Uri Directory, Uri Account, string AccountThumbprint, Uri Order);
public sealed record AcmeIssuedCertificate(string Status, string? ChainPem = null, string[]? DnsNames = null);

public interface IAcmeRenewalInfoProvider
{
    Task<AcmeRenewalWindow?> GetRenewalWindowAsync(X509Certificate2 certificate, string contactEmail, CancellationToken cancellationToken);
    Task MarkReplacedAsync(X509Certificate2 certificate, string contactEmail, CancellationToken cancellationToken);
}

public sealed record CertificateMaterial(Guid Id, IReadOnlyList<string> Domains, CertificateChallengeType ChallengeType,
    CertificateKeyAlgorithm KeyAlgorithm, string? ContactEmail, string CertificatePem, string PrivateKeyPem, DateTimeOffset CreatedAt, DateTimeOffset? LastRenewalAt = null, string? LastRenewalProblemCode = null,
    CertificateKind Kind = CertificateKind.Acme);

public sealed record WebRootChallengeFile(string Token, string ContentHash)
{
    public static WebRootChallengeFile Describe(string token, string authorization) =>
        new(token, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorization))));
}
