using System.Text.Json;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.ManagedRuntimes;

public sealed class RuntimeCatalogException(string code) : Exception(code)
{
    public string ProblemCode { get; } = code;
}

/// <summary>Server never provisions Helper trust through the runtime download/install boundary.</summary>
public sealed class RuntimeCatalogService(IPrivilegedOperationTransport transport, IRuntimeDownloadProxyProvider downloadProxy)
{
    public Task<RuntimeSourceView[]> SourcesAsync(CancellationToken ct) =>
        ExecuteAsync<RuntimeSourceView[]>(new(PrivilegedOperationKind.RuntimeSources), ct);

    public async Task<RuntimeCatalogSnapshot> CatalogAsync(string sourceId, bool refresh, bool includePrerelease, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 128
            || sourceId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new RuntimeCatalogException("runtime.source_invalid");
        // Only a refresh opens a socket, so only a refresh can carry the host's download route.
        var result = await ExecuteAsync<RuntimeCatalogSnapshot>(new(refresh ? PrivilegedOperationKind.RuntimeCatalogRefresh
            : PrivilegedOperationKind.RuntimeCatalogGet, RuntimeSourceId: sourceId,
            DownloadProxy: refresh ? await downloadProxy.ResolveAsync(ct) : null), ct);
        return !includePrerelease && result.Catalog is { } catalog
            ? result with { Catalog = catalog with { Releases = catalog.Releases.Where(r => r.Channel == RuntimeChannel.Stable).ToArray() } }
            : result;
    }

    private async Task<T> ExecuteAsync<T>(PrivilegedOperationRequest request, CancellationToken ct)
    {
        var result = await transport.ExecuteAsync(request, ct);
        if (!result.Success)
        {
            var code = result.ProblemCode is PrivilegedProblemCode.HelperUnavailable or PrivilegedProblemCode.TimedOut
                ? "runtime.helper_unavailable" : AllowedProblem(result.Error) ? result.Error! : "runtime.source_store_unavailable";
            throw new RuntimeCatalogException(code);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64 ?? ""))
                ?? throw new RuntimeCatalogException("runtime.helper_response_invalid");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        { throw new RuntimeCatalogException("runtime.helper_response_invalid"); }
    }

    // Keep arbitrary local diagnostics out of HTTP responses.
    private static bool AllowedProblem(string? code) => code is "runtime.source_not_found" or "runtime.source_store_busy"
        or "runtime.source_invalid" or "runtime.source_disabled" or "runtime.source_store_unavailable";
}
