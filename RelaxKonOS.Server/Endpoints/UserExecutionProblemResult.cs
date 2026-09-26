using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>
/// The one place a refused execution boundary becomes a wire problem. Identity refusals keep their own
/// kebab-case type so a client can explain the cause; every other boundary failure shares the generic
/// "unavailable" type. Endpoints must not build these responses by hand, or a code would end up with
/// two names depending on which route it travelled.
/// </summary>
internal static class UserExecutionProblemResult
{
    public static IResult From(UserExecutionException exception) => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: exception.ProblemCode == UserExecutionProblemCode.IdentityNotEligible
            ? "Identity not eligible for user execution"
            : "User execution unavailable",
        detail: exception.Message,
        type: UserExecutionProblemTypes.Uri(exception.ProblemCode));
}
