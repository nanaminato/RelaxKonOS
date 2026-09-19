using System.Text.RegularExpressions;

namespace RelaxKonOS.Protocol.Workspace.SystemStyles;

/// <summary>Problem codes surfaced when a system style is refused. Keys, not sentences: the client localises them.</summary>
public static class SystemStyleProblems
{
    public const string InvalidManifest = "systemstyle.invalid_manifest";
    public const string SchemaUnsupported = "systemstyle.schema_unsupported";
    public const string HostApiTooOld = "systemstyle.host_api_too_old";
    public const string InvalidId = "systemstyle.invalid_id";
    public const string InvalidDisplayName = "systemstyle.invalid_display_name";
    public const string IncompleteRecipes = "systemstyle.incomplete_recipes";
    public const string UnknownRecipe = "systemstyle.unknown_recipe";
    public const string UnknownToken = "systemstyle.unknown_token";
    public const string TokenOutOfRange = "systemstyle.token_out_of_range";
    public const string MissingToken = "systemstyle.missing_token";
    public const string HitTargetTooSmall = "systemstyle.hit_target_too_small";
    public const string ReducedMotionUnsupported = "systemstyle.reduced_motion_unsupported";
    public const string StyleUnavailable = "systemstyle.style_unavailable";
    public const string InvalidSource = "systemstyle.invalid_source";
    public const string ExternalSourceRequired = "systemstyle.external_source_required";
    public const string PackageAttributionMissing = "systemstyle.package_attribution_missing";
}

/// <summary>
/// Verifies a data-only style manifest before any of its tokens reach the live resource graph.
/// A style that fails validation must leave the previously applied style untouched: callers apply
/// the result atomically, so a rejection can never produce a half-styled or transparent UI.
/// </summary>
public static class SystemStyleManifestValidator
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9.-]{2,127}$", RegexOptions.Compiled);

    public static bool TryValidate(
        SystemStyleManifestDto? manifest,
        string hostApiVersion,
        out string? problemCode,
        out SystemStyleManifestDto normalized)
    {
        normalized = new SystemStyleManifestDto();
        problemCode = SystemStyleProblems.InvalidManifest;
        if (manifest is null) return false;

        if (manifest.SchemaVersion != SystemStyleManifestDto.CurrentSchemaVersion)
            return Fail(SystemStyleProblems.SchemaUnsupported, out problemCode, out normalized);

        if (!Version.TryParse(manifest.MinimumHostApiVersion, out var minimumHost)
            || !Version.TryParse(hostApiVersion, out var host)
            || minimumHost > host)
            return Fail(SystemStyleProblems.HostApiTooOld, out problemCode, out normalized);

        var id = manifest.Id?.Trim();
        if (string.IsNullOrEmpty(id) || !IdPattern.IsMatch(id))
            return Fail(SystemStyleProblems.InvalidId, out problemCode, out normalized);

        var displayName = manifest.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80)
            return Fail(SystemStyleProblems.InvalidDisplayName, out problemCode, out normalized);

        if (!TryValidateRecipes(manifest.SupportedRecipes, out problemCode)) return false;

        var accessibility = manifest.Accessibility ?? new SystemStyleAccessibilityDto();
        if (accessibility.MinimumHitTarget < SystemStyleTokenContract.AbsoluteMinimumHitTarget)
            return Fail(SystemStyleProblems.HitTargetTooSmall, out problemCode, out normalized);
        if (!accessibility.SupportsReducedMotion)
            return Fail(SystemStyleProblems.ReducedMotionUnsupported, out problemCode, out normalized);

        if (!TryValidateTokenMap(manifest.Tokens, out problemCode)) return false;
        if (!TryValidateTokenMap(manifest.LightTokens, out problemCode)) return false;
        if (!TryValidateTokenMap(manifest.DarkTokens, out problemCode)) return false;

        foreach (var required in SystemStyleTokenContract.Required)
        {
            var declared = manifest.Tokens.ContainsKey(required)
                || (manifest.LightTokens?.ContainsKey(required) == true && manifest.DarkTokens?.ContainsKey(required) == true);
            if (!declared)
                return Fail(SystemStyleProblems.MissingToken, out problemCode, out normalized);
        }

        if (manifest.Tokens.TryGetValue("MinimumHitTarget", out var declaredTarget)
            && declaredTarget < accessibility.MinimumHitTarget)
            return Fail(SystemStyleProblems.HitTargetTooSmall, out problemCode, out normalized);

        // Provenance is part of the contract, not decoration: a manifest that cannot say where it
        // came from is refused rather than filed under an "unknown" bucket.
        var source = manifest.Source?.Trim();
        if (!SystemStyleSources.IsRecognized(source))
            return Fail(SystemStyleProblems.InvalidSource, out problemCode, out normalized);

        normalized = manifest with
        {
            Id = id,
            DisplayName = displayName,
            MinimumHostApiVersion = manifest.MinimumHostApiVersion!.Trim(),
            Source = source!,
            Accessibility = accessibility,
            Tokens = Clean(manifest.Tokens),
            LightTokens = manifest.LightTokens is null ? null : Clean(manifest.LightTokens),
            DarkTokens = manifest.DarkTokens is null ? null : Clean(manifest.DarkTokens),
        };
        problemCode = null;
        return true;
    }

    /// <summary>
    /// The gate a style that arrives with an installed package must pass. It is a stricter form of
    /// <see cref="TryValidate"/>: the manifest must additionally claim an external provenance and
    /// name the package and version it came from, so a package can never present itself as a style
    /// shipped by the host.
    /// </summary>
    /// <remarks>
    /// "Only the current contract version" is enforced by <see cref="TryValidate"/>: the schema
    /// version must match exactly and the declared host API must not exceed this build's. There is
    /// deliberately no migration path and no older-schema reader.
    /// </remarks>
    public static bool TryValidateExternal(
        SystemStyleManifestDto? manifest,
        string hostApiVersion,
        out string? problemCode,
        out SystemStyleManifestDto normalized)
    {
        if (!TryValidate(manifest, hostApiVersion, out problemCode, out normalized))
        {
            normalized = new SystemStyleManifestDto();
            return false;
        }

        if (!SystemStyleSources.IsExternal(normalized.Source))
        {
            problemCode = SystemStyleProblems.ExternalSourceRequired;
            normalized = new SystemStyleManifestDto();
            return false;
        }

        if (string.IsNullOrWhiteSpace(normalized.PackageId) || string.IsNullOrWhiteSpace(normalized.PackageVersion))
        {
            problemCode = SystemStyleProblems.PackageAttributionMissing;
            normalized = new SystemStyleManifestDto();
            return false;
        }

        problemCode = null;
        return true;
    }

    /// <summary>Validates every recipe slot; a style that leaves a slot empty is incomplete.</summary>
    private static bool TryValidateRecipes(SystemStyleRecipeSelectionDto? selection, out string? problemCode)
    {
        problemCode = SystemStyleProblems.IncompleteRecipes;
        if (selection is null) return false;
        foreach (var (kind, value) in selection.AsMap())
        {
            var allowed = SystemStyleRecipeKinds.Allowed(kind);
            if (allowed is null || string.IsNullOrWhiteSpace(value) || !allowed.Contains(value, StringComparer.Ordinal))
            {
                problemCode = SystemStyleProblems.UnknownRecipe;
                return false;
            }
        }
        problemCode = null;
        return true;
    }

    private static bool TryValidateTokenMap(Dictionary<string, double>? tokens, out string? problemCode)
    {
        problemCode = null;
        if (tokens is null) return true;
        foreach (var (key, value) in tokens)
        {
            if (!SystemStyleTokenContract.IsKnown(key))
            {
                problemCode = SystemStyleProblems.UnknownToken;
                return false;
            }
            if (double.IsNaN(value) || double.IsInfinity(value) || !SystemStyleTokenContract.IsInRange(key, value))
            {
                problemCode = SystemStyleProblems.TokenOutOfRange;
                return false;
            }
        }
        return true;
    }

    private static Dictionary<string, double> Clean(Dictionary<string, double> tokens) =>
        tokens.Where(pair => SystemStyleTokenContract.IsKnown(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static bool Fail(string code, out string? problemCode, out SystemStyleManifestDto normalized)
    {
        problemCode = code;
        normalized = new SystemStyleManifestDto();
        return false;
    }
}
