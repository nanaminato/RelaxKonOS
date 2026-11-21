namespace RelaxKonOS.AppSDK;

public sealed class ProxyRequestException(string problemCode, int statusCode = 0)
    : HttpRequestException(problemCode, null, statusCode == 0 ? null : (System.Net.HttpStatusCode)statusCode)
{
    public string ProblemCode { get; } = problemCode;
}
