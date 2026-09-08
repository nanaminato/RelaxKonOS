using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>Registers the shared protected-request token lifecycle on a typed server client.</summary>
public static class RemoteOsHttpClientBuilderExtensions
{
    public static IHttpClientBuilder AddRemoteOsAuthentication(this IHttpClientBuilder builder)
        => builder.AddHttpMessageHandler<AuthenticatedHttpHandler>();
}
