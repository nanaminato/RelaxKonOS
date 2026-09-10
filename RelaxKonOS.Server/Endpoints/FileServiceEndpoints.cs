using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.FileServices;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Endpoints;

public static class FileServiceEndpoints
{
    private const string ElevationTarget = "smb:managed";
    public static IEndpointRouteBuilder MapFileServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(FileServiceApiRoutes.Smb).RequireAuthorization().WithTags("File Services");
        group.MapGet(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.Status), (IFileServiceManager manager, CancellationToken ct) => manager.GetStatusAsync(ct)).RequireAuthorization("FileServicesRead");
        group.MapGet(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.Capabilities), (IFileServiceManager manager, CancellationToken ct) => manager.GetCapabilitiesAsync(ct)).RequireAuthorization("FileServicesRead");
        group.MapGet(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.Shares), (IFileServiceManager manager, CancellationToken ct) => manager.ListSharesAsync(ct)).RequireAuthorization("FileServicesRead");
        group.MapGet(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.Connection), (IFileServiceManager manager) => Results.Ok(manager.GetConnectionInfo())).RequireAuthorization("FileServicesRead");
        group.MapPost(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.Install), (HttpContext context, IHostElevationSessionStore elevations, IFileServiceAudit audit, IFileServiceManager manager, CancellationToken ct) => RunAsync(context, elevations, audit, "install", ElevationTarget, () => manager.InstallAsync(ct))).RequireAuthorization("FileServicesManage");
        group.MapPost(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.Start), (HttpContext context, IHostElevationSessionStore elevations, IFileServiceAudit audit, IFileServiceManager manager, CancellationToken ct) => RunAsync(context, elevations, audit, "start", ElevationTarget, () => manager.LifecycleAsync(SmbLifecycleAction.Start, ct))).RequireAuthorization("FileServicesManage");
        group.MapPost(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.Stop), (HttpContext context, IHostElevationSessionStore elevations, IFileServiceAudit audit, IFileServiceManager manager, CancellationToken ct) => RunAsync(context, elevations, audit, "stop", ElevationTarget, () => manager.LifecycleAsync(SmbLifecycleAction.Stop, ct))).RequireAuthorization("FileServicesManage");
        group.MapPost(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.Restart), (HttpContext context, IHostElevationSessionStore elevations, IFileServiceAudit audit, IFileServiceManager manager, CancellationToken ct) => RunAsync(context, elevations, audit, "restart", ElevationTarget, () => manager.LifecycleAsync(SmbLifecycleAction.Restart, ct))).RequireAuthorization("FileServicesManage");
        group.MapPost(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.Shares), (UpsertFileShareRequest request, HttpContext context, IHostElevationSessionStore elevations, IFileServiceAudit audit, IFileServiceManager manager, CancellationToken ct) => RunAsync(context, elevations, audit, "share-create", request.Path, () => manager.CreateShareAsync(request, ct))).RequireAuthorization("FileServicesManage");
        group.MapPut(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.ShareById), (string shareId, UpsertFileShareRequest request, HttpContext context, IHostElevationSessionStore elevations, IFileServiceAudit audit, IFileServiceManager manager, CancellationToken ct) => RunAsync(context, elevations, audit, "share-update", shareId, () => manager.UpdateShareAsync(shareId, request, ct))).RequireAuthorization("FileServicesManage");
        group.MapDelete(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.ShareById), (string shareId, HttpContext context, IHostElevationSessionStore elevations, IFileServiceAudit audit, IFileServiceManager manager, CancellationToken ct) => RunAsync(context, elevations, audit, "share-delete", shareId, () => manager.DeleteShareAsync(shareId, ct))).RequireAuthorization("FileServicesManage");
        // Samba credentials are Linux-only. Windows deliberately exposes neither credential
        // state nor a password endpoint because it never manages Windows host accounts.
        if (OperatingSystem.IsLinux())
        {
            group.MapGet(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.Users), (IFileServiceManager manager, CancellationToken ct) => manager.ListUsersAsync(ct)).RequireAuthorization("FileServicesRead");
            group.MapPost(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.EnableUser), (string username, HttpContext context, IHostElevationSessionStore elevations, IFileServiceAudit audit, IFileServiceManager manager, CancellationToken ct) => RunAsync(context, elevations, audit, "user-enable", username, () => manager.SetUserEnabledAsync(username, true, ct))).RequireAuthorization("FileServicesManage");
            group.MapPost(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.DisableUser), (string username, HttpContext context, IHostElevationSessionStore elevations, IFileServiceAudit audit, IFileServiceManager manager, CancellationToken ct) => RunAsync(context, elevations, audit, "user-disable", username, () => manager.SetUserEnabledAsync(username, false, ct))).RequireAuthorization("FileServicesManage");
            group.MapPut(FileServiceApiRoutes.RelativeToSmb(FileServiceApiRoutes.UserPassword), (string username, SetSambaPasswordRequest request, HttpContext context, IHostElevationSessionStore elevations, IFileServiceAudit audit, IFileServiceManager manager, CancellationToken ct) => RunAsync(context, elevations, audit, "user-password", username, () => manager.SetUserPasswordAsync(username, request.Password, ct))).RequireAuthorization("FileServicesManage");
        }
        return app;
    }
    private static async Task<IResult> RunAsync(HttpContext context, IHostElevationSessionStore elevations, IFileServiceAudit audit, string action, string resource, Func<Task<FileServiceOperationResultDto>> operation)
    {
        if (!elevations.IsGranted(context.User, HostElevationCapability.SmbManage, ElevationTarget))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, extensions: new Dictionary<string, object?> { ["problemCode"] = FileServiceProblemCodes.ElevationRequired });
        var result = await operation();
        // The mutation has completed; audit is part of its durable outcome and must not be
        // dropped merely because the originating HTTP request disconnected.
        await audit.WriteAsync(context.User, action, resource, result.Succeeded, result.ProblemCode, result.OperationId, CancellationToken.None);
        return result.Succeeded ? Results.Ok(result) : Results.Problem(statusCode: Status(result.ProblemCode), extensions: new Dictionary<string, object?> { ["problemCode"] = result.ProblemCode, ["operationId"] = result.OperationId });
    }
    private static int Status(string? code) => code is FileServiceProblemCodes.ShareConflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
}
