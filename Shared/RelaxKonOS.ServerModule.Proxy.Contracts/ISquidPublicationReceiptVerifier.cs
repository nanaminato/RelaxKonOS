namespace RelaxKonOS.Server.Proxy;

public interface ISquidPublicationReceiptVerifier
{
    bool Matches(Guid? expectedOperationId, string renderedConfiguration, Guid operationId,
        string candidateHash, string? observedHash, bool applied);
}
