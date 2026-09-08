using System.Net.Http.Headers;

namespace RelaxKonOS.Client.Services;

/// <summary>Attaches the active RelaxKonOS display language to every server API request.</summary>
public sealed class AcceptLanguageHandler(ShellSettings settings) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.AcceptLanguage.Any())
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(settings.Language));
        return base.SendAsync(request, cancellationToken);
    }
}
