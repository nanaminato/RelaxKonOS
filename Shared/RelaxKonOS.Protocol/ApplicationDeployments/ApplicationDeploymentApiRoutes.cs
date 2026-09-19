using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.ApplicationDeployments;

/// <summary>
/// Application deployment routes. Absolute constants are what a client uses; the relative
/// <c>Pattern</c> constants are what the server maps inside <see cref="Root"/>.
/// </summary>
public static class ApplicationDeploymentApiRoutes
{
    private const string V1 = RelaxKonOSEndpoints.ApiVersionPrefix;

    public const string Root = "/" + V1 + "/application-deployments";

    public const string Templates = Root + "/templates";
    public const string TemplatesPattern = "/templates";

    public const string Applications = Root + "/applications";
    public const string ApplicationPattern = "/applications/{applicationId:guid}";
    /// <summary>Creates an application record. Deploying is a separate, idempotent operation.</summary>
    public const string CreateApplication = Applications;
    public const string RevisionsPattern = "/applications/{applicationId:guid}/revisions";
    public const string OperationsPattern = "/applications/{applicationId:guid}/operations";
    public const string LogsPattern = "/applications/{applicationId:guid}/logs";
    public const string DeployPattern = "/applications/{applicationId:guid}/deploy";
    public const string RollbackPattern = "/applications/{applicationId:guid}/rollback";
    public const string StartPattern = "/applications/{applicationId:guid}/start";
    public const string StopPattern = "/applications/{applicationId:guid}/stop";
    public const string RestartPattern = "/applications/{applicationId:guid}/restart";
    public const string OperationPattern = "/operations/{operationId:guid}";
    public const string CancelOperationPattern = "/operations/{operationId:guid}/cancel";
    public const string ActiveOperationPattern = "/operations/active";

    /// <summary>Bounded archive upload into the server-owned staging area.</summary>
    public const string Uploads = Root + "/uploads";
    /// <summary>Registers an already-present server file (for example one chosen in RemoteExplorer).</summary>
    public const string FileReferences = Root + "/file-references";

    public const string UploadPattern = "/uploads";
    public const string FileReferencePattern = "/file-references";

    public static string Application(Guid applicationId) => $"{Applications}/{applicationId:D}";
    public static string Revisions(Guid applicationId) => $"{Applications}/{applicationId:D}/revisions";
    public static string ApplicationOperations(Guid applicationId) => $"{Applications}/{applicationId:D}/operations";
    public static string Logs(Guid applicationId) => $"{Applications}/{applicationId:D}/logs";
    public static string Deploy(Guid applicationId) => $"{Applications}/{applicationId:D}/deploy";
    public static string Rollback(Guid applicationId) => $"{Applications}/{applicationId:D}/rollback";
    public static string Start(Guid applicationId) => $"{Applications}/{applicationId:D}/start";
    public static string Stop(Guid applicationId) => $"{Applications}/{applicationId:D}/stop";
    public static string Restart(Guid applicationId) => $"{Applications}/{applicationId:D}/restart";
    public static string Operation(Guid operationId) => $"{Root}/operations/{operationId:D}";
    public static string Cancel(Guid operationId) => Operation(operationId) + "/cancel";
    public static string ActiveOperation() => $"{Root}/operations/active";
}
