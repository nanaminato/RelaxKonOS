using Microsoft.AspNetCore.Http;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>
/// The one place that decides what an API problem response looks like. Domain exceptions implement
/// <see cref="IProblemException"/> and endpoints may return <see cref="Create"/> for validation
/// branches that have no exception to throw; both produce the same status, title, type URL and
/// <c>problemCode</c> extension.
/// </summary>
public static class ProblemMapping
{
    private const string ProblemBase = "https://relaxkonos.app/problems/";

    /// <summary>Maps a domain exception to the shared problem shape.</summary>
    public static IResult ToResult(IProblemException error) => Create(error.ProblemCode, error.StatusCode, error.Detail);

    /// <summary>Problem for a validation branch that returns instead of throwing.</summary>
    public static IResult Create(string code, int status, string? detail = null) => Results.Problem(
        detail: detail,
        statusCode: status,
        title: code,
        type: ProblemBase + code,
        extensions: new Dictionary<string, object?> { ["problemCode"] = code });

    /// <summary>
    /// The pipeline form: writes the same response an endpoint result would. Used by the exception
    /// mapping middleware so a failure raised inside a service keeps the public contract.
    /// </summary>
    internal static async Task WriteAsync(HttpContext context, IProblemException error)
    {
        context.Response.Clear();
        await ToResult(error).ExecuteAsync(context);
    }
}
