using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.WebsitePublishing;

internal static class WebsitePublicationTestScopes
{
    internal static WebsitePublicationCoordinator Create(WebsitePublicationStore store,
        ApplicationDeploymentManager applications, ApplicationDeploymentCatalogStore catalog,
        IWebServerManager webServers, ICertificateManager certificates, CertificateOperationStore certificateOperations,
        IHttpClientFactory httpClients, IHostApplicationLifetime lifetime, ILogger<WebsitePublicationCoordinator> logger,
        IResidentModuleWorkLeaseSource leases, Action? executionCreated = null, Func<ValueTask>? cleanup = null)
    {
        var scopes = new Factory(() =>
        {
            executionCreated?.Invoke();
            return new WebsitePublicationExecution(store, applications, catalog, webServers, certificates, certificateOperations,
                httpClients, lifetime, NullLogger<WebsitePublicationExecution>.Instance);
        }, cleanup);
        return new WebsitePublicationCoordinator(store, catalog, scopes, lifetime, logger, leases);
    }

    private sealed class Factory(Func<WebsitePublicationExecution> create, Func<ValueTask>? cleanup) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(create, cleanup);
    }

    private sealed class Scope(Func<WebsitePublicationExecution> create, Func<ValueTask>? cleanup) :
        IServiceScope, IServiceProvider, IAsyncDisposable
    {
        private WebsitePublicationExecution? execution;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(WebsitePublicationExecution) ? execution ??= create() : null;
        public ValueTask DisposeAsync() => cleanup?.Invoke() ?? ValueTask.CompletedTask;
        public void Dispose() => throw new InvalidOperationException("The publication scope must be disposed asynchronously.");
    }
}
