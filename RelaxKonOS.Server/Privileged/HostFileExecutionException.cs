namespace RelaxKonOS.Server.Privileged;

/// <summary>A privileged file attempt failed after authorization. It must not be turned into a
/// fresh password challenge by the ordinary user-execution endpoint.</summary>
public sealed class HostFileExecutionException(int statusCode, string problemCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string ProblemCode { get; } = problemCode;

    public static HostFileExecutionException From(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => new(404, "not-found", "Privileged file path was not found."),
        UnauthorizedAccessException => new(403, "access-denied", "Privileged Helper denied this file operation."),
        ArgumentException => new(400, "invalid-path", "Invalid privileged file target."),
        InvalidOperationException => new(503, "privileged-helper-unavailable", "Privileged Helper is unavailable."),
        TimeoutException => new(503, "privileged-helper-unavailable", "Privileged Helper timed out."),
        _ => new(500, "io-error", "Privileged file operation failed."),
    };
}
