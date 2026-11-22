using System.Security.Cryptography;
using System.Text;

namespace RelaxKonOS.Server.Proxy;

public sealed class SquidReceiptVerifier : ISquidReceiptVerifier
{
    public bool Matches(Guid? expectedOperationId, string renderedConfiguration, Guid operationId,
        string candidateHash, string? observedHash, bool applied) =>
        operationId != Guid.Empty && operationId == expectedOperationId
        && candidateHash == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(renderedConfiguration)))
        && observedHash is { Length: 64 } && observedHash.All(Uri.IsHexDigit)
        && (!applied || observedHash == candidateHash);

    public bool MatchesLifecycle(Guid expectedOperationId, int expectedAction, Guid operationId, int action) =>
        operationId == expectedOperationId && action == expectedAction;

    public bool MatchesAccount(Guid expectedOperationId, string expectedIdentity, Guid operationId, string requestIdentity) =>
        operationId == expectedOperationId && requestIdentity == expectedIdentity;
}
