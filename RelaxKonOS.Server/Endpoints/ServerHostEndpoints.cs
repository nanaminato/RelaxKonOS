using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>宿主系统类别端点。</summary>
public static class ServerHostEndpoints
{
    public static IEndpointRouteBuilder MapServerHostEndpoints(this IEndpointRouteBuilder app)
    {
        // 这是整个 Server 上唯一一个不需要凭据的信息面，因此它只回答一件事，而且是被逼出来的：
        // 连接管理要在用户尚未登录、手上也没有可用密码时，为一条保存的连接选出平台标记——
        // 那条连接只有一个地址，别的什么都不能问（`/server/capabilities` 需要已认证的会话）。
        //
        // 代价是任何能连到这个端口的人都能知道宿主是 Ubuntu、Windows 10/11 还是 Windows Server。
        // 这属于操作系统指纹，不是凭据、配置或主机身份，因此这里接受它；任何想往响应里加东西的改动
        // 都要重新过一遍这条标准（账号、版本、主机名、路径一律不加）。
        app.MapGet(ServerApiRoutes.HostOperatingSystem, () => Results.Ok(HostOperatingSystemDescriptor.Describe()))
            .AllowAnonymous()
            .WithTags("Server");
        return app;
    }
}
