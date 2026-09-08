using System.Text.Json;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.AppSettings;

/// <summary>Isolation boundary for an application's persisted configuration.</summary>
public enum AppSettingsScope
{
    User,
    Workspace,
    Device,
}

/// <summary>Routes for application-private, server-persisted configuration documents.</summary>
public static class AppSettingsApiRoutes
{
    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;

    /// <summary>GET/PUT one configuration document. App id, scope, and key are path parameters.</summary>
    public const string Document = $"/{V1}/app-settings/{{appId}}/{{scope}}/{{key}}";

    /// <summary>Deletes every private settings document for one app in the authenticated user's tenant.</summary>
    public const string Application = $"/{V1}/app-settings/{{appId}}";
}

/// <summary>A versioned JSON configuration document owned by one application.</summary>
public sealed record AppSettingsDocumentDto(
    AppSettingsScope Scope,
    string Key,
    JsonElement Value,
    int SchemaVersion,
    long Revision,
    DateTimeOffset UpdatedAt);

/// <summary>Replacement value for an application configuration document.</summary>
public sealed record PutAppSettingsRequest(JsonElement Value, int SchemaVersion = 1);
