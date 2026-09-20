using System.Text.Json;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// Read-only tag lookup for Docker Hub's public catalogue. Registry V2 does not define a portable
/// "most recently updated" ordering, whereas Docker Hub does; private and other registries remain
/// manually enterable rather than being probed with an operator's credentials.
/// </summary>
internal sealed class ApplicationDeploymentImageTagCatalog(IOutboundProxyHttpClientFactory clients, ILogger<ApplicationDeploymentImageTagCatalog> logger)
{
    private const int MaximumTags = 20;

    public async Task<ApplicationImageTagsDto> ListAsync(string repositoryReference, CancellationToken cancellationToken)
    {
        if (!TryParseDockerHub(repositoryReference, out var repository, out var dockerHubPath))
            return new(ApplicationDeploymentValidation.IsValidImageReference(repositoryReference)
                ? StripTag(repositoryReference.Trim()) : string.Empty, [], false);

        try
        {
            using var client = await clients.CreateAsync(OutboundProxyTarget.ImageTags, TimeSpan.FromSeconds(10), cancellationToken);
            client.BaseAddress = new Uri("https://hub.docker.com/");
            var requestPath = $"v2/repositories/{Uri.EscapeDataString(dockerHubPath.Namespace)}/{Uri.EscapeDataString(dockerHubPath.Name)}/tags?page_size={MaximumTags}&ordering=last_updated";
            var destination = new Uri(client.BaseAddress!, requestPath);
            logger.LogInformation("Application image-tag lookup started. Repository={Repository} Destination={Destination}",
                repository, destination.GetLeftPart(UriPartial.Authority));
            using var response = await client.GetAsync(requestPath, cancellationToken);
            logger.LogInformation("Application image-tag lookup received a response. Repository={Repository} StatusCode={StatusCode}",
                repository, (int)response.StatusCode);
            if (!response.IsSuccessStatusCode) return new(repository, [], false);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return new(repository, [], false);

            var tags = results.EnumerateArray()
                .Select(item => item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null)
                .Where(tag => tag is { Length: > 0 })
                .Select(tag => tag!)
                .Select(tag => new ApplicationImageTagDto(tag, $"{repository}:{tag}"))
                .Where(tag => ApplicationDeploymentValidation.IsValidImageReference(tag.ImageReference)
                    && ApplicationDeploymentValidation.IsPinnedImageReference(tag.ImageReference))
                .Take(MaximumTags)
                .ToArray();
            return new(repository, tags, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            logger.LogWarning("Application image-tag lookup failed. Repository={Repository} Failure={Failure}",
                repository, exception.GetType().Name);
            return new(repository, [], false);
        }
    }

    /// <summary>Parses only Docker Hub references; no user-supplied registry URL is contacted.</summary>
    private static bool TryParseDockerHub(string? value, out string repository, out (string Namespace, string Name) path)
    {
        repository = string.Empty;
        path = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = StripTag(value.Trim());
        if (!ApplicationDeploymentValidation.IsValidImageReference(candidate)) return false;

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        var explicitRegistry = segments[0].Contains('.') || segments[0].Contains(':') || segments[0].Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (explicitRegistry && !segments[0].Equals("docker.io", StringComparison.OrdinalIgnoreCase)
            && !segments[0].Equals("index.docker.io", StringComparison.OrdinalIgnoreCase)) return false;
        var names = explicitRegistry ? segments[1..] : segments;
        if (names.Length == 0 || names.Length > 2) return false;
        var dockerHubNamespace = names.Length == 1 ? "library" : names[0];
        var dockerHubName = names[^1];
        repository = candidate;
        path = (dockerHubNamespace, dockerHubName);
        return true;
    }

    private static string StripTag(string value)
    {
        var separator = value.LastIndexOf(':');
        return separator > value.LastIndexOf('/') ? value[..separator] : value;
    }

}
