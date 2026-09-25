using System.Security.Claims;

namespace RelaxKonOS.Server.UserExecution;

public interface IUserExecutionContextResolver
{
    UserExecutionContext Resolve(ClaimsPrincipal principal);
}
