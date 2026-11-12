using Microsoft.AspNetCore.Mvc;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Endpoints;

internal static class CloudOAuthCallbackEndpoints
{
    internal static IEndpointRouteBuilder MapCloudOAuthCallback(this IEndpointRouteBuilder app)
    {
        // Intentionally outside the authenticated module-management group. Only this exact
        // Google callback is anonymous. The module resolves the owner from its single-use state.
        app.MapGet(CloudOAuthCallbackRequest.PublicPath, async (HttpContext http,
            [FromServices] ModuleHostSupervisor hosts) =>
        {
            CloudOAuthCallbackRequest.Harden(http.Response);
            if (!CloudOAuthCallbackRequest.TryRead(http.Request, out var query))
                return Reply("Invalid authorization response.", StatusCodes.Status400BadRequest);
            using var connection = hosts.AcquireConnection(CloudStorageApi.ModuleId);
            if (connection is null)
                return Reply("Cloud storage authorization is unavailable.", StatusCodes.Status503ServiceUnavailable);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, connection.Stopping);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
                { Timeout = TimeSpan.FromSeconds(30) };
            using var request = CloudOAuthCallbackRequest.Create(connection.Endpoint, connection.ServiceToken, query);
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stopping.Token);
                // Do not relay HTML, Location, Set-Cookie, tokens or provider errors from the module.
                // Completion is read by the authenticated initiating client, never by this browser page.
                return response.IsSuccessStatusCode
                    ? Reply("Authorization response received. Return to RelaxKonOS to check the result.", 200)
                    : Reply("Authorization could not be completed. Return to RelaxKonOS and try again.", 400);
            }
            catch (HttpRequestException)
            {
                return Reply("Cloud storage authorization is unavailable.", StatusCodes.Status503ServiceUnavailable);
            }
            catch (OperationCanceledException)
            {
                return Reply("Authorization request timed out. Return to RelaxKonOS to check the result.", StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous().WithTags("Cloud storage authorization");
        return app;
    }

    private static IResult Reply(string text, int status) => Results.Text(text, "text/plain", statusCode: status);
}
