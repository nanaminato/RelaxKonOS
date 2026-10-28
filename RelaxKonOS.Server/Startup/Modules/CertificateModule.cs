using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Endpoints;

namespace RelaxKonOS.Server.Startup.Modules;

/// <summary>
/// ACME issuance, renewal and the certificate management surface.
///
/// The TLS boot path deliberately stays in Core: <c>ICertificateStore</c>,
/// <c>CertificateDeploymentRepository</c>, <c>KestrelCertificateRegistry</c> and
/// <c>KestrelCertificateStartupService</c> all live there, because a Server whose listener cannot
/// start is not a Server at all — disabling this module must never take the HTTPS endpoint with it.
/// The module owns only the ACME account workflow, the renewal loop, the operation ledger it serves,
/// and the management routes.
/// </summary>
internal sealed class CertificateModule : IServerModule
{
    public string Id => ServerModules.CertificatesId;
    public IReadOnlyList<string> Capabilities => [ServerCapabilities.Certificates];
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        // FileHttp01ChallengeStore stays in Core: the Nginx manager writes the challenge location
        // that points at its root, so it must exist whether or not ACME issuance is enabled.
        builder.Services.AddSingleton<DirectHttp01ChallengeStore>();
        builder.Services.AddSingleton<IAcmeService, AnvilAcmeService>();
        builder.Services.AddSingleton<IAcmeRenewalInfoProvider>(services => (AnvilAcmeService)services.GetRequiredService<IAcmeService>());
        builder.Services.AddSingleton<CertificateRenewalAttemptRepository>();
        builder.Services.AddSingleton<CertificateOperationStore>();
        builder.Services.AddSingleton<CertificateRenewalRunRepository>();
        builder.Services.AddSingleton<ICertificateManager, CertificateManager>();
        builder.Services.AddHostedService<CertificateRenewalWorker>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context) =>
        endpoints.MapCertificateEndpoints();
}
