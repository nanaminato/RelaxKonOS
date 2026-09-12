using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Server.Identity;

namespace RelaxKonOS.Server.Endpoints;

public sealed class AuthenticationEndpointFilter(AuthenticationGate gate) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { using var lease = gate.Enter(); return await next(context); }
        catch (AliasAuthenticationException exception) { return Failure(context.HttpContext, exception.Status, exception.Code); }
        catch (DbException) { return Failure(context.HttpContext, 503, "authentication-unavailable"); }
        catch (DbUpdateException) { return Failure(context.HttpContext, 503, "authentication-unavailable"); }
    }
    private static IResult Failure(HttpContext http, int status, string code)
    {
        if (status == 429) http.Response.Headers.RetryAfter = "5";
        return Results.Problem(statusCode: status, type: "https://relaxkonos.app/problems/" + code,
            title: status == 401 ? "Invalid credentials" : code,
            detail: status == 401 ? "Login failed. Check your credentials and try again." : "The authentication operation could not be completed.");
    }
}

public static class AliasEndpoints
{
    public static IEndpointRouteBuilder MapAliasEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").RequireAuthorization().WithTags("Account security")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8192))
            .AddEndpointFilter<AuthenticationEndpointFilter>();
        group.MapGet(AuthApiRoutes.LoginAlias, (HttpContext http, AliasCredentialService service)
            => Results.Ok(service.Read(service.RequireUser(http.User))));
        group.MapPost(AuthApiRoutes.LoginAlias, async (CreateAliasRequest request, HttpContext http, AliasCredentialService service, CancellationToken ct)
            => Results.Created(AuthApiRoutes.LoginAlias, await service.ChangeAsync(service.RequireUser(http.User), http.User, request, http, ct)));
        group.MapPut(AuthApiRoutes.LoginAlias, async (RenameAliasRequest request, HttpContext http, AliasCredentialService service, CancellationToken ct)
            => Results.Ok(await service.ChangeAsync(service.RequireUser(http.User), http.User, request, http, ct)));
        group.MapPut(AuthApiRoutes.AliasPassword, async (ChangeAliasPasswordRequest request, HttpContext http, AliasCredentialService service, CancellationToken ct) =>
        {
            await service.ChangeAsync(service.RequireUser(http.User), http.User, request, http, ct);
            return Results.NoContent();
        });
        group.MapPost(AuthApiRoutes.DeleteAlias, async (DeleteAliasRequest request, HttpContext http, AliasCredentialService service, CancellationToken ct) =>
        {
            await service.ChangeAsync(service.RequireUser(http.User), http.User, request, http, ct);
            return Results.NoContent();
        });
        group.MapPut(AuthApiRoutes.SystemLogin, async (SetSystemLoginRequest request, HttpContext http, AliasCredentialService service, CancellationToken ct)
            => Results.Ok(await service.ChangeAsync(service.RequireUser(http.User), http.User, request, http, ct)));
        return app;
    }
}
