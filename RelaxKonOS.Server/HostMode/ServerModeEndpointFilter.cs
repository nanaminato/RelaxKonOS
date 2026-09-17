using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.HostMode;

public sealed class ServerModeEndpointFilter(ServerHostFeature feature) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var mode = context.HttpContext.RequestServices.GetRequiredService<IServerModeResolver>();
        return mode.Supports(feature)
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                type: "https://relaxkonos.app/problems/privileged-feature-unavailable",
                title: "Feature unavailable in User Mode", detail: "This host operation is unavailable for the current Linux user deployment."));
    }
}

public static class ServerModeEndpointConventionExtensions
{
    public static RouteGroupBuilder RequireHostFeature(this RouteGroupBuilder group, ServerHostFeature feature)
        => group.AddEndpointFilter(new ServerModeEndpointFilter(feature));
}
