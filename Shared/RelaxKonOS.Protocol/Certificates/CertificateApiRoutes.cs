using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Certificates;

public static class CertificateApiRoutes
{
    private const string V1 = RelaxKonOSEndpoints.ApiVersionPrefix;
    public const string Certificates = $"/{V1}/certificates";
    public const string CollectionPattern = "";
    public const string Preflight = $"{Certificates}/preflight";
    public const string PreflightPattern = "/preflight";
    public const string ById = $"{Certificates}/{{id}}";
    public const string ByIdPattern = "/{id:guid}";
    public const string Request = Certificates;
    public const string SelfSigned = $"{Certificates}/self-signed";
    public const string SelfSignedPattern = "/self-signed";
    public const string Renew = $"{Certificates}/{{id}}/renew";
    public const string RenewPattern = "/{id:guid}/renew";
    public const string Deploy = $"{Certificates}/{{id}}/deployments/kestrel";
    public const string DeployPattern = "/{id:guid}/deployments/kestrel";
    public const string Revoke = $"{Certificates}/{{id}}/revoke";
    public const string RevokePattern = "/{id:guid}/revoke";
    public const string DeletePattern = "/{id:guid}";
    public const string Operations = $"{Certificates}/operations/{{operationId}}";
    public const string OperationsPattern = "/operations/{operationId:guid}";
    public const string CancelOperation = $"{Certificates}/operations/{{operationId}}/cancel";
    public const string CancelOperationPattern = "/operations/{operationId:guid}/cancel";
}
