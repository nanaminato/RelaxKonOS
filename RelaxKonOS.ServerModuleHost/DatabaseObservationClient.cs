using System.Net;
using Microsoft.AspNetCore.Http;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;

namespace RelaxKonOS.ServerModuleHost;

internal sealed class DatabaseObservationClient(string moduleId, string? brokerUrl, string? token,
    IHttpContextAccessor accessor, HttpClient? transport = null) : IModuleDatabaseObservation
{
    private static readonly HttpClient Transport = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    }) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };

    public async ValueTask<HttpResponseMessage> ReadAsync(DatabaseObservationKind kind, Guid? instance,
        CancellationToken cancellationToken)
    {
        if (moduleId != DatabaseObservationProtocol.ModuleId || accessor.HttpContext is not { } incoming)
            throw new HttpRequestException("module.database_observation_denied", null, HttpStatusCode.Forbidden);
        var context = incoming.Request.Headers[ModulePrivilegeProtocol.RequestContextHeader];
        if (context.Count != 1 || context[0] is not { Length: 64 } contextToken)
            throw new HttpRequestException("module.database_observation_denied", null, HttpStatusCode.Forbidden);
        if (!Uri.TryCreate(brokerUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp
            || !endpoint.IsLoopback || token is not { Length: 64 })
            throw new HttpRequestException("module.database_observation_unavailable", null, HttpStatusCode.ServiceUnavailable);

        var path = kind switch
        {
            DatabaseObservationKind.PostgreSqlState => DatabaseObservationProtocol.PostgreSqlState,
            DatabaseObservationKind.PostgreSqlMaintenancePlan => DatabaseObservationProtocol.PostgreSqlMaintenancePlan,
            DatabaseObservationKind.PostgreSqlMigrationPlan => DatabaseObservationProtocol.PostgreSqlMigrationPlan,
            DatabaseObservationKind.MySqlState => DatabaseObservationProtocol.MySqlState,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (instance is not null && kind != DatabaseObservationKind.PostgreSqlState)
            throw new ArgumentException("Only PostgreSQL state accepts an instance.", nameof(instance));
        if (instance is { } selected) path += "?instance=" + selected.ToString("D");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, path));
        request.Headers.Add(ModuleHostProtocol.ServiceTokenHeader, token);
        request.Headers.Add(ModulePrivilegeProtocol.RequestContextHeader, contextToken);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, incoming.RequestAborted);
        return await (transport ?? Transport).SendAsync(request, HttpCompletionOption.ResponseContentRead, stopping.Token);
    }
}
