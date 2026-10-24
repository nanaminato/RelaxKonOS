using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;

internal static class EditorWriteChecks
{
    public static async Task RunAsync()
    {
        foreach (var kind in new[] { "success", "route", "parent", "permission" })
        {
            var calls = 0;
            using var http = new HttpClient(new Handler(async request =>
            {
                calls++;
                if (request.Method != HttpMethod.Put || request.RequestUri!.AbsolutePath != FileApiRoutes.ConditionalContent)
                    throw new Exception("Editor must use the current conditional write route.");
                var body = await request.Content!.ReadFromJsonAsync<ConditionalFileWriteRequest>(RelaxKonOSJsonOptions.Default);
                if (body is null || body.Path != "C:/project/new.cs" || body.Content.Length != 0 || body.ExpectedVersion is not null)
                    throw new Exception("New file must submit empty bytes and an explicit null version.");
                return kind switch
                {
                    "success" => new(HttpStatusCode.OK) { Content = JsonContent.Create(new ConditionalFileWriteResult(new string('a', 64))) },
                    "route" => new(HttpStatusCode.NotFound) { Content = new StringContent("") },
                    "parent" => new(HttpStatusCode.NotFound) { Content = JsonContent.Create(new ProblemDetails(
                        "https://relaxkonos.app/problems/not-found", "Not found", 404, "Target directory not found.", null)) },
                    _ => new(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new ProblemDetails(
                        "https://relaxkonos.app/problems/access-denied", "Access denied", 403, "Permission denied.", null)) },
                };
            }));
            var client = new ExplorerClient(http, DispatchProxy.Create<IAuthSession, DownloadSession>(), null!);
            try
            {
                var version = await client.WriteEditorFileAsync("C:/project/new.cs", [], null);
                if (kind != "success" || version != new string('a', 64)) throw new Exception("Unexpected write success.");
            }
            catch (RelaxKonOSAuthException error)
            {
                var expected = kind switch { "route" => "editor-endpoint-unavailable", "parent" => "not-found", "permission" => "access-denied", _ => "unexpected" };
                if (!error.Type.EndsWith("/" + expected) || (kind == "route" && !error.Message.Contains(FileApiRoutes.ConditionalContent)))
                    throw new Exception("Editor must distinguish missing routes, missing directories and denied access.", error);
            }
            if (calls != 1) throw new Exception("A failed exclusive creation must not retry through another route.");
        }
        Console.WriteLine("PASS: Editor HTTP creation uses the current contract and distinguishes route, path and permission failures.");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
