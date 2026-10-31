using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Services.Modules;

/// <summary>Why the connected server does not offer a capability an application asked for.</summary>
public enum ServerCapabilityAvailability
{
    /// <summary>The inventory was never read, so nothing can be said about the cause.</summary>
    Unknown,

    /// <summary>A module declares the capability and is disabled on this server.</summary>
    ProvidedByDisabledModule,

    /// <summary>A module declares it and is enabled, so the deployment mode or host platform withholds it.</summary>
    ProvidedByEnabledModule,

    /// <summary>No official module on this build declares it; it is not a module-switchable capability.</summary>
    NotProvidedByAnyModule,
}

public readonly record struct ServerCapabilityExplanation(ServerCapabilityAvailability Availability, string? ModuleId)
{
    public static readonly ServerCapabilityExplanation Unknown = new(ServerCapabilityAvailability.Unknown, null);

    /// <summary>
    /// Attributes a missing capability to a module. Deliberately pure: the same inventory and
    /// capability must always produce the same answer, so a launch dialog and a settings page can
    /// never disagree about why an application is unavailable.
    /// </summary>
    public static ServerCapabilityExplanation Explain(IReadOnlyList<ServerModuleDto>? inventory, string capability)
    {
        if (inventory is null) return Unknown;

        ServerModuleDto? disabledProvider = null;
        foreach (var module in inventory)
        {
            if (!module.Capabilities.Contains(capability, StringComparer.Ordinal)) continue;
            if (module.EffectiveEnabled)
                return new(ServerCapabilityAvailability.ProvidedByEnabledModule, module.Id);
            disabledProvider ??= module;
        }

        return disabledProvider is null
            ? new(ServerCapabilityAvailability.NotProvidedByAnyModule, null)
            : new(ServerCapabilityAvailability.ProvidedByDisabledModule, disabledProvider.Id);
    }
}
