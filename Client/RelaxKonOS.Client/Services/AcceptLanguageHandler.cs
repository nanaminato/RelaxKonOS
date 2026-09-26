using System.Net.Http.Headers;

namespace RelaxKonOS.Client.Services;

/// <summary>Attaches the effective desktop display language to every server API request.</summary>
public sealed class AcceptLanguageHandler(LocalizationService localization) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.AcceptLanguage.Any())
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(localization.CurrentLanguage));
        return base.SendAsync(request, cancellationToken);
    }
}
