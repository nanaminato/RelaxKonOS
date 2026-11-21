using RelaxKonOS.App.WebServers;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Client.Apps.WebServers;

internal sealed class WebServerAuthorization(IHostElevationBroker broker) : IWebServerAuthorization
{
    internal static string? FormatProblem(string? problemCode) =>
        PrivilegedHelperProblemText.TryFormat(problemCode, out var message) ? message : null;

    public Task<T> ExecuteAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        broker.ExecuteAsync(capability, target, operation, cancellationToken);
}
