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
        var scopes = new OperationTestScopeFactory<WebsitePublicationExecution>(() =>
        {
            executionCreated?.Invoke();
            return new WebsitePublicationExecution(store, applications, catalog, webServers, certificates, certificateOperations,
                httpClients, lifetime, NullLogger<WebsitePublicationExecution>.Instance);
        }, cleanup);
        return new WebsitePublicationCoordinator(store, catalog, scopes, lifetime, logger, leases);
    }

}

internal sealed class OperationTestScopeFactory<T>(Func<T> create, Func<ValueTask>? cleanup = null) : IServiceScopeFactory where T : class
{
    public IServiceScope CreateScope() => new Scope(create, cleanup);

    private sealed class Scope(Func<T> create, Func<ValueTask>? cleanup) :
        IServiceScope, IServiceProvider, IAsyncDisposable
    {
        private T? execution;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(T) ? execution ??= create() : null;
        public ValueTask DisposeAsync() => cleanup?.Invoke() ?? ValueTask.CompletedTask;
        public void Dispose() => throw new InvalidOperationException("The operation scope must be disposed asynchronously.");
    }
}
