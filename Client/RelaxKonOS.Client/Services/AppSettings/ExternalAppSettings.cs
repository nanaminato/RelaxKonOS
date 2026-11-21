using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.AppSettings;

namespace RelaxKonOS.Client.Services.AppSettings;

public sealed class ExternalAppSettings(AppId appId, IAppSettingsClient client) : IExternalAppSettings
{
    public async Task<ExternalAppSettingsDocument?> GetAsync(
        ExternalAppSettingsScope scope = ExternalAppSettingsScope.Workspace,
        string key = "default", CancellationToken cancellationToken = default)
    {
        var document = await client.GetAsync(appId.Value, ToProtocolScope(scope), key, cancellationToken);
        return document is null ? null : ToExternal(document);
    }

    public async Task<ExternalAppSettingsDocument> SetAsync(
        System.Text.Json.JsonElement value, int schemaVersion = 1,
        ExternalAppSettingsScope scope = ExternalAppSettingsScope.Workspace,
        string key = "default", long? expectedRevision = null, CancellationToken cancellationToken = default)
        => ToExternal(await client.SaveAsync(appId.Value, ToProtocolScope(scope), key, value,
            schemaVersion, expectedRevision, cancellationToken));

    private static AppSettingsScope ToProtocolScope(ExternalAppSettingsScope scope) => scope switch
    {
        ExternalAppSettingsScope.User => AppSettingsScope.User,
        ExternalAppSettingsScope.Workspace => AppSettingsScope.Workspace,
        ExternalAppSettingsScope.Device => AppSettingsScope.Device,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static ExternalAppSettingsDocument ToExternal(AppSettingsDocumentDto document) => new(
        document.Scope switch
        {
            AppSettingsScope.User => ExternalAppSettingsScope.User,
            AppSettingsScope.Workspace => ExternalAppSettingsScope.Workspace,
            AppSettingsScope.Device => ExternalAppSettingsScope.Device,
            _ => throw new ArgumentOutOfRangeException(nameof(document)),
        },
        document.Key, document.Value, document.SchemaVersion, document.Revision, document.UpdatedAt);
}
