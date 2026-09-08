using RelaxKonOS.Protocol.Proxy;
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
    IProxyGeoDataService? geoData = null,
    IProxySettingsService? settingsService = null) : IProxyConfigurationTransactionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
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
        if (engine.EngineId == MihomoEngine.Id && geoData is not null)
        {
            var geoProblem = await geoData.EnsureBundledAsync(cancellationToken);
            if (!string.IsNullOrEmpty(geoProblem)) return geoProblem;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
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
                var validation = await engine.ValidateConfigurationAsync(temporary, cancellationToken);
                if (!string.IsNullOrEmpty(validation)) { File.Delete(temporary); return validation; }
                File.Move(temporary, ProfilePath(profileId), overwrite: true); SetPrivateFile(ProfilePath(profileId));
                return null;
            }
            catch (IOException) { return ProxyProblemCodes.ConfigApplyFailed; }
            catch (UnauthorizedAccessException) { return ProxyProblemCodes.PrivilegedOperationUnavailable; }
            catch (ArgumentException) { return ProxyProblemCodes.ConfigInvalid; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
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
            var yaml = await File.ReadAllTextAsync(storedPath, cancellationToken);
            var directory = paths.GetProtectedConfigurationDirectory(); Directory.CreateDirectory(directory); SetPrivateDirectory(directory);
            var secret = await controllerSecrets.GetOrCreateAsync(cancellationToken);
            var settings = settingsService is null
                ? new ProxySettingsDto(false, false, true, true, false, "warning", 7890, false, "127.0.0.1", ProxyTunSettingsDto.Default)
                : await settingsService.GetAsync(cancellationToken);
            var managedYaml = engine.EngineId == MihomoEngine.Id
                ? MihomoManagedConfiguration.WithServerControllerSettings(
                    MihomoManagedConfiguration.WithRuntimeSettings(
                        MihomoManagedConfiguration.WithManagedTunSettings(
                            MihomoManagedConfiguration.WithServerGeoDataSettings(yaml), settings), settings), controllerOptions, secret)
                : yaml;
            var active = Path.Combine(directory, "active.yaml");
            var temporary = Path.Combine(directory, ".apply-" + Guid.NewGuid().ToString("N"));
            var backup = Path.Combine(directory, "backup-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + ".yaml");
            try
            {
                await File.WriteAllTextAsync(temporary, managedYaml, cancellationToken); SetPrivateFile(temporary);
                var validation = await engine.ValidateConfigurationAsync(temporary, cancellationToken);
                if (!string.IsNullOrEmpty(validation)) return validation;
                if (File.Exists(active)) File.Copy(active, backup, overwrite: false);
                File.Move(temporary, active, overwrite: true); SetPrivateFile(active);
                var reload = await engine.ReloadAsync(cancellationToken);
                var health = string.IsNullOrEmpty(reload) ? await engine.GetHealthAsync(cancellationToken) : null;
                if (string.IsNullOrEmpty(reload) && health?.State == ProxyHealthState.Healthy) return null;
                if (File.Exists(backup))
                {
                    File.Copy(backup, active, overwrite: true); SetPrivateFile(active);
                    var rollback = await engine.ReloadAsync(cancellationToken);
                    var rollbackHealth = string.IsNullOrEmpty(rollback) ? await engine.GetHealthAsync(cancellationToken) : null;
                    return rollbackHealth?.State == ProxyHealthState.Healthy ? ProxyProblemCodes.ConfigApplyFailed : ProxyProblemCodes.RecoveryRequired;
                }
                return ProxyProblemCodes.ConfigApplyFailed;
            }
            catch (IOException) { return ProxyProblemCodes.ConfigApplyFailed; }
            catch (UnauthorizedAccessException) { return ProxyProblemCodes.PrivilegedOperationUnavailable; }
            catch (ProxyControllerSecretException) { return ProxyProblemCodes.ConfigApplyFailed; }
            catch (ArgumentException) { return ProxyProblemCodes.ConfigInvalid; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
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
