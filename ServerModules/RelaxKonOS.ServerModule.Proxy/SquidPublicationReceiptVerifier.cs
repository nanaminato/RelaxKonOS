using System.Security.Cryptography;
using System.Text;

namespace RelaxKonOS.Server.Proxy;

public sealed class SquidPublicationReceiptVerifier : ISquidPublicationReceiptVerifier
{
    public bool Matches(Guid? expectedOperationId, string renderedConfiguration, Guid operationId,
        string candidateHash, string? observedHash, bool applied) =>
        operationId != Guid.Empty && operationId == expectedOperationId
        && candidateHash == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(renderedConfiguration)))
        && observedHash is { Length: 64 } && observedHash.All(Uri.IsHexDigit)
        && (!applied || observedHash == candidateHash);
}
