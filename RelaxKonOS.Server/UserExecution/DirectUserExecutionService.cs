using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>Validates the User Mode direct channel without ever changing process identity.</summary>
public sealed class DirectUserExecutionService(IServerModeResolver serverMode) : IUserExecutionService
{
    public UserExecutionResult Validate(UserExecutionContext context, UserExecutionRequest request)
    {
        if (request.Version != UserExecutionProtocol.Version || request.OperationId is not { } id || id == Guid.Empty)
            return new(false, Error: "invalid user-execution request", ProblemCode: UserExecutionProblemCode.InvalidRequest);
        if (request.Identity != context.Identity)
            return new(false, Error: "user-execution identity mismatch", ProblemCode: UserExecutionProblemCode.IdentityMismatch);
        if (!Enum.IsDefined(request.Operation))
            return new(false, Error: "unsupported user-execution operation", ProblemCode: UserExecutionProblemCode.InvalidRequest);
        if (serverMode.Mode == ServerMode.System)
            return new(false, Error: "the System Mode user-execution Helper is not installed", ProblemCode: UserExecutionProblemCode.HelperUnavailable);
        if (context.Identity.Platform != PlatformKind.Linux)
            return new(false, Error: "User Mode execution is supported only on Linux", ProblemCode: UserExecutionProblemCode.UnsupportedPlatform);
        return new(true);
    }
}
