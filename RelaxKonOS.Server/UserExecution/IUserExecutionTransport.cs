using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.Server.UserExecution;

public interface IUserExecutionTransport
{
    Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request, CancellationToken cancellationToken = default);
}
