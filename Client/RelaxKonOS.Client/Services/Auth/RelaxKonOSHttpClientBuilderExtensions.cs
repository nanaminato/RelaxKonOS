using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>Registers the shared protected-request token lifecycle on a typed server client.</summary>
public static class RelaxKonOSHttpClientBuilderExtensions
{
    public static IHttpClientBuilder AddRelaxKonOSAuthentication(this IHttpClientBuilder builder)
        => builder.AddHttpMessageHandler<AuthenticatedHttpHandler>();
}
