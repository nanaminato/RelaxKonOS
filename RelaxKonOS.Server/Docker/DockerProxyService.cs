using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Docker;

/// <summary>Raised when a saved proxy preference is unusable; the message is a localization key.</summary>
public sealed class DockerProxyValidationException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}

public interface IDockerProxyService
{
    /// <summary>Saved preference, per-layer outcome, and the proxy the daemon actually reports.</summary>
    Task<DockerProxyStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<DockerProxyStatusDto> SaveAsync(SaveDockerProxySettingsRequest request, Guid actorUserId, CancellationToken cancellationToken = default);
    Task<DockerProxyStatusDto> ClearAsync(Guid actorUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the shared outbound-proxy preference and installs its Docker layers. The build layer needs
/// no host change because the Server already controls its own docker child processes; the daemon
/// layer is delegated to <see cref="IDockerEngineProxyConfigurator"/>.
/// </summary>
public sealed class DockerProxyService(
    IDockerProxySettingsRepository settings,
    IDockerProxyResolver resolver,
    IDockerEngineProxyConfigurator configurator,
    IDockerEngineService engine,
    IDockerDesktopProxyReader desktopProxy,
    ILogger<DockerProxyService> logger) : IDockerProxyService
{
    internal DockerComposeExecution Execution { private get; set; } = DockerComposeExecution.Shared;

    public async Task<DockerProxyStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // An explicit host status read must observe a stopped listener or changed Mihomo port,
        // even while download/build consumers still hold the short resolution cache.
        resolver.Invalidate();
        var resolution = await resolver.ResolveAsync(cancellationToken);
        var saved = await ReadSavedAsync(cancellationToken);
        var engineState = await SafeReadEngineProxyAsync(cancellationToken);
        var desktop = ReadDesktopProxy();
        LogReportedProxy(engineState);
        return Describe(resolution, saved, engineState, desktop,
            [BuildLayer(resolution), EngineLayer(resolution, saved, engineState, desktop)]);
    }

    public async Task<DockerProxyStatusDto> SaveAsync(SaveDockerProxySettingsRequest request, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var previous = await ReadSavedAsync(cancellationToken);
        // Saving an unconfirmed replacement must not forget that the old daemon setting is still
        // installed. That marker is what lets a later Clear remove the old setting safely.
        var normalized = Normalize(request, actorUserId);
        normalized.EngineApplied = previous?.EngineApplied == true;
        normalized.EngineProblemCode = previous?.EngineApplied == true && request.Enabled && request.ApplyToEngine && !request.Confirmed
            ? DockerProxyProblem.ConfirmationRequired
            : string.Empty;
        await settings.SaveAsync(normalized, cancellationToken);
        resolver.Invalidate();

        var resolution = await resolver.ResolveAsync(cancellationToken);
        var saved = await ReadSavedAsync(cancellationToken) ?? new DockerProxySetting();
        var layers = new List<DockerProxyLayerDto> { BuildLayer(resolution) };

        // Order matters. A preference that resolves to a problem is reported and the host is left
        // exactly as it was: installing it would either be rejected by the Helper (Linux) or, on
        // Docker Desktop, write manual mode with an empty proxy and restart the daemon.
        if (resolution.ProblemCode.Length > 0)
            layers.Add(new DockerProxyLayerDto(DockerProxyTarget.Engine, DockerProxyLayerState.Failed, resolution.ProblemCode, string.Empty));
        else if (!resolution.Enabled || !resolution.ApplyToEngine)
            layers.Add(await RetireEngineLayerAsync(previous, resolution, cancellationToken));
        else if (!configurator.IsSupported)
            layers.Add(new DockerProxyLayerDto(DockerProxyTarget.Engine, DockerProxyLayerState.Unsupported, DockerProxyProblem.PlatformUnsupported, string.Empty));
        else if (!request.Confirmed)
            // Installing or replacing the daemon proxy restarts Docker and interrupts running
            // containers, so it never happens as a side effect of saving a form.
            layers.Add(new DockerProxyLayerDto(DockerProxyTarget.Engine, DockerProxyLayerState.Failed, DockerProxyProblem.ConfirmationRequired, string.Empty));
        else
            layers.Add(await InstallEngineLayerAsync(resolution, cancellationToken));

        // The freshly installed layer is re-evaluated against the daemon's own read-back, because a
        // written configuration is not yet a live one.
        var engineState = await SafeReadEngineProxyAsync(cancellationToken);
        var desktop = ReadDesktopProxy();
        var reEvaluated = layers.Any(layer => layer.Target == DockerProxyTarget.Engine && layer.State == DockerProxyLayerState.RestartRequired)
            ? EngineLayer(resolution, await ReadSavedAsync(cancellationToken), engineState, desktop)
            : layers.Single(layer => layer.Target == DockerProxyTarget.Engine);
        return Describe(resolution, await ReadSavedAsync(cancellationToken), engineState, desktop,
            [layers.Single(layer => layer.Target == DockerProxyTarget.Build), reEvaluated]);
    }

    /// <summary>
    /// Clearing the preference also removes the daemon configuration, because leaving a stale proxy
    /// behind would keep redirecting pulls with no setting left to explain it.
    /// </summary>
    public async Task<DockerProxyStatusDto> ClearAsync(Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var previous = await ReadSavedAsync(cancellationToken);
        await settings.DeleteAsync(cancellationToken);
        resolver.Invalidate();

        var resolution = await resolver.ResolveAsync(cancellationToken);
        if (previous is null)
            return await GetStatusAsync(cancellationToken);

        var layers = new List<DockerProxyLayerDto>
        {
            BuildLayer(resolution),
            await RetireEngineLayerAsync(previous, resolution, cancellationToken),
        };
        var engineState = await SafeReadEngineProxyAsync(cancellationToken);
        return Describe(resolution, null, engineState, ReadDesktopProxy(), layers);
    }

    private async Task<DockerProxyLayerDto> InstallEngineLayerAsync(DockerProxyResolution resolution, CancellationToken cancellationToken)
    {
        var wasApplied = (await ReadSavedAsync(cancellationToken))?.EngineApplied == true;
        var result = await configurator.ApplyAsync(true, resolution, cancellationToken);
        await RecordEngineOutcomeAsync(result.Success || wasApplied, result.Success ? string.Empty : result.ProblemCode, cancellationToken);
        return new DockerProxyLayerDto(DockerProxyTarget.Engine,
            result.Success ? DockerProxyLayerState.RestartRequired : DockerProxyLayerState.Failed,
            result.Success ? string.Empty : result.ProblemCode, result.Detail);
    }

    /// <summary>
    /// Removes a previously installed daemon configuration. A daemon that was never configured on
    /// this host is left alone, so a save cannot restart Docker for nothing.
    /// </summary>
    private async Task<DockerProxyLayerDto> RetireEngineLayerAsync(DockerProxySetting? previous, DockerProxyResolution resolution, CancellationToken cancellationToken)
    {
        if (previous is null || !previous.EngineApplied)
            return new DockerProxyLayerDto(DockerProxyTarget.Engine, DockerProxyLayerState.Disabled, string.Empty, string.Empty);
        if (!configurator.IsSupported)
            return new DockerProxyLayerDto(DockerProxyTarget.Engine, DockerProxyLayerState.Unsupported, DockerProxyProblem.PlatformUnsupported, string.Empty);

        var result = await configurator.ApplyAsync(false, resolution, cancellationToken);
        await RecordEngineOutcomeAsync(!result.Success && previous.EngineApplied, result.Success ? string.Empty : result.ProblemCode, cancellationToken);
        logger.LogInformation("Docker daemon proxy removal completed. Success={Success}", result.Success);
        return new DockerProxyLayerDto(DockerProxyTarget.Engine,
            result.Success ? DockerProxyLayerState.RestartRequired : DockerProxyLayerState.Failed,
            result.Success ? string.Empty : result.ProblemCode, result.Detail);
    }

    /// <summary>Persists whether a RelaxKonOS-managed daemon setting still exists and its latest outcome.</summary>
    private async Task RecordEngineOutcomeAsync(bool engineApplied, string problemCode, CancellationToken cancellationToken)
    {
        var saved = await ReadSavedAsync(cancellationToken);
        if (saved is null) return;
        saved.EngineApplied = engineApplied;
        saved.EngineProblemCode = problemCode;
        await settings.SaveAsync(saved, cancellationToken);
    }

    private DockerProxyLayerDto BuildLayer(DockerProxyResolution resolution)
    {
        using var lease = Execution.Acquire<IDockerProxyProjection>(DockerComposeExecution.ProxyProjectionEntryType);
        return lease.Executor.BuildLayer(resolution);
    }

    private DockerProxyLayerDto EngineLayer(DockerProxyResolution resolution, DockerProxySetting? saved,
        DockerEngineProxyState? engineState, DockerDesktopProxyDto? desktop)
    {
        using var lease = Execution.Acquire<IDockerProxyProjection>(DockerComposeExecution.ProxyProjectionEntryType);
        return lease.Executor.EngineLayer(resolution, saved?.EngineApplied == true, saved?.EngineProblemCode ?? string.Empty,
            configurator.IsSupported, engineState, desktop);
    }

    private DockerProxyStatusDto Describe(DockerProxyResolution resolution, DockerProxySetting? saved, DockerEngineProxyState? engineState, DockerDesktopProxyDto? desktop, IReadOnlyList<DockerProxyLayerDto> layers) =>
        new(ToDto(saved), layers,
            engineState?.HttpProxy ?? string.Empty, engineState?.HttpsProxy ?? string.Empty, engineState?.NoProxy ?? string.Empty,
            resolution.ManagedProxyEndpoint, resolution.ManagedProxyAvailable, configurator.Platform, desktop);

    /// <summary>
    /// Validates and normalizes the request. The managed source derives its URLs from the proxy
    /// runtime, so a caller cannot point Docker at an arbitrary endpoint through this source.
    /// </summary>
    private static DockerProxySetting Normalize(SaveDockerProxySettingsRequest request, Guid actorUserId)
    {
        _ = DockerProxyValidation.TryNormalize(request.HttpProxy, out var httpProxy);
        _ = DockerProxyValidation.TryNormalize(request.HttpsProxy, out var httpsProxy);
        if (!DockerProxyValidation.TryNormalizeBypassList(request.NoProxy, out var noProxy)
            || !DockerProxyValidation.IsValidBypassList(noProxy))
            throw new DockerProxyValidationException(DockerProxyProblem.ConfigurationInvalid);
        if (request.Enabled && request.Source == DockerProxySource.Custom)
        {
            if (!DockerProxyValidation.IsValidProxyUrl(httpProxy)) throw new DockerProxyValidationException(DockerProxyProblem.ConfigurationInvalid);
            if (httpsProxy.Length > 0 && !DockerProxyValidation.IsValidProxyUrl(httpsProxy)) throw new DockerProxyValidationException(DockerProxyProblem.ConfigurationInvalid);
        }

        var managed = request.Source == DockerProxySource.ManagedProxy;
        return new DockerProxySetting
        {
            Enabled = request.Enabled,
            Source = request.Source,
            // A managed-proxy source stores no URLs of its own: keeping stale custom values would
            // silently resurrect them if the source were switched back.
            HttpProxy = managed ? string.Empty : httpProxy,
            HttpsProxy = managed ? string.Empty : httpsProxy,
            NoProxy = noProxy,
            ApplyToEngine = request.Enabled && request.ApplyToEngine,
            ApplyToBuild = request.Enabled && request.ApplyToBuild,
            ApplyToImageTags = request.Enabled && request.ApplyToImageTags,
            ApplyToRuntimeDownloads = request.Enabled && request.ApplyToRuntimeDownloads,
            // A new preference has not been installed on the host yet, so the previous outcome is
            // discarded: the values it described no longer apply.
            EngineApplied = false,
            EngineProblemCode = string.Empty,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = actorUserId.ToString("D"),
        };
    }

    private static DockerProxySettingsDto ToDto(DockerProxySetting? saved) => saved is null
        ? new DockerProxySettingsDto(false, DockerProxySource.Custom, string.Empty, string.Empty, string.Empty, false, false, false, false)
        : new DockerProxySettingsDto(saved.Enabled, saved.Source, saved.HttpProxy, saved.HttpsProxy, saved.NoProxy,
            saved.ApplyToEngine, saved.ApplyToBuild, saved.ApplyToImageTags, saved.ApplyToRuntimeDownloads);

    private async Task<DockerProxySetting?> ReadSavedAsync(CancellationToken cancellationToken)
    {
        try { return await settings.GetAsync(cancellationToken); }
        catch (DockerProxySecretUnreadableException)
        {
            logger.LogWarning("The stored Docker proxy value cannot be decrypted.");
            return null;
        }
    }

    private async Task<DockerEngineProxyState?> SafeReadEngineProxyAsync(CancellationToken cancellationToken)
    {
        try { return await engine.GetProxyStateAsync(cancellationToken); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Docker Desktop's own stored proxy, when this host has one. The read is a diagnostic side
    /// channel, so a missing or unreadable file never turns a status read into a failure.
    /// </summary>
    private DockerDesktopProxyDto? ReadDesktopProxy()
    {
        try { return desktopProxy.Read(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The Docker Desktop settings file could not be read.");
            return null;
        }
    }

    /// <summary>
    /// The daemon's reported proxy is shown to the operator verbatim, but a log line has a wider
    /// audience than the operator, so anything embedding a credential is masked before it is written.
    /// </summary>
    private void LogReportedProxy(DockerEngineProxyState? state)
    {
        if (state is null || state.HttpProxy.Length == 0 && state.HttpsProxy.Length == 0) return;
        logger.LogDebug("Docker daemon reports a proxy. HttpProxy={HttpProxy} HttpsProxy={HttpsProxy} NoProxy={NoProxy}",
            DockerProxyValidation.MaskProxy(state.HttpProxy),
            DockerProxyValidation.MaskProxy(state.HttpsProxy),
            DockerProxyValidation.MaskProxy(state.NoProxy));
    }
}
