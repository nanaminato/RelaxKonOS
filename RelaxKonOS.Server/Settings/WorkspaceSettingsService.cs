using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Server.ConfigurationRegistry;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Settings;

/// <summary>Workspace preference persistence, independent of a settings window. Callers authorize Workspace ownership first.</summary>
public interface IWorkspaceSettingsService
{
    WorkspacePreferencesDto Read(Workspace workspace);
    WorkspacePreferencesDto? Save(Workspace workspace, WorkspacePreferencesDto draft, string actor);
}

public sealed class WorkspaceSettingsService(IRegistryRepository registry) : IWorkspaceSettingsService
{
    public WorkspacePreferencesDto Read(Workspace workspace)
    {
        var entry = registry.Find(workspace.UserId, RegistryScope.Workspace, workspace.Id,
            WorkspaceConfigurationRegistry.DesktopPath, WorkspaceConfigurationRegistry.DefaultValueName);
        if (entry is null)
        {
            // Insert-only and durable through the repository's normal persistence path.
            registry.CompareExchange(CreateEntry(workspace, WorkspacePreferencesDto.Default, "system"), 0);
            entry = registry.Find(workspace.UserId, RegistryScope.Workspace, workspace.Id,
                WorkspaceConfigurationRegistry.DesktopPath, WorkspaceConfigurationRegistry.DefaultValueName)!;
        }
        var value = JsonSerializer.Deserialize<WorkspacePreferencesDto>(entry.ValueJson, RemoteOsJsonOptions.Default);
        if (value is null || !WorkspacePreferencesValidator.TryNormalize(value, out var normalized))
            throw new InvalidDataException("Stored workspace preferences are invalid; they have not been overwritten.");
        normalized.Revision = entry.Revision;
        return normalized;
    }

    public WorkspacePreferencesDto? Save(Workspace workspace, WorkspacePreferencesDto draft, string actor)
    {
        if (draft.Revision is not > 0) throw new ArgumentException("settings.revision_required");
        if (!WorkspacePreferencesValidator.TryNormalize(draft, out var normalized))
            throw new ArgumentException("Invalid workspace preferences.");
        var saved = registry.CompareExchange(CreateEntry(workspace, normalized, actor), draft.Revision.Value);
        if (saved is null) return null;
        normalized.Revision = saved.Revision;
        return normalized;
    }

    private static RegistryEntry CreateEntry(Workspace workspace, WorkspacePreferencesDto value, string actor) => new()
    {
        UserId = workspace.UserId, Scope = RegistryScope.Workspace, ScopeId = workspace.Id,
        Path = WorkspaceConfigurationRegistry.DesktopPath, Name = WorkspaceConfigurationRegistry.DefaultValueName,
        ValueType = RegistryValueType.Json,
        ValueJson = JsonSerializer.Serialize(value with { Revision = null }, RemoteOsJsonOptions.Default),
        DesiredUpdatedAt = DateTimeOffset.UtcNow, DesiredUpdatedBy = actor,
    };
}
