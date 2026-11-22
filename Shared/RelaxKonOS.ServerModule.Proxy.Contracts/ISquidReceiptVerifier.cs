namespace RelaxKonOS.Server.Proxy;

public interface ISquidReceiptVerifier
{
    bool Matches(Guid? expectedOperationId, string renderedConfiguration, Guid operationId,
        string candidateHash, string? observedHash, bool applied);

    bool MatchesLifecycle(Guid expectedOperationId, int expectedAction, Guid operationId, int action);

    bool MatchesAccount(Guid expectedOperationId, string expectedIdentity, Guid operationId, string requestIdentity);
}
