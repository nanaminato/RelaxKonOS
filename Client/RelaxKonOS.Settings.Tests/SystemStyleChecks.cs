using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;

/// <summary>
/// Pure contract checks for the desktop-experience layer. They run without Avalonia, without a
/// display server and without touching the host, so they hold on every platform.
/// </summary>
internal static class SystemStyleChecks
{
    public static void Run()
    {
        VerifyRecipeVocabularyIsClosed();
        VerifyBuiltInProfilesAreComplete();
        VerifyValidatorRejectsMalformedManifests();
        VerifyTokenResolution();
        VerifyAppearanceIsColourOnly();
        VerifyRecommendedStyleMapping();
        VerifyPreferencesRoundTrip();
        VerifyExternalPackageGate();
        VerifyRecipeCoverage();
        VerifyNoHardcodedColours();
        Console.WriteLine("System style verification passed: closed recipe vocabulary, three complete built-in profiles, " +
                          "manifest rejection, token resolution, colour-only appearance, wire round-trip, " +
                          "external-package gate, recipe coverage, no hardcoded colours in product UI.");
    }

    /// <summary>Every recipe slot must resolve to a fixed, host-reviewed value set.</summary>
    private static void VerifyRecipeVocabularyIsClosed()
    {
        Check(SystemStyleRecipeKinds.All.Count == 4, "A system style must fill exactly four recipe slots.");
        foreach (var kind in SystemStyleRecipeKinds.All)
        {
            var allowed = SystemStyleRecipeKinds.Allowed(kind);
            Check(allowed is { Count: >= 3 }, $"Recipe slot '{kind}' has no admissible values.");
            Check(allowed!.All(value => !string.IsNullOrWhiteSpace(value)), $"Recipe slot '{kind}' declares an empty value.");
        }
        Check(SystemStyleRecipeKinds.Allowed("unknown-slot") is null, "An unknown recipe slot was accepted.");
    }

    private static void VerifyBuiltInProfilesAreComplete()
    {
        Check(BuiltInSystemStyles.All.Count == 3, "Three built-in system styles are expected.");
        foreach (var manifest in BuiltInSystemStyles.All)
        {
            var accepted = SystemStyleManifestValidator.TryValidate(
                manifest, SystemStyleManifestDto.CurrentHostApiVersion, out var problem, out var normalized);
            Check(accepted, $"Built-in style '{manifest.Id}' failed validation: {problem}.");

            var light = normalized.ResolveTokens(dark: false);
            var dark = normalized.ResolveTokens(dark: true);
            foreach (var key in SystemStyleTokenContract.Required)
            {
                Check(light.ContainsKey(key) && dark.ContainsKey(key), $"Style '{manifest.Id}' is missing '{key}'.");
                Check(SystemStyleTokenContract.IsInRange(key, light[key]),
                    $"Style '{manifest.Id}' resolved '{key}' outside its range.");
            }

            // Every recipe slot must be filled with an admissible value.
            foreach (var (kind, value) in normalized.SupportedRecipes.AsMap())
            {
                var allowed = SystemStyleRecipeKinds.Allowed(kind);
                Check(allowed is not null && allowed.Contains(value, StringComparer.Ordinal),
                    $"Style '{manifest.Id}' selected an inadmissible value for '{kind}'.");
            }

            Check(normalized.Accessibility.MinimumHitTarget >= SystemStyleTokenContract.AbsoluteMinimumHitTarget,
                $"Style '{manifest.Id}' declares a hit target below the host minimum.");
        }

        // The three profiles must differ in more than one dimension, otherwise the switch is cosmetic.
        var recipes = BuiltInSystemStyles.All.Select(style => style.SupportedRecipes.AsMap()).ToArray();
        for (var i = 0; i < recipes.Length; i++)
            for (var j = i + 1; j < recipes.Length; j++)
                Check(recipes[i].Where(pair => !string.Equals(pair.Value, recipes[j][pair.Key], StringComparison.Ordinal)).Count() >= 2,
                    "Two built-in styles differ in fewer than two recipe slots.");
    }

    private static void VerifyValidatorRejectsMalformedManifests()
    {
        var hostApi = SystemStyleManifestDto.CurrentHostApiVersion;

        CheckProblem(BuiltInSystemStyles.WindowsLike with { SchemaVersion = 99 }, hostApi,
            SystemStyleProblems.SchemaUnsupported, "an unsupported schema version");
        CheckProblem(BuiltInSystemStyles.WindowsLike with { Id = "Bad Id" }, hostApi,
            SystemStyleProblems.InvalidId, "an invalid style id");
        CheckProblem(BuiltInSystemStyles.WindowsLike with { DisplayName = "" }, hostApi,
            SystemStyleProblems.InvalidDisplayName, "an empty display name");
        CheckProblem(BuiltInSystemStyles.WindowsLike with { MinimumHostApiVersion = "9.9" }, hostApi,
            SystemStyleProblems.HostApiTooOld, "a style requiring a newer host API");
        CheckProblem(BuiltInSystemStyles.WindowsLike with
        {
            SupportedRecipes = BuiltInSystemStyles.WindowsLike.SupportedRecipes with { ContextMenu = "arbitrary-xaml" },
        }, hostApi, SystemStyleProblems.UnknownRecipe, "a recipe outside the closed vocabulary");
        CheckProblem(BuiltInSystemStyles.WindowsLike with { Tokens = With(BuiltInSystemStyles.WindowsLike.Tokens, "WindowBlue", 4) },
            hostApi, SystemStyleProblems.UnknownToken, "an unknown token");
        CheckProblem(BuiltInSystemStyles.WindowsLike with { Tokens = With(BuiltInSystemStyles.WindowsLike.Tokens, "WindowCornerRadius", 9999) },
            hostApi, SystemStyleProblems.TokenOutOfRange, "an out-of-range token");
        CheckProblem(BuiltInSystemStyles.WindowsLike with { Tokens = With(BuiltInSystemStyles.WindowsLike.Tokens, "MenuCornerRadius", double.NaN) },
            hostApi, SystemStyleProblems.TokenOutOfRange, "a NaN token");
        CheckProblem(BuiltInSystemStyles.WindowsLike with { Tokens = Without(BuiltInSystemStyles.WindowsLike.Tokens, "WindowTitleBarHeight") },
            hostApi, SystemStyleProblems.MissingToken, "a missing required token");
        CheckProblem(BuiltInSystemStyles.WindowsLike with
        {
            Accessibility = new SystemStyleAccessibilityDto { MinimumHitTarget = 12, SupportsReducedMotion = true },
        }, hostApi, SystemStyleProblems.HitTargetTooSmall, "a hit target below the accessibility floor");
        CheckProblem(BuiltInSystemStyles.WindowsLike with
        {
            Accessibility = new SystemStyleAccessibilityDto { MinimumHitTarget = 40, SupportsReducedMotion = false },
        }, hostApi, SystemStyleProblems.ReducedMotionUnsupported, "a style that depends on animation");

        Check(!SystemStyleManifestValidator.TryValidate(null, hostApi, out var nullProblem, out _)
              && nullProblem == SystemStyleProblems.InvalidManifest,
            "A null manifest was accepted.");
    }

    private static void VerifyTokenResolution()
    {
        // An optional token the style omits must fall back to the contract default...
        Check(Math.Abs(SystemStyleTokenContract.DefaultOf("DockMagnification") - 1.25) < 0.0001,
            "Optional token default is not the contract default.");

        // ...and a mode-specific override must win only for that mode.
        var manifest = BuiltInSystemStyles.WindowsLike with
        {
            LightTokens = new Dictionary<string, double>(StringComparer.Ordinal) { ["MenuCornerRadius"] = 2 },
            DarkTokens = new Dictionary<string, double>(StringComparer.Ordinal) { ["MenuCornerRadius"] = 14 },
        };
        var light = manifest.ResolveTokens(dark: false);
        var dark = manifest.ResolveTokens(dark: true);
        Check(Math.Abs(light["MenuCornerRadius"] - 2) < 0.0001, "Light token override was not applied.");
        Check(Math.Abs(dark["MenuCornerRadius"] - 14) < 0.0001, "Dark token override was not applied.");
        Check(Math.Abs(light["WindowCornerRadius"] - 8) < 0.0001, "Shared tokens must survive a mode override.");

        // A manifest that only declares mode-specific values for a required token is still complete.
        var modeOnly = BuiltInSystemStyles.WindowsLike with
        {
            Tokens = Without(BuiltInSystemStyles.WindowsLike.Tokens, "FocusRingThickness"),
            LightTokens = new Dictionary<string, double>(StringComparer.Ordinal) { ["FocusRingThickness"] = 2 },
            DarkTokens = new Dictionary<string, double>(StringComparer.Ordinal) { ["FocusRingThickness"] = 3 },
        };
        Check(SystemStyleManifestValidator.TryValidate(modeOnly, SystemStyleManifestDto.CurrentHostApiVersion, out _, out _),
            "Mode-specific declarations must satisfy the required-token rule.");
    }

    /// <summary>A style must never carry colour; colours stay in the palette contract.</summary>
    private static void VerifyAppearanceIsColourOnly()
    {
        var appearance = AppearancePreferencesDto.Default;
        Check(appearance.Mode == ThemeKind.Light && appearance.PaletteId == AppearancePreferencesDto.DefaultPaletteId,
            "Default appearance is not the documented light palette.");
        Check(appearance.CustomPalettes.Count == 0, "Default appearance must not invent custom palettes.");

        var light = ThemePaletteDefaults.Resolve(appearance, dark: false);
        var dark = ThemePaletteDefaults.Resolve(appearance, dark: true);
        Check(ThemePaletteValidator.TryValidate(light, out _) && ThemePaletteValidator.TryValidate(dark, out _),
            "The default palette no longer meets contrast requirements.");
        Check(light["Surface"] != dark["Surface"], "Light and dark palettes resolve identically.");

        // No token may name a colour or a brush: those keys belong to the palette contract only.
        foreach (var definition in SystemStyleTokenContract.All)
            Check(!definition.Key.EndsWith("Color", StringComparison.Ordinal)
                  && !definition.Key.EndsWith("Brush", StringComparison.Ordinal),
                $"Token '{definition.Key}' looks like a colour and must stay in the palette contract.");

        // Nor may a manifest expose a colour slot.
        var manifestProperties = typeof(SystemStyleManifestDto).GetProperties().Select(property => property.Name).ToArray();
        foreach (var forbidden in new[] { "Colors", "Palette", "Accent", "Background" })
            Check(!manifestProperties.Contains(forbidden), $"Manifest exposes a colour-shaped property '{forbidden}'.");
    }

    private static void VerifyRecommendedStyleMapping()
    {
        Check(SystemStyleIds.RecommendedForShell("relaxkonos.windows-like") == SystemStyleIds.WindowsLike, "Windows shell must recommend the Windows-like style.");
        Check(SystemStyleIds.RecommendedForShell("relaxkonos.macos-like") == SystemStyleIds.MacOsLike, "macOS shell must recommend the macOS-like style.");
        Check(SystemStyleIds.RecommendedForShell("relaxkonos.ubuntu-like") == SystemStyleIds.UbuntuLike, "Ubuntu shell must recommend the Ubuntu-like style.");
        Check(SystemStyleIds.RecommendedForShell(null) == SystemStyleIds.WindowsLike, "An unknown shell must fall back to the default style.");

        // The recommendation is a convenience, never a stored alias: both ids stay independently addressable.
        Check(BuiltInSystemStyles.TryGet(SystemStyleIds.MacOsLike, out var manifest) && manifest.Id == SystemStyleIds.MacOsLike,
            "Built-in style lookup failed.");
        Check(!BuiltInSystemStyles.TryGet("com.example.missing", out _), "An unknown style id resolved to a built-in.");
    }

    /// <summary>
    /// The wire shape is the only path either side reads or writes; an old sibling field must not
    /// survive a round-trip, and the style id must travel with the appearance it belongs to.
    /// </summary>
    private static void VerifyPreferencesRoundTrip()
    {
        var options = RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default;
        var preferences = new WorkspacePreferencesDto(
            WorkspacePreferencesDto.BuiltInWallpaperPrefix + "bloom",
            WorkspacePreferencesDto.TimeFormat24H, "yyyy/M/d", "en-US", "en-US", [],
            DesktopExperience: new DesktopExperiencePreferencesDto
            {
                Appearance = new AppearancePreferencesDto { Mode = ThemeKind.Dark, PaletteId = "builtin:nord" },
                SystemStyleId = SystemStyleIds.UbuntuLike,
                Shell = new ShellSelectionDto("relaxkonos.ubuntu-like"),
            });

        var json = System.Text.Json.JsonSerializer.Serialize(preferences, options);
        Check(!json.Contains("styleId", StringComparison.Ordinal), "The removed StyleId field is still serialized.");
        Check(!json.Contains("\"theme\":", StringComparison.Ordinal), "The removed top-level theme field is still serialized.");

        var restored = System.Text.Json.JsonSerializer.Deserialize<WorkspacePreferencesDto>(json, options)
            ?? throw new InvalidOperationException("Workspace preferences did not round-trip.");

        Check(restored.DesktopExperience?.SystemStyleId == SystemStyleIds.UbuntuLike, "The system style id was lost on the wire.");
        Check(restored.DesktopExperience?.Appearance.Mode == ThemeKind.Dark
              && restored.DesktopExperience?.Appearance.PaletteId == "builtin:nord",
            "The appearance payload was lost on the wire.");
        Check(restored.DesktopExperience?.Shell.ShellId == "relaxkonos.ubuntu-like", "The shell selection was lost on the wire.");
    }

    /// <summary>
    /// A style that arrives with a package is held to the stricter gate: it must claim an external
    /// provenance and name the package and version it came from, so it can never present itself as
    /// a style the host ships. The gate is also the place where "only the current contract version"
    /// is enforced, so no older manifest reader exists to keep alive.
    /// </summary>
    private static void VerifyExternalPackageGate()
    {
        var hostApi = SystemStyleManifestDto.CurrentHostApiVersion;
        var external = BuiltInSystemStyles.WindowsLike with
        {
            Id = "com.example.windows11-style",
            Source = SystemStyleSources.SignedPackage,
            PackageId = "com.example.windows11",
            PackageVersion = "1.0.0",
        };

        Check(SystemStyleManifestValidator.TryValidateExternal(external, hostApi, out _, out var normalized),
            "A properly attributed external style was refused.");
        Check(normalized.Source == SystemStyleSources.SignedPackage, "The external provenance was not preserved.");

        // A built-in manifest must not pass the external gate: claiming "builtin" is not enough.
        CheckExternalProblem(external with { Source = SystemStyleSources.BuiltIn }, hostApi,
            SystemStyleProblems.ExternalSourceRequired, "an external style claiming to be built in");

        // Attribution is mandatory.
        CheckExternalProblem(external with { PackageId = null }, hostApi,
            SystemStyleProblems.PackageAttributionMissing, "an external style with no package id");
        CheckExternalProblem(external with { PackageVersion = "" }, hostApi,
            SystemStyleProblems.PackageAttributionMissing, "an external style with no package version");

        // An unrecognised provenance is refused outright rather than filed under "unknown".
        CheckProblem(external with { Source = "copied-from-a-blog" }, hostApi,
            SystemStyleProblems.InvalidSource, "an unrecognised provenance");
        CheckProblem(external with { Source = "unknown" }, hostApi,
            SystemStyleProblems.InvalidSource, "the retired placeholder provenance");

        // A newer contract version is refused for external and built-in styles alike.
        CheckProblem(external with { SchemaVersion = SystemStyleManifestDto.CurrentSchemaVersion + 1 }, hostApi,
            SystemStyleProblems.SchemaUnsupported, "an external style from a newer schema");
        CheckProblem(external with { MinimumHostApiVersion = "2.0" }, hostApi,
            SystemStyleProblems.HostApiTooOld, "an external style requiring an API this host does not implement");

        Check(!SystemStyleSources.IsExternal(SystemStyleSources.BuiltIn), "A built-in style counted as external.");
        Check(!SystemStyleSources.IsRecognized(null), "A null provenance was recognised.");
    }

    /// <summary>
    /// Every recipe value the contract admits must actually be rendered by the host. Without this
    /// rule a style could select a variant nobody implemented and the UI would silently fall back,
    /// which is exactly the "looks unstyled" failure the recipe closure exists to prevent.
    /// </summary>
    private static void VerifyRecipeCoverage()
    {
        var root = TryFindRepositoryRoot();
        if (root is null)
        {
            Console.WriteLine("System style recipe coverage: skipped (repository root not found from the test output directory).");
            return;
        }

        // Recipe values are consumed as pseudo-classes / classes in the reviewed template, or as
        // named constants in the host's derived-token and profile tables. The file that consumes a
        // slot is the proof that the slot has a rendering strategy at all.
        var slots = new (string Kind, string File, string Prefix, bool ConstantName)[]
        {
            (SystemStyleRecipeKinds.WindowChrome, "Framework/RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml", ":chrome-", false),
            (SystemStyleRecipeKinds.TaskSwitcher, "Client/RelaxKonOS.Client/Views/Shell/WindowOverviewView.axaml", "recipe-", false),
            (SystemStyleRecipeKinds.ContextMenu, "Framework/RelaxKonOS.UI/Themes/SystemStyle/SystemStyleResourceBuilder.cs", "ContextMenuRecipes.", true),
            (SystemStyleRecipeKinds.ShellChrome, "Shared/RelaxKonOS.Protocol/Workspace/SystemStyles/BuiltInSystemStyles.cs", "ShellChromeRecipes.", true),
        };

        foreach (var (kind, relativePath, prefix, constantName) in slots)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(path), $"Recipe slot '{kind}' has no host implementation file ({relativePath}).");
            var source = File.ReadAllText(path);

            foreach (var value in SystemStyleRecipeKinds.Allowed(kind)!)
            {
                var marker = constantName ? prefix + ConstantName(value) : prefix + value;
                Check(source.Contains(marker, StringComparison.Ordinal),
                    $"Recipe slot '{kind}' allows '{value}' but {Path.GetFileName(relativePath)} does not implement it (looked for '{marker}').");
            }
        }

        // Shell chrome is structural: each built-in profile must pair with the chrome of the shell
        // it is recommended for, otherwise the settings page would offer a combination the shell
        // itself cannot honour.
        var pairings = new (string ShellId, string StyleId, string Recipe)[]
        {
            ("relaxkonos.windows-like", SystemStyleIds.WindowsLike, ShellChromeRecipes.BottomTaskbar),
            ("relaxkonos.macos-like", SystemStyleIds.MacOsLike, ShellChromeRecipes.TopMenuPlusDock),
            ("relaxkonos.ubuntu-like", SystemStyleIds.UbuntuLike, ShellChromeRecipes.TopBarPlusLeftDock),
        };
        foreach (var (shellId, styleId, recipe) in pairings)
        {
            Check(SystemStyleIds.RecommendedForShell(shellId) == styleId,
                $"Shell '{shellId}' no longer recommends its companion style.");
            Check(BuiltInSystemStyles.TryGet(styleId, out var manifest) && manifest.SupportedRecipes.ShellChrome == recipe,
                $"Style '{styleId}' does not select the '{recipe}' shell chrome its shell needs.");
        }
    }

    /// <summary>
    /// The plan's CI rule for product UI: no new hex colour may appear outside the documented
    /// exemptions. The list is deliberately short and each entry is a colour *source* rather than
    /// chrome - wallpaper artwork, terminal colour schemes and chart series - so a colour that
    /// belongs to the theme can only ever enter through the palette contract.
    /// </summary>
    private static void VerifyNoHardcodedColours()
    {
        var root = TryFindRepositoryRoot();
        if (root is null)
        {
            Console.WriteLine("System style colour scan: skipped (repository root not found from the test output directory).");
            return;
        }

        // Relative path -> the number of literals present when the exemption was granted. A file
        // may shed literals freely; it may never gain one.
        var exemptions = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Framework/RelaxKonOS.UI/Themes/Tokens/SystemStyleTokens.axaml"] = 2,
            ["Client/RelaxKonOS.Client/Services/ShellSettings.cs"] = 27,
            ["Client/RelaxKonOS.Client/Apps/Terminal/TerminalAppearance.cs"] = 12,
            ["Client/RelaxKonOS.Client/Apps/TaskManager/ViewModels/TaskManagerViewModel.cs"] = 4,
            ["Client/RelaxKonOS.Client/Apps/TaskManager/Controls/PerformanceLineChart.cs"] = 1,
        };

        var offending = new List<string>();
        var found = new Dictionary<string, int>(StringComparer.Ordinal);

        // UI projects only. Shared/ holds the palette contract itself and the host-side services
        // hold no presentation, so neither is in scope for a rule about themeable UI.
        foreach (var scanRoot in new[] { "Client/RelaxKonOS.Client", "Framework", "examples" })
        {
            var directory = Path.Combine(root, scanRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (extension is not (".axaml" or ".cs")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                // Generated XAML code-behind carries resolved literals copied from the resources.
                if (file.EndsWith(".g.cs", StringComparison.Ordinal) || file.EndsWith(".g.i.cs", StringComparison.Ordinal)) continue;

                var count = HexColour.Matches(File.ReadAllText(file)).Count;
                if (count == 0) continue;

                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                found[relative] = count;
                if (!exemptions.TryGetValue(relative, out var allowed))
                    offending.Add($"{relative} ({count} literals)");
                else if (count > allowed)
                    offending.Add($"{relative} ({count} literals, {allowed} exempted)");
            }
        }

        Check(offending.Count == 0,
            "Product UI declares hardcoded colours outside the documented exemptions: " + string.Join(", ", offending) + ".");

        // A documented exemption that no longer exists means the list is stale; that is a warning
        // rather than a failure, so cleaning a file never blocks the build.
        foreach (var stale in exemptions.Keys.Where(key => !found.ContainsKey(key)))
            Console.WriteLine($"System style colour scan: exemption no longer needed and can be removed: {stale}.");
    }

    private static readonly Regex HexColour = new("#[0-9A-Fa-f]{6,8}\\b", RegexOptions.Compiled);

    /// <summary>Walks up from the test output directory to the directory holding the solution.</summary>
    private static string? TryFindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RelaxKonOS.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>Maps a kebab-case recipe value to the PascalCase constant that names it in C#.</summary>
    private static string ConstantName(string recipeValue)
    {
        var parts = recipeValue.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var name = string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        // "MacosStrip" is spelled "MacOsStrip" and "gnome-overview" is "GnomeOverview".
        return name.Replace("Macos", "MacOs", StringComparison.Ordinal);
    }

    private static Dictionary<string, double> With(Dictionary<string, double> source, string key, double value)
    {
        var copy = new Dictionary<string, double>(source, StringComparer.Ordinal) { [key] = value };
        return copy;
    }

    private static Dictionary<string, double> Without(Dictionary<string, double> source, string key)
    {
        var copy = new Dictionary<string, double>(source, StringComparer.Ordinal);
        copy.Remove(key);
        return copy;
    }

    private static void CheckProblem(SystemStyleManifestDto manifest, string hostApi, string expected, string what)
    {
        var accepted = SystemStyleManifestValidator.TryValidate(manifest, hostApi, out var problem, out _);
        Check(!accepted && problem == expected, $"The validator accepted {what} (got '{problem}', expected '{expected}').");
    }

    private static void CheckExternalProblem(SystemStyleManifestDto manifest, string hostApi, string expected, string what)
    {
        var accepted = SystemStyleManifestValidator.TryValidateExternal(manifest, hostApi, out var problem, out _);
        Check(!accepted && problem == expected, $"The external gate accepted {what} (got '{problem}', expected '{expected}').");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("System style check failed: " + message);
    }
}
