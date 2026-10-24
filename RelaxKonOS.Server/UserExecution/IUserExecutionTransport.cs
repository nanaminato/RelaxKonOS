using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.Server.UserExecution;

public interface IUserExecutionTransport
{
    Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request, CancellationToken cancellationToken = default);
    Task<UserExecutionResult> ExecuteArchiveAsync(UserExecutionRequest request, Action<ArchiveExtractionProgress> progress,
        CancellationToken cancellationToken = default) => ExecuteAsync(request, cancellationToken);
}
