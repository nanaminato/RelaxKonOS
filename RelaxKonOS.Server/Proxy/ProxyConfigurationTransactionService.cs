using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Serializes raw-YAML application. Raw configuration is never returned through public contracts.</summary>
public interface IProxyConfigurationTransactionService
{
    Task<string?> StoreAsync(Guid profileId, string yaml, CancellationToken cancellationToken);
    Task<string?> ReadStoredAsync(Guid profileId, CancellationToken cancellationToken);
    Task<string?> ActivateStoredAsync(Guid profileId, CancellationToken cancellationToken);
    Task<string?> ApplyAsync(Guid profileId, string yaml, CancellationToken cancellationToken);
}

public sealed class ProxyConfigurationTransactionService(
    IProxyPlatformPaths paths,
    IProxyEngineRegistry engines,
    IProxyProfileRepository profiles,
    IProxyControllerSecretStore controllerSecrets,
    MihomoControllerOptions controllerOptions,
    MihomoPublicationStore publication,
    IProxyGeoDataService? geoData = null,
    IProxySettingsService? settingsService = null,
    ILogger<ProxyConfigurationTransactionService>? logger = null,
    MihomoConfigurationPublicationService? managedPublication = null,
    MihomoRuntimeService? managedRuntime = null) : IProxyConfigurationTransactionService
{
    private SemaphoreSlim _gate => publication.Gate;
    public async Task<string?> ApplyAsync(Guid profileId, string yaml, CancellationToken cancellationToken)
    {
        var stored = await StoreAsync(profileId, yaml, cancellationToken);
        return string.IsNullOrEmpty(stored) ? await ActivateStoredAsync(profileId, cancellationToken) : stored;
    }

    public async Task<string?> StoreAsync(Guid profileId, string yaml, CancellationToken cancellationToken)
    {
        if (yaml.Length is 0 or > 1_048_576 || yaml.IndexOf('\0') >= 0) return ProxyProblemCodes.ConfigInvalid;
        var profile = await profiles.GetAsync(profileId, cancellationToken);
        var engine = profile is null ? null : engines.Find(profile.EngineId);
        if (engine is null) return ProxyProblemCodes.NotSupported;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await publication.ReadAsync(cancellationToken);
            if (state.Pending is not null) return ProxyProblemCodes.RecoveryRequired;
            var managed = engine.EngineId == MihomoEngine.Id && state.Published is not null;
            if (!managed && await publication.UncoordinatedWriteProblemAsync(cancellationToken) is { } publicationProblem) return publicationProblem;
            if (!managed && engine.EngineId == MihomoEngine.Id && geoData is not null)
            {
                var geoProblem = await geoData.EnsureBundledAsync(cancellationToken);
                if (!string.IsNullOrEmpty(geoProblem)) return geoProblem;
            }
            var directory = GetProfilesDirectory();
            var temporary = Path.Combine(directory, ".validate-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Validate the same GEO policy that will be activated.  Validating the raw
                // subscription first lets a profile's geo-auto-update/geox-url make Mihomo
                // download data even though the managed -d directory has already been staged.
                // Keep the original profile for later activation, but never let its download
                // settings influence the validation process.
                var validationYaml = engine.EngineId == MihomoEngine.Id
                    ? MihomoManagedConfiguration.WithServerGeoDataSettings(yaml)
                    : yaml;
                await File.WriteAllTextAsync(temporary, validationYaml, cancellationToken); SetPrivateFile(temporary);
                // Drafts are structurally bounded here. The installed target validates natively only
                // inside the protected configuration transaction, never against live runtime data.
                var validation = managed ? await ValidateManagedDraftAsync(yaml, state.Published!, cancellationToken)
                    : await engine.ValidateConfigurationAsync(temporary, cancellationToken);
                if (!string.IsNullOrEmpty(validation))
                {
                    logger?.LogWarning("Proxy profile storage was rejected by configuration validation. ProfileId={ProfileId} EngineId={EngineId} ProblemCode={ProblemCode}", profileId, engine.EngineId, validation);
                    File.Delete(temporary); return validation;
                }
                File.Move(temporary, ProfilePath(profileId), overwrite: true); SetPrivateFile(ProfilePath(profileId));
                logger?.LogInformation("Proxy profile configuration stored. ProfileId={ProfileId} EngineId={EngineId} Characters={Characters}", profileId, engine.EngineId, yaml.Length);
                return null;
            }
            catch (RuntimePolicyException) { return ProxyProblemCodes.ConfigInvalid; }
            catch (IOException) { return ProxyProblemCodes.ConfigApplyFailed; }
            catch (UnauthorizedAccessException) { return ProxyProblemCodes.PrivilegedOperationUnavailable; }
            catch (ArgumentException) { return ProxyProblemCodes.ConfigInvalid; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (RuntimePolicyException) { return ProxyProblemCodes.RecoveryRequired; }
        catch (IOException) { return ProxyProblemCodes.RecoveryRequired; }
        catch (UnauthorizedAccessException) { return ProxyProblemCodes.PrivilegedOperationUnavailable; }
        finally { _gate.Release(); }
    }

    /// <summary>Reads the last imported profile content without contacting its subscription source.</summary>
    public async Task<string?> ReadStoredAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var storedPath = ProfilePath(profileId);
        if (!File.Exists(storedPath)) return null;
        try { return await File.ReadAllTextAsync(storedPath, cancellationToken); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<string?> ActivateStoredAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(profileId, cancellationToken);
        var engine = profile is null ? null : engines.Find(profile.EngineId);
        var storedPath = ProfilePath(profileId);
        if (engine is null || !File.Exists(storedPath)) return ProxyProblemCodes.ConfigInvalid;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await publication.ReadAsync(cancellationToken);
            if (state.Pending is not null) return ProxyProblemCodes.RecoveryRequired;
            if (engine.EngineId == MihomoEngine.Id && state.Published is not null)
                return await ActivateManagedUnderGateAsync(profileId, storedPath, state, cancellationToken);
            if (await publication.UncoordinatedWriteProblemAsync(cancellationToken) is { } publicationProblem) return publicationProblem;
            var yaml = await File.ReadAllTextAsync(storedPath, cancellationToken);
            var directory = paths.GetProtectedConfigurationDirectory(); Directory.CreateDirectory(directory); SetPrivateDirectory(directory);
            var active = Path.Combine(directory, "active.yaml");
            // Activating a profile rewrites the whole file, but TUN activation is session state
            // owned by the safety transaction rather than by the profile.  Carry the live
            // activation and its route-exclusion boundary across, exactly as a settings write does.
            var tunActivation = File.Exists(active)
                ? MihomoManagedConfiguration.ReadTunActivation(await File.ReadAllTextAsync(active, cancellationToken))
                : new MihomoTunActivation(false, []);
            var secret = await controllerSecrets.GetOrCreateAsync(cancellationToken);
            var settings = settingsService is null
                ? new ProxySettingsDto(false, false, true, true, false, "warning", 7890, false, "127.0.0.1", ProxyTunSettingsDto.Default)
                : await settingsService.GetAsync(cancellationToken);
            var managedYaml = engine.EngineId == MihomoEngine.Id
                ? MihomoManagedConfiguration.WithServerControllerSettings(
                    MihomoManagedConfiguration.WithRuntimeSettings(
                        MihomoManagedConfiguration.WithManagedTunSettings(
                            MihomoManagedConfiguration.WithServerGeoDataSettings(yaml), settings,
                            tunActivation.Enabled, tunActivation.RouteExclusions), settings), controllerOptions, secret)
                : yaml;
            var temporary = Path.Combine(directory, ".apply-" + Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(directory, "backup-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".yaml");
            try
            {
                await File.WriteAllTextAsync(temporary, managedYaml, cancellationToken); SetPrivateFile(temporary);
                var validation = await engine.ValidateConfigurationAsync(temporary, cancellationToken);
                if (!string.IsNullOrEmpty(validation))
                {
                    logger?.LogWarning("Proxy configuration activation was rejected by validation. ProfileId={ProfileId} EngineId={EngineId} ProblemCode={ProblemCode}", profileId, engine.EngineId, validation);
                    return validation;
                }
                if (File.Exists(active)) File.Copy(active, backup, overwrite: false);
                File.Move(temporary, active, overwrite: true); SetPrivateFile(active);
                var reload = await engine.ReloadAsync(cancellationToken);
                var health = string.IsNullOrEmpty(reload) ? await engine.GetHealthAsync(cancellationToken) : null;
                if (string.IsNullOrEmpty(reload) && health?.State == ProxyHealthState.Healthy)
                {
                    if (await profiles.SetActiveAsync(profileId, cancellationToken) is null)
                        return ProxyProblemCodes.RecoveryRequired;
                    logger?.LogInformation("Proxy configuration activated. ProfileId={ProfileId} EngineId={EngineId} BackupCreated={BackupCreated}", profileId, engine.EngineId, File.Exists(backup));
                    return null;
                }
                if (File.Exists(backup))
                {
                    File.Copy(backup, active, overwrite: true); SetPrivateFile(active);
                    var rollback = await engine.ReloadAsync(cancellationToken);
                    var rollbackHealth = string.IsNullOrEmpty(rollback) ? await engine.GetHealthAsync(cancellationToken) : null;
                    var outcome = rollbackHealth?.State == ProxyHealthState.Healthy ? ProxyProblemCodes.ConfigApplyFailed : ProxyProblemCodes.RecoveryRequired;
                    logger?.LogWarning("Proxy configuration activation failed and rollback was attempted. ProfileId={ProfileId} EngineId={EngineId} Outcome={Outcome}", profileId, engine.EngineId, outcome);
                    return outcome;
                }
                return ProxyProblemCodes.ConfigApplyFailed;
            }
            catch (IOException) { return ProxyProblemCodes.ConfigApplyFailed; }
            catch (UnauthorizedAccessException) { return ProxyProblemCodes.PrivilegedOperationUnavailable; }
            catch (ProxyControllerSecretException) { return ProxyProblemCodes.ConfigApplyFailed; }
            catch (ArgumentException) { return ProxyProblemCodes.ConfigInvalid; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (ArgumentException) { return ProxyProblemCodes.ConfigInvalid; }
        catch (RuntimePolicyException) { return ProxyProblemCodes.RecoveryRequired; }
        catch (IOException) { return ProxyProblemCodes.RecoveryRequired; }
        catch (UnauthorizedAccessException) { return ProxyProblemCodes.PrivilegedOperationUnavailable; }
        finally { _gate.Release(); }
    }

    private async Task<string?> ValidateManagedDraftAsync(string yaml, MihomoPublishedRuntime previous, CancellationToken ct)
    {
        if (settingsService is null) return ProxyProblemCodes.NotSupported;
        var settings = await settingsService.GetAsync(ct);
        var candidate = MihomoManagedConfiguration.WithServerControllerSettings(
            MihomoManagedConfiguration.WithRuntimeSettings(
                MihomoManagedConfiguration.WithServerGeoDataSettings(yaml), settings), controllerOptions, previous.ControllerSecret);
        var description = MihomoConfigurationPolicy.Describe(candidate);
        return description.ResourcePaths.Count != 0 || description.Material.NetworkTransitionRequired
            ? ProxyProblemCodes.NotSupported : null;
    }

    private async Task<string?> ActivateManagedUnderGateAsync(Guid profileId, string storedPath,
        MihomoPublicationState state, CancellationToken ct)
    {
        if (managedPublication is null || managedRuntime is null) return ProxyProblemCodes.NotSupported;
        try
        {
            var previous = state.Published!;
            var secret = await controllerSecrets.ReadExistingAsync(ct);
            if (secret is null || secret != previous.ControllerSecret) return ProxyProblemCodes.RecoveryRequired;
            var settings = settingsService is null ? null : await settingsService.GetAsync(ct);
            if (settings is null) return ProxyProblemCodes.NotSupported;
            var yaml = await File.ReadAllTextAsync(storedPath, ct);
            var activation = MihomoManagedConfiguration.ReadTunActivation(previous.Yaml);
            yaml = MihomoManagedConfiguration.WithServerControllerSettings(
                MihomoManagedConfiguration.WithRuntimeSettings(
                    MihomoManagedConfiguration.WithManagedTunSettings(
                        MihomoManagedConfiguration.WithServerGeoDataSettings(yaml), settings,
                        activation.Enabled, activation.RouteExclusions), settings), controllerOptions, secret);
            var description = MihomoConfigurationPolicy.Describe(yaml);
            // Resource distribution and network transitions require dedicated transactions.
            if (description.ResourcePaths.Count != 0 || description.Material.NetworkTransitionRequired)
                return ProxyProblemCodes.NotSupported;
            var observed = await managedRuntime.QueryHelperAsync(ct);
            if (observed.Installation.Ownership != RuntimeOwnership.Managed || observed.ActiveRevisionId is null
                || observed.ConfigurationIdentity != state.Identity || observed.PendingOperationId is not null
                || observed.Installation.RecoveryRequired) return ProxyProblemCodes.RecoveryRequired;
            var operationId = Guid.NewGuid();
            var plan = new RuntimeConfigurationPlan(observed.Installation.InstallationId, observed.Installation.HostId,
                "mihomo", observed.Installation.StateRevision, observed.ActiveRevisionId.Value, state.Identity!,
                MihomoPublicationStore.Identity(yaml), operationId.ToString("N"));
            var profile = await profiles.GetAsync(profileId, ct);
            if (profile is null || profile.EngineId != MihomoEngine.Id) return ProxyProblemCodes.ConfigInvalid;
            var active = (await profiles.ListAsync(ct)).SingleOrDefault(item => item.IsActive);
            var selection = new MihomoProfilePublication(profile.Id, profile.Revision, active?.Id, active?.Revision, profile.CreatedAt, active?.CreatedAt);
            var result = await managedPublication.PublishUnderGateAsync(operationId, state.Revision, plan, description.Material, ct, profile: selection);
            if (result.Outcome != MihomoPublicationOutcome.Confirmed)
                return result.Outcome == MihomoPublicationOutcome.RecoveryRequired
                    ? ProxyProblemCodes.RecoveryRequired : ProxyProblemCodes.ConfigApplyFailed;
            return null; // Selection and its SQLite operation marker were reconciled before authority confirmation.
        }
        catch (RuntimePolicyException) { return ProxyProblemCodes.RecoveryRequired; }
        catch (RuntimeLifecycleException) { return ProxyProblemCodes.RecoveryRequired; }
        catch (ProxyControllerSecretException) { return ProxyProblemCodes.RecoveryRequired; }
        catch (IOException) { return ProxyProblemCodes.RecoveryRequired; }
        catch (UnauthorizedAccessException) { return ProxyProblemCodes.PrivilegedOperationUnavailable; }
    }

    private string GetProfilesDirectory()
    {
        var directory = Path.Combine(paths.GetProtectedConfigurationDirectory(), "profiles");
        Directory.CreateDirectory(directory); SetPrivateDirectory(directory);
        return directory;
    }
    private string ProfilePath(Guid profileId) => Path.Combine(GetProfilesDirectory(), profileId.ToString("N") + ".yaml");
    private static void SetPrivateDirectory(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    private static void SetPrivateFile(string path) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
}

public sealed class ProxyProfileService(IProxyProfileRepository profiles) : IProxyProfileService
{
    public Task<IReadOnlyList<ProxyProfileDto>> ListAsync(CancellationToken cancellationToken) => profiles.ListAsync(cancellationToken);
    public Task<ProxyProfileDto?> GetAsync(Guid profileId, CancellationToken cancellationToken) => profiles.GetAsync(profileId, cancellationToken);
}

public sealed class ProxyConfigurationService : IProxyConfigurationService
{
    // Public configuration validation/apply endpoints are introduced only in Goal 6.
    public Task<string?> ValidateAsync(Guid profileId, CancellationToken cancellationToken) => Task.FromResult<string?>(ProxyProblemCodes.NotSupported);
}
