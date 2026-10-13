using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.ConfigurationRegistry;

public static class DeviceConfigurationRegistry
{
    public static void EnsureDefaults(IRegistryRepository registry, Guid user, Guid device)
    {
        Ensure(ClientPresentationPaths.Terminal, TerminalPaddingSettings.Default);
        Ensure(ClientPresentationPaths.Synchronization, ClientSynchronizationSettings.Default);
        Ensure(ClientPresentationPaths.Registry, RegistryAppearanceSettings.Default);
        void Ensure<T>(string path, T value) => registry.CompareExchange(new RegistryEntry
        {
            UserId = user, Scope = RegistryScope.Device, ScopeId = device, Path = path,
            Name = WorkspaceConfigurationRegistry.DefaultValueName, ValueType = RegistryValueType.Json,
            ValueJson = JsonSerializer.Serialize(value, RelaxKonOSJsonOptions.Default),
            DesiredUpdatedAt = DateTimeOffset.UtcNow, DesiredUpdatedBy = user.ToString("D"),
        }, 0);
    }
}
