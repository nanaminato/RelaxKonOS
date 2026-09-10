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
        group.MapGet("/status", (IFileServiceManager manager, CancellationToken ct) => manager.GetStatusAsync(ct)).RequireAuthorization("FileServicesRead");
        group.MapGet("/capabilities", (IFileServiceManager manager, CancellationToken ct) => manager.GetCapabilitiesAsync(ct)).RequireAuthorization("FileServicesRead");
        group.MapGet(FileServiceApiRoutes.Shares, (IFileServiceManager manager, CancellationToken ct) => manager.ListSharesAsync(ct)).RequireAuthorization("FileServicesRead");
        group.MapGet(FileServiceApiRoutes.Users, (IFileServiceManager manager, CancellationToken ct) => manager.ListUsersAsync(ct)).RequireAuthorization("FileServicesRead");
        group.MapGet(FileServiceApiRoutes.Connection, (IFileServiceManager manager) => Results.Ok(manager.GetConnectionInfo())).RequireAuthorization("FileServicesRead");
        group.MapPost(FileServiceApiRoutes.Install, (HttpContext context, IHostElevationSessionStore elevations, IFileServiceManager manager, CancellationToken ct) =>
            RunAsync(context, elevations, () => manager.InstallAsync(ct))).RequireAuthorization("FileServicesManage");
        group.MapPost(FileServiceApiRoutes.Start, (HttpContext context, IHostElevationSessionStore elevations, IFileServiceManager manager, CancellationToken ct) =>
            RunAsync(context, elevations, () => manager.LifecycleAsync(SmbLifecycleAction.Start, ct))).RequireAuthorization("FileServicesManage");
        group.MapPost(FileServiceApiRoutes.Stop, (HttpContext context, IHostElevationSessionStore elevations, IFileServiceManager manager, CancellationToken ct) =>
            RunAsync(context, elevations, () => manager.LifecycleAsync(SmbLifecycleAction.Stop, ct))).RequireAuthorization("FileServicesManage");
        group.MapPost(FileServiceApiRoutes.Restart, (HttpContext context, IHostElevationSessionStore elevations, IFileServiceManager manager, CancellationToken ct) =>
            RunAsync(context, elevations, () => manager.LifecycleAsync(SmbLifecycleAction.Restart, ct))).RequireAuthorization("FileServicesManage");
        group.MapPost(FileServiceApiRoutes.Shares, (UpsertFileShareRequest request, HttpContext context, IHostElevationSessionStore elevations, IFileServiceManager manager, CancellationToken ct) =>
            RunAsync(context, elevations, () => manager.CreateShareAsync(request, ct))).RequireAuthorization("FileServicesManage");
        group.MapPut(FileServiceApiRoutes.ShareById, (string shareId, UpsertFileShareRequest request, HttpContext context, IHostElevationSessionStore elevations, IFileServiceManager manager, CancellationToken ct) =>
            RunAsync(context, elevations, () => manager.UpdateShareAsync(shareId, request, ct))).RequireAuthorization("FileServicesManage");
        group.MapDelete(FileServiceApiRoutes.ShareById, (string shareId, HttpContext context, IHostElevationSessionStore elevations, IFileServiceManager manager, CancellationToken ct) =>
            RunAsync(context, elevations, () => manager.DeleteShareAsync(shareId, ct))).RequireAuthorization("FileServicesManage");
        group.MapPost(FileServiceApiRoutes.EnableUser, (string username, HttpContext context, IHostElevationSessionStore elevations, IFileServiceManager manager, CancellationToken ct) =>
            RunAsync(context, elevations, () => manager.SetUserEnabledAsync(username, true, ct))).RequireAuthorization("FileServicesManage");
        group.MapPost(FileServiceApiRoutes.DisableUser, (string username, HttpContext context, IHostElevationSessionStore elevations, IFileServiceManager manager, CancellationToken ct) =>
            RunAsync(context, elevations, () => manager.SetUserEnabledAsync(username, false, ct))).RequireAuthorization("FileServicesManage");
        group.MapPut(FileServiceApiRoutes.UserPassword, (string username, SetSambaPasswordRequest request, HttpContext context, IHostElevationSessionStore elevations, IFileServiceManager manager, CancellationToken ct) =>
            RunAsync(context, elevations, () => manager.SetUserPasswordAsync(username, request.Password, ct))).RequireAuthorization("FileServicesManage");
        return app;
    }
    private static async Task<IResult> RunAsync(HttpContext context, IHostElevationSessionStore elevations, Func<Task<FileServiceOperationResultDto>> operation)
    {
        if (!elevations.IsGranted(context.User, HostElevationCapability.SmbManage, ElevationTarget))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, extensions: new Dictionary<string, object?> { ["problemCode"] = FileServiceProblemCodes.ElevationRequired });
        var result = await operation();
        return result.Succeeded ? Results.Ok(result) : Results.Problem(statusCode: Status(result.ProblemCode), extensions: new Dictionary<string, object?> { ["problemCode"] = result.ProblemCode, ["operationId"] = result.OperationId });
    }
    private static int Status(string? code) => code is FileServiceProblemCodes.ShareConflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
}
