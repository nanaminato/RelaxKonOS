using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Isolated DI for existing domain recovery fixtures, with lazy per-kind selection.</summary>
internal sealed class PublicationRecoveryServices : IDisposable
{
    private readonly ServiceProvider provider;
    internal IServiceScopeFactory Scopes => provider.GetRequiredService<IServiceScopeFactory>();
    internal PublicationRecoveryServices(MihomoPublicationStore store,
        Func<IMihomoConfigurationPublicationRecovery> configuration,
        Func<MihomoInstallationPublicationService> installation,
        Func<MihomoRemovalPublicationService> removal)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddTransient(_ => configuration());
        services.AddTransient(_ => installation());
        services.AddTransient(_ => removal());
        provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
    public void Dispose() => provider.Dispose();
}
