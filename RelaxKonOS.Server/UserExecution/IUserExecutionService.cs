using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>
/// Boundary for the dedicated local user-execution transport. The initial implementation is a
/// no-side-effect User Mode adapter; System Mode intentionally fails closed until its separate
/// Helper implementation is installed.
/// </summary>
public interface IUserExecutionService
{
    UserExecutionResult Validate(UserExecutionContext context, UserExecutionRequest request);
}
