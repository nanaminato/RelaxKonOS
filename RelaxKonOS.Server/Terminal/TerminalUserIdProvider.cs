using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace RelaxKonOS.Server.Terminal;

/// <summary>
/// 以 JWT 的 <c>sub</c> claim（用户 ID）作为 SignalR <see cref="HubConnectionContext.UserIdentifier"/>，
/// 使 <c>TerminalHub</c> 可按用户过滤会话列表 / 索引持久会话。注册见 <c>Program.cs</c> 的 AddSignalR。
/// </summary>
public sealed class TerminalUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        var user = connection.User;
        return user?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
