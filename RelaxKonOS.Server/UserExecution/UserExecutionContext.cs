using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>
/// Server-internal authenticated execution identity. Instances can only be made by the resolver;
/// callers never receive a way to substitute a username, UID/SID, or home directory.
/// </summary>
public sealed record UserExecutionContext(Guid RelaxKonOSUserId, UserExecutionIdentity Identity);

public sealed class UserExecutionException(UserExecutionProblemCode problemCode, string message) : Exception(message)
{
    public UserExecutionProblemCode ProblemCode { get; } = problemCode;
}
