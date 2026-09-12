using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Identity;

public sealed class SessionValidityService(IServiceScopeFactory scopes)
{
    public bool IsValid(Guid userId, long version)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var user = scope.ServiceProvider.GetRequiredService<IUserRepository>().FindById(userId);
            return user is { IdentityReviewRequired: false } && user.SecurityVersion == version;
        }
        catch { return false; }
    }
    public bool IsValid(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true
            || !Guid.TryParse(principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)
            || !long.TryParse(principal.FindFirst("security_version")?.Value, out var version) || version < 0)
            return false;
        if (!principal.HasClaim(RelaxKonOSAuthSchemes.TokenTypeClaim, RelaxKonOSAuthSchemes.FileCapabilityTokenType)
            && (!Guid.TryParse(principal.FindFirst("sid")?.Value, out _)
                || principal.FindFirst("amr")?.Value is not ("system" or "alias")
                || !long.TryParse(principal.FindFirst("auth_time")?.Value, out _))) return false;
        return IsValid(userId, version);
    }
}

public sealed class SessionValidityHubFilter(SessionValidityService validity, AuthSessionStore sessions) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (!validity.IsValid(context.Context.User)) { context.Context.Abort(); throw new HubException("Session expired."); }
        return await next(context);
    }
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        if (!validity.IsValid(context.Context.User)) { context.Context.Abort(); return; }
        var userId = Guid.Parse(context.Context.User!.FindFirst("sub")?.Value ?? context.Context.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        void Revoke(Guid id) { if (id == userId) context.Context.Abort(); }
        sessions.UserRevoked += Revoke;
        _ = WatchAsync(context, () => sessions.UserRevoked -= Revoke);
        try { await next(context); }
        catch { context.Context.Abort(); throw; }
    }
    private async Task WatchAsync(HubLifetimeContext context, Action cleanup)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(context.Context.ConnectionAborted))
                if (!validity.IsValid(context.Context.User)) { context.Context.Abort(); return; }
        }
        catch (OperationCanceledException) { }
        finally { cleanup(); }
    }
}
