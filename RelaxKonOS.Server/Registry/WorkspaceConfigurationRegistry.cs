using System.Text.Json;
using RelaxKonOS.Protocol.Browser;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.ConfigurationRegistry;

/// <summary>Workspace presentation settings live exclusively in the configuration registry.</summary>
public static class WorkspaceConfigurationRegistry
{
    public const string TerminalPath = "Workspace\\Terminal";
    public const string DesktopPath = "Workspace\\Desktop";
    public const string BrowserPath = "Workspace\\Browser";
    public const string WindowManagerPath = "Workspace\\WindowManager";
    public const string DefaultValueName = "(Default)";

    public static void EnsureDefaults(IRegistryRepository registry, Workspace workspace, string updatedBy = "system")
    {
        Ensure(registry, workspace, TerminalPath, TerminalSettingsDto.Default, updatedBy);
        Ensure(registry, workspace, DesktopPath, WorkspacePreferencesDto.Default, updatedBy);
        Ensure(registry, workspace, BrowserPath, BrowserSettingsDto.Default, updatedBy);
        Ensure(registry, workspace, WindowManagerPath, WorkspaceWindowLayoutDto.Default, updatedBy);
    }

    public static T Read<T>(IRegistryRepository registry, Workspace workspace, string path, T defaultValue)
    {
        var entry = registry.Find(workspace.UserId, RegistryScope.Workspace, workspace.Id, path, DefaultValueName);
        if (entry is not null)
        {
            try
            {
                var value = JsonSerializer.Deserialize<T>(entry.ValueJson, RelaxKonOSJsonOptions.Default);
                if (value is not null) return value;
            }
            catch (JsonException) { }
        }
        Write(registry, workspace, path, defaultValue, "system");
        return defaultValue;
    }

    public static void Write<T>(IRegistryRepository registry, Workspace workspace, string path, T value, string updatedBy)
    {
        registry.Upsert(new RegistryEntry
        {
            UserId = workspace.UserId, Scope = RegistryScope.Workspace, ScopeId = workspace.Id,
            Path = path, Name = DefaultValueName, ValueType = RegistryValueType.Json,
            ValueJson = JsonSerializer.Serialize(value, RelaxKonOSJsonOptions.Default),
            DesiredUpdatedAt = DateTimeOffset.UtcNow, DesiredUpdatedBy = updatedBy,
        });
    }

    private static void Ensure<T>(IRegistryRepository registry, Workspace workspace, string path, T value, string updatedBy)
    {
        registry.CompareExchange(new RegistryEntry
        {
            UserId = workspace.UserId, Scope = RegistryScope.Workspace, ScopeId = workspace.Id,
            Path = path, Name = DefaultValueName, ValueType = RegistryValueType.Json,
            ValueJson = JsonSerializer.Serialize(value, RelaxKonOSJsonOptions.Default),
            DesiredUpdatedAt = DateTimeOffset.UtcNow, DesiredUpdatedBy = updatedBy,
        }, 0);
    }
}
