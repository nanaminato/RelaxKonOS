using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Foundation.Auth;
using RelaxKonOS.Client.Foundation.Http;

namespace RelaxKonOS.Client.Foundation.DependencyInjection;

public static class FoundationServiceCollectionExtensions
{
    public static IServiceCollection AddRelaxKonOSFoundation(this IServiceCollection services)
    {
        services.AddHttpClient<IRelaxKonOSClient, RelaxKonOSClient>();
        services.AddSingleton<IConnectionProfileStore, InMemoryConnectionProfileStore>();
        services.AddSingleton<IAuthSession, AuthSession>();
        return services;
    }
}
