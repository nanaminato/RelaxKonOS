using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Startup;

/// <summary>
/// The problem contract is one shape for every domain failure: status, <c>title=code</c>,
/// <c>type=problems/{code}</c> and the <c>problemCode</c> extension. These checks pin that shape for
/// the two pilot domains (Git builds and installations), for explicit validation branches, and for
/// the pipeline that turns an escaping domain exception into the same response.
/// </summary>
internal static class ProblemMappingChecks
{
    internal static async Task RunAsync()
    {
        Verify(new GitBuildProblem("git-build.not-found", 404), "git-build.not-found", 404);
        Verify(new InstallationException("installation.permission_denied", 403), "installation.permission_denied", 403);
        Verify(new InstallationException("runtime.publication_required", 503), "runtime.publication_required", 503);
        Verify(new InstallationCancelledException("install.cancelled"), "install.cancelled", 409);
        Verify(new InstallationInterruptedException("install.interrupted"), "install.interrupted", 409);
        Verify(new ExplicitProblem(), "installation.invalid_request", 400);
        await VerifyPipelineAsync();
        Console.WriteLine("Problem mapping checks passed: 9");
    }

    private static void Verify(IProblemException error, string expectedCode, int expectedStatus)
    {
        TestAssert.Equal(expectedCode, error.ProblemCode, "problemMapping: the exception reports its problem code");
        TestAssert.Equal(expectedStatus, error.StatusCode, "problemMapping: the exception reports its HTTP status");
        var result = ProblemMapping.ToResult(error);
        TestAssert.True(result is Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult,
            "problemMapping: the mapped result is a problem response");
        var problem = ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)result).ProblemDetails;
        TestAssert.Equal(expectedStatus, problem.Status, "problemMapping: status comes from the exception");
        TestAssert.Equal(expectedCode, problem.Title, "problemMapping: title is the problem code");
        TestAssert.Equal("https://relaxkonos.app/problems/" + expectedCode, problem.Type,
            "problemMapping: type URL is derived from the code");
        TestAssert.Equal(expectedCode, problem.Extensions["problemCode"],
            "problemMapping: problemCode extension is preserved for clients");
        Console.WriteLine("PASS problemMapping: " + expectedCode + " -> " + expectedStatus);
    }

    /// <summary>Exercises the real middleware, not a copy of it: an escaping domain exception must
    /// keep the exact problem shape, and an unmapped exception must stay a 500.</summary>
    private static async Task VerifyPipelineAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseServerProblemMapping();
        app.MapGet("/git-problem", () => { throw new GitBuildProblem("git-build.not-found", 404); });
        app.MapGet("/install-problem", () => { throw new InstallationException("runtime.publication_required", 503); });
        app.MapGet("/unmapped", () => { throw new InvalidOperationException("not a problem exception"); });
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        var git = await client.GetAsync("/git-problem");
        TestAssert.Equal(HttpStatusCode.NotFound, git.StatusCode, "problemMapping: middleware maps a Git build failure");
        using (var body = JsonDocument.Parse(await git.Content.ReadAsStringAsync()))
        {
            TestAssert.Equal("git-build.not-found", body.RootElement.GetProperty("problemCode").GetString(),
                "problemMapping: middleware keeps the problemCode extension");
            TestAssert.Equal("https://relaxkonos.app/problems/git-build.not-found",
                body.RootElement.GetProperty("type").GetString(), "problemMapping: middleware keeps the type URL");
        }
        Console.WriteLine("PASS problemMapping: pipeline maps git-build.not-found -> 404");

        var installation = await client.GetAsync("/install-problem");
        TestAssert.Equal(HttpStatusCode.ServiceUnavailable, installation.StatusCode,
            "problemMapping: middleware maps an installation failure");
        using (var body = JsonDocument.Parse(await installation.Content.ReadAsStringAsync()))
        {
            TestAssert.Equal("runtime.publication_required", body.RootElement.GetProperty("problemCode").GetString(),
                "problemMapping: installation code survives the pipeline");
        }
        Console.WriteLine("PASS problemMapping: pipeline maps runtime.publication_required -> 503");

        var unmapped = await client.GetAsync("/unmapped");
        TestAssert.Equal(HttpStatusCode.InternalServerError, unmapped.StatusCode,
            "problemMapping: an exception without a problem code stays a 500");
        Console.WriteLine("PASS problemMapping: unmapped exception stays 500");

        await app.StopAsync();
    }

    /// <summary>A validation branch that returns a problem instead of throwing one.</summary>
    private sealed class ExplicitProblem : Exception, IProblemException
    {
        public string ProblemCode => "installation.invalid_request";

        public int StatusCode => 400;
    }
}
