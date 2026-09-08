using RelaxKonOS.Core.VirtualSystemDrive;

namespace RelaxKonOS.Client.Services.VirtualSystemDrive;

/// <summary>Owns VSD desktop link persistence; a corrupt link is isolated from its neighbours.</summary>
public sealed class ShortcutStore
{
    private readonly VirtualSystemDrive _drive;

    public ShortcutStore(VirtualSystemDrive drive) => _drive = drive;

    public async Task<IReadOnlyList<RemoteOsShortcut>> ListAsync(CancellationToken cancellationToken = default)
    {
        _drive.EnsureCreated();
        var desktop = _drive.ResolveRootChild($"Users/{_drive.LocalProfileId}/Desktop");
        var links = new List<RemoteOsShortcut>();
        foreach (var file in Directory.EnumerateFiles(desktop, "*.remoteos-link.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var shortcut = await _drive.ReadJsonAsync<RemoteOsShortcut>(file, cancellationToken);
                if (RemoteOsShortcutValidator.Validate(shortcut).IsValid)
                    links.Add(shortcut);
            }
            catch (VirtualSystemDriveException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return links.OrderBy(link => link.DisplayName, StringComparer.Ordinal).ToArray();
    }

    public async Task<RemoteOsShortcut> CreateAsync(RemoteOsShortcut shortcut, CancellationToken cancellationToken = default)
    {
        if (!RemoteOsShortcutValidator.Validate(shortcut).IsValid)
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.ShortcutInvalid);
        var normalized = shortcut with { Id = shortcut.Id.ToLowerInvariant() };
        await _drive.WriteJsonAtomicallyAsync(PathFor(normalized.Id), normalized, cancellationToken);
        return normalized;
    }

    public Task RenameAsync(string id, string displayName, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out _) || string.IsNullOrWhiteSpace(displayName))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.ShortcutInvalid);
        return RenameCoreAsync(id, displayName.Trim(), cancellationToken);
    }

    public bool Delete(string id)
    {
        if (!Guid.TryParse(id, out _)) return false;
        var path = PathFor(id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private async Task RenameCoreAsync(string id, string displayName, CancellationToken cancellationToken)
    {
        var path = PathFor(id);
        var existing = await _drive.ReadJsonAsync<RemoteOsShortcut>(path, cancellationToken);
        await CreateAsync(existing with { DisplayName = displayName }, cancellationToken);
    }

    private string PathFor(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.ShortcutInvalid);
        return _drive.ResolveRootChild($"Users/{_drive.LocalProfileId}/Desktop/{id.ToLowerInvariant()}.remoteos-link.json");
    }
}
