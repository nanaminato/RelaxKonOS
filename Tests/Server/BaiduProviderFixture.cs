using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;
using RelaxKonOS.ServerModule.CloudBaidu;
using RelaxKonOS.ServerModuleHost;

internal sealed class BaiduProviderFixture(WebApplication app, ICloudFileProviders client) : IAsyncDisposable
{
    public ICloudFileProviders Client => client;

    internal static async Task<BaiduProviderFixture> StartAsync(IModuleHostContext context, HttpMessageHandler upstream)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var module = new CloudBaiduModule((_, _) => new HttpClient(upstream, disposeHandler: false));
        try
        {
            await module.StartAsync(context, default);
            app.MapGet(CloudProviderProtocol.RequestPath(CloudFileProvider.Baidu), () => Results.NoContent());
            module.MapEndpoints(app, context);
            await app.StartAsync();
            return new(app, new CloudProviderClient(app.Urls.Single(), new string('a', 64)));
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}
