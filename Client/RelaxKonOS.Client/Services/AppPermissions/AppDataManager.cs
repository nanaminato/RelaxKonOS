using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Client.Services.AppPermissions;

/// <summary>User-selected parts of an application's data reset. Local app data is always reset.</summary>
public sealed record AppDataClearOptions(bool ClearPermissionDecisions, bool ClearServerData);

/// <summary>Outcome reported to Settings after an application-data reset.</summary>
public sealed record AppDataClearResult(bool PermissionDecisionsCleared, bool ServerDataCleared);

/// <summary>Host-owned lifecycle service for data that belongs to one application id.</summary>
public interface IAppDataManager
{
    Task<AppDataClearResult> ClearAsync(AppId appId, AppDataClearOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Clears the standard per-app local-data directory and, when selected, the app's local permission
/// decisions and all of its server-private settings for the current user. Package binaries are not
/// application data and are therefore left to the separate uninstall workflow.
/// </summary>
public sealed class AppDataManager : IAppDataManager
{
    private readonly IAppPermissionManager _permissions;
    private readonly IAppSettingsClient _settings;
    private readonly string _localDataRoot;

    public AppDataManager(IAppPermissionManager permissions, IAppSettingsClient settings)
    {
        _permissions = permissions;
        _settings = settings;
        _localDataRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS", "app-data"));
    }

    public async Task<AppDataClearResult> ClearAsync(
        AppId appId, AppDataClearOptions options, CancellationToken cancellationToken = default)
    {
        ClearLocalData(appId);
        if (options.ClearPermissionDecisions)
            _permissions.Clear(appId);
        if (options.ClearServerData)
            await _settings.ClearAsync(appId.Value, cancellationToken);

        return new AppDataClearResult(options.ClearPermissionDecisions, options.ClearServerData);
    }

    private void ClearLocalData(AppId appId)
    {
        var root = _localDataRoot + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(_localDataRoot, appId.Value));
        if (!path.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid application data path.");
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
