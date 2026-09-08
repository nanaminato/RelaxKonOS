using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.VirtualSystemDrive;

namespace RelaxKonOS.Client.Services.VirtualSystemDrive;

/// <summary>Repairs observable built-in descriptors from Host-compiled definitions.</summary>
public sealed class BuiltInDescriptorSeeder
{
    private readonly VirtualSystemDrive _drive;
    private readonly IBuiltInApplicationFactoryRegistry _registry;

    public BuiltInDescriptorSeeder(VirtualSystemDrive drive, IBuiltInApplicationFactoryRegistry registry)
    {
        _drive = drive;
        _registry = registry;
    }

    /// <summary>
    /// A descriptor is never trusted as the source of built-in identity. Missing, stale, or
    /// malformed files are simply replaced from the registry; user-owned VSD content is untouched.
    /// </summary>
    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        _drive.EnsureCreated();
        foreach (var definition in _registry.Definitions)
        {
            try
            {
                var directory = _drive.ResolveUnder(_drive.BuiltInProgramsDirectory, definition.AppId.Value);
                Directory.CreateDirectory(directory);
                var path = _drive.ResolveUnder(directory, "app.relaxkonos.json");
                var expected = ToDescriptor(definition);
                var mustReplace = true;
                try
                {
                    var existing = await _drive.ReadJsonAsync<ApplicationDescriptor>(path, cancellationToken);
                    mustReplace = !MatchesDefinition(existing, definition);
                }
                catch (VirtualSystemDriveException)
                {
                    // The descriptor is a recoverable installation mirror, not user configuration.
                }

                if (mustReplace)
                    await _drive.WriteJsonAtomicallyAsync(path, expected, cancellationToken);
            }
            catch (VirtualSystemDriveException)
            {
                // A bad directory must not prevent other compiled-in applications from seeding.
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static ApplicationDescriptor ToDescriptor(BuiltInApplicationDefinition definition)
    {
        var manifest = definition.Manifest;
        return new ApplicationDescriptor(
            ApplicationDescriptorValidator.CurrentSchemaVersion,
            definition.AppId.Value,
            ApplicationDescriptorKind.BuiltIn,
            manifest.DisplayName,
            manifest.Version,
            new ApplicationDescriptorActivation(BuiltInKey: definition.BuiltInKey),
            manifest.Description,
            new ApplicationDescriptorIcon(manifest.IconPath, manifest.IconGlyph),
            manifest.Permissions,
            manifest.FileExtensions,
            manifest.UriSchemes,
            manifest.InstancePolicy.ToString(),
            manifest.SupportedClientPlatforms,
            manifest.PermissionModelVersion);
    }

    /// <summary>
    /// Compares every persisted field with the Host projection. The comparison deliberately does
    /// not use record equality because deserialized collection instances are not reference-equal.
    /// </summary>
    public static bool MatchesDefinition(ApplicationDescriptor? descriptor, BuiltInApplicationDefinition definition)
    {
        if (descriptor is null || !ApplicationDescriptorValidator.Validate(descriptor).IsValid)
            return false;

        var expected = ToDescriptor(definition);
        return descriptor.SchemaVersion == expected.SchemaVersion
            && descriptor.Id == expected.Id
            && descriptor.Kind == expected.Kind
            && descriptor.DisplayName == expected.DisplayName
            && descriptor.Version == expected.Version
            && descriptor.Description == expected.Description
            && descriptor.PermissionModelVersion == expected.PermissionModelVersion
            && descriptor.InstancePolicy == expected.InstancePolicy
            && descriptor.Activation.BuiltInKey == expected.Activation.BuiltInKey
            && descriptor.Activation.EntryAssembly is null
            && descriptor.Activation.EntryType is null
            && descriptor.Icon?.Path == expected.Icon?.Path
            && descriptor.Icon?.Glyph == expected.Icon?.Glyph
            && Same(descriptor.RequestedPermissions, expected.RequestedPermissions)
            && Same(descriptor.SupportedFileExtensions, expected.SupportedFileExtensions)
            && Same(descriptor.SupportedUriSchemes, expected.SupportedUriSchemes)
            && Same(descriptor.ClientPlatforms, expected.ClientPlatforms);
    }

    private static bool Same(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        (left ?? Array.Empty<string>()).SequenceEqual(right ?? Array.Empty<string>(), StringComparer.Ordinal);
}
