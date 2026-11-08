using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.WebServer;
using RelaxKonOS.Server.WebsitePublishing;

internal static class WebsitePublicationTestScopes
{
    internal static WebsitePublicationCoordinator Create(WebsitePublicationStore store,
        ApplicationDeploymentManager applications, ApplicationDeploymentCatalogStore catalog,
        IWebServerManager webServers, ICertificateManager certificates, CertificateOperationStore certificateOperations,
        IHttpClientFactory httpClients, IHostApplicationLifetime lifetime, ILogger<WebsitePublicationCoordinator> logger,
        IResidentModuleWorkLeaseSource leases, Action? executionCreated = null, Func<ValueTask>? cleanup = null,
        WebServerExecutionCode? code = null)
    {
        var scopes = new OperationTestScopeFactory<WebsitePublicationExecution>(() =>
        {
            executionCreated?.Invoke();
            return new WebsitePublicationExecution(store, applications, catalog, webServers, certificates, certificateOperations,
                httpClients, lifetime, NullLogger<WebsitePublicationExecution>.Instance, code ?? WebServerExecutionCode.Shared);
        }, cleanup, disposeExecution: true);
        return new WebsitePublicationCoordinator(store, catalog, scopes, lifetime, logger, leases);
    }

}

internal sealed class OperationTestScopeFactory<T>(Func<T> create, Func<ValueTask>? cleanup = null, bool disposeExecution = false) : IServiceScopeFactory where T : class
{
    public IServiceScope CreateScope() => new Scope(create, cleanup, disposeExecution);

    private sealed class Scope(Func<T> create, Func<ValueTask>? cleanup, bool disposeExecution) :
        IServiceScope, IServiceProvider, IAsyncDisposable
    {
        private T? execution;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(T) ? execution ??= create() : null;
        public async ValueTask DisposeAsync()
        {
            try { if (cleanup is not null) await cleanup(); }
            finally { if (disposeExecution && execution is IDisposable owned) owned.Dispose(); }
        }
        public void Dispose() => throw new InvalidOperationException("The operation scope must be disposed asynchronously.");
    }
}
