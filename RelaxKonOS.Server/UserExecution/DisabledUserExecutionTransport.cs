using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.Server.UserExecution;

public sealed class DisabledUserExecutionTransport : IUserExecutionTransport
{
    public Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new UserExecutionResult(false, Error: "user execution is unavailable", ProblemCode: UserExecutionProblemCode.HelperUnavailable));
}
