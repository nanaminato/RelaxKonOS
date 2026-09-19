using RelaxKonOS.Protocol.Workspace.SystemStyles;

namespace RelaxKonOS.Client.Services.Theming;

/// <summary>One system style this device can actually render.</summary>
public sealed record SystemStyleDefinition(
    string Id,
    string DisplayName,
    bool IsBuiltIn,
    SystemStyleManifestDto Manifest,
    string? UnavailableReason = null)
{
    public bool IsAvailable => UnavailableReason is null;

    public bool HasUnavailableReason => !string.IsNullOrWhiteSpace(UnavailableReason);

    /// <summary>Display name that falls back to the raw id when a package omits a label.</summary>
    public string SelectionDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}

public interface ISystemStyleRegistry
{
    /// <summary>Every style the user may pick, including ones this device cannot resolve.</summary>
    IReadOnlyList<SystemStyleDefinition> Available { get; }

    event EventHandler? Changed;

    bool TryResolve(string? id, out SystemStyleDefinition definition);

    /// <summary>Validated manifest for an id, or <c>null</c> plus a problem code.</summary>
    bool TryGetManifest(string? id, out SystemStyleManifestDto manifest, out string? problemCode);
}

/// <summary>
/// Device-local resolution of the workspace's <c>SystemStyleId</c>. The workspace carries an
/// intent; whether this machine can honour it is decided here, at startup, and never by rewriting
/// the user's preference. A style that fails validation is reported with a problem code instead of
/// being silently downgraded, so the settings page can explain what is wrong.
/// </summary>
public sealed class SystemStyleRegistry : ISystemStyleRegistry
{
    /// <summary>The manifest API this build implements; manifests requiring more are refused.</summary>
    public const string HostApiVersion = SystemStyleManifestDto.CurrentHostApiVersion;

    private readonly Dictionary<string, SystemStyleDefinition> _styles = new(StringComparer.Ordinal);

    public SystemStyleRegistry()
    {
        foreach (var manifest in BuiltInSystemStyles.All)
            Register(manifest, isBuiltIn: true);
    }

    public IReadOnlyList<SystemStyleDefinition> Available =>
        _styles.Values.OrderBy(style => style.IsBuiltIn ? 0 : 1).ThenBy(style => style.DisplayName, StringComparer.Ordinal).ToArray();

    public event EventHandler? Changed;

    public bool TryResolve(string? id, out SystemStyleDefinition definition)
    {
        if (!string.IsNullOrEmpty(id) && _styles.TryGetValue(id, out var found) && found.IsAvailable)
        {
            definition = found;
            return true;
        }
        definition = default!;
        return false;
    }

    public bool TryGetManifest(string? id, out SystemStyleManifestDto manifest, out string? problemCode)
    {
        manifest = new SystemStyleManifestDto();
        if (!string.IsNullOrEmpty(id) && _styles.TryGetValue(id, out var found))
        {
            if (found.IsAvailable)
            {
                manifest = found.Manifest;
                problemCode = null;
                return true;
            }
            problemCode = found.UnavailableReason ?? SystemStyleProblems.StyleUnavailable;
            return false;
        }
        problemCode = SystemStyleProblems.StyleUnavailable;
        return false;
    }

    /// <summary>
    /// Adds a manifest after validation. An unavailable style keeps its identity and reason so the
    /// settings page can show "not installed on this device" instead of dropping the user's choice.
    /// </summary>
    /// <remarks>
    /// A package-supplied style is held to the stricter external gate: it must declare an external
    /// provenance and name the package it came from. Nothing in this build feeds the external path
    /// yet - third-party manifests stay closed until the built-in profiles cover every scenario -
    /// but the boundary is enforced here rather than left to be remembered later.
    /// </remarks>
    public bool Register(SystemStyleManifestDto manifest, bool isBuiltIn)
    {
        var accepted = isBuiltIn
            ? SystemStyleManifestValidator.TryValidate(manifest, HostApiVersion, out var problem, out var normalized)
            : SystemStyleManifestValidator.TryValidateExternal(manifest, HostApiVersion, out problem, out normalized);
        if (!accepted)
        {
            if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id))
                _styles[manifest.Id] = new SystemStyleDefinition(manifest.Id, manifest.DisplayName, isBuiltIn, new(), problem);
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        _styles[normalized.Id] = new SystemStyleDefinition(normalized.Id, normalized.DisplayName, isBuiltIn, normalized);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
