using System.Diagnostics;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Services.Diagnostics;

/// <summary>Records completed HTTP traffic, including headers and buffered payloads.</summary>
public sealed class NetworkDiagnosticsHandler(NetworkDiagnosticsService diagnostics, string source) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!diagnostics.IsRecording || !diagnostics.ShouldCapture(request.RequestUri))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var requestHeaders = NetworkDiagnosticsService.CaptureHeaders(request.Headers, request.Content?.Headers);
        var requestBody = await NetworkDiagnosticsService.CapturePayloadAsync(request.Content).ConfigureAwait(false);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var contentType = response.Content?.Headers.ContentType?.MediaType;
            var responseHeaders = NetworkDiagnosticsService.CaptureHeaders(response.Headers, response.Content?.Headers);
            var responseBody = await NetworkDiagnosticsService.CapturePayloadAsync(response.Content).ConfigureAwait(false);
            stopwatch.Stop();
            diagnostics.Record(new NetworkDiagnosticEntry(
                0, startedAt, stopwatch.Elapsed, NetworkDiagnosticKind.Http, source,
                request.RequestUri?.AbsolutePath ?? request.Method.Method, request.Method.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                response.IsSuccessStatusCode ? NetworkDiagnosticOutcome.Succeeded : NetworkDiagnosticOutcome.Failed,
                (int)response.StatusCode, contentType, response.Content?.Headers.ContentLength,
                NetworkDiagnosticsService.IsMediaContent(contentType, request.RequestUri), null,
                requestHeaders, responseHeaders, requestBody, responseBody,
                RequestUrl: request.RequestUri?.ToString()));
            return response;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var outcome = exception is OperationCanceledException
                ? NetworkDiagnosticOutcome.Cancelled : NetworkDiagnosticOutcome.TransportError;
            diagnostics.Record(new NetworkDiagnosticEntry(
                0, startedAt, stopwatch.Elapsed, NetworkDiagnosticKind.Http, source,
                request.RequestUri?.AbsolutePath ?? request.Method.Method, request.Method.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty, outcome, null,
                null, null, false, NetworkDiagnosticsService.ErrorKind(exception), requestHeaders,
                RequestBody: requestBody, RequestUrl: request.RequestUri?.ToString()));
            throw;
        }
    }
}
