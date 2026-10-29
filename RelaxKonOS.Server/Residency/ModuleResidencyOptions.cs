namespace RelaxKonOS.Server.Residency;

/// <summary>
/// How the Host treats a module that is enabled but not currently serving a request. Installation,
/// enablement and residency are three separate facts: this setting never changes whether a module is
/// installed or enabled, only when its object graph may be released from memory.
/// </summary>
internal enum ModuleResidencyStrategy
{
    /// <summary>Load on first use and keep the object graph for the lifetime of the process.</summary>
    KeepLoaded,

    /// <summary>Release after the last lease ends and the grace period has passed. The default.</summary>
    IdleDelayed,

    /// <summary>On session logout, try to release the modules that session used without an idle wait.</summary>
    ReleaseOnLogout,
}

/// <summary>
/// One resolved residency rule. The grace period is the time between "no lease is held" and the first
/// attempt to release; it absorbs short pauses between requests instead of paying a cold start each
/// time.
/// </summary>
internal sealed record ModuleResidencySettings(
    ModuleResidencyStrategy Strategy,
    TimeSpan IdleGracePeriod,
    TimeSpan WakeTimeout)
{
    /// <summary>
    /// Provisional default. The design fixes the default strategy (idle delayed release) but not this
    /// duration: it must be chosen from measured wake cost and idle memory, not from this number.
    /// </summary>
    internal static readonly TimeSpan DefaultIdleGracePeriod = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan DefaultWakeTimeout = TimeSpan.FromSeconds(30);

    internal static ModuleResidencySettings Default { get; } =
        new(ModuleResidencyStrategy.IdleDelayed, DefaultIdleGracePeriod, DefaultWakeTimeout);

    internal bool IsReleasable => Strategy != ModuleResidencyStrategy.KeepLoaded;
}

/// <summary>
/// Parsed <c>ModuleResidency</c> configuration. Unknown keys, unknown modules, non-releasable modules
/// and out-of-range durations stop startup: a typo must not silently turn idle release back on or off.
/// </summary>
/// <remarks>
/// <code>
/// "ModuleResidency": {
///   "Strategy": "IdleDelayed",
///   "IdleGraceSeconds": 300,
///   "WakeTimeoutSeconds": 30,
///   "Modules": {
///     "relaxkonos.git": { "IdleGraceSeconds": 60 }
///   }
/// }
/// </code>
/// A module entry inherits every value it does not set from the top-level rule.
/// </remarks>
internal sealed class ModuleResidencyOptions
{
    private const int MaxSeconds = 7 * 24 * 60 * 60;

    private static readonly string[] SettingNames = ["Strategy", "IdleGraceSeconds", "WakeTimeoutSeconds"];

    private readonly IReadOnlyDictionary<string, ModuleResidencySettings> _modules;

    private ModuleResidencyOptions(ModuleResidencySettings defaults, IReadOnlyDictionary<string, ModuleResidencySettings> modules)
    {
        Default = defaults;
        _modules = modules;
    }

    internal ModuleResidencySettings Default { get; }

    internal IReadOnlyDictionary<string, ModuleResidencySettings> Modules => _modules;

    internal static ModuleResidencyOptions Parse(IConfiguration configuration,
        IReadOnlySet<string> knownModuleIds, IReadOnlySet<string> releasableModuleIds)
    {
        var section = configuration.GetSection("ModuleResidency");
        if (!section.Exists())
            return new ModuleResidencyOptions(ModuleResidencySettings.Default,
                new Dictionary<string, ModuleResidencySettings>(StringComparer.Ordinal));
        if (section.Value is not null)
            throw new InvalidOperationException("ModuleResidency must be a configuration section.");

        var defaults = ModuleResidencySettings.Default;
        IConfigurationSection? modulesSection = null;
        foreach (var setting in section.GetChildren())
        {
            if (string.Equals(setting.Key, "Modules", StringComparison.OrdinalIgnoreCase))
            {
                if (!setting.GetChildren().Any())
                    throw new InvalidOperationException($"{setting.Path} must contain at least one module entry.");
                modulesSection = setting;
                continue;
            }
            defaults = Apply(defaults, setting);
        }

        var overrides = new Dictionary<string, ModuleResidencySettings>(StringComparer.Ordinal);
        if (modulesSection is not null)
        {
            foreach (var moduleSetting in modulesSection.GetChildren())
            {
                if (!knownModuleIds.Contains(moduleSetting.Key))
                    throw new InvalidOperationException($"Unknown server module in ModuleResidency: {moduleSetting.Key}.");
                if (!releasableModuleIds.Contains(moduleSetting.Key))
                    throw new InvalidOperationException(
                        $"Server module {moduleSetting.Key} does not declare a release boundary, so ModuleResidency cannot configure it.");
                if (moduleSetting.Value is not null || !moduleSetting.GetChildren().Any())
                    throw new InvalidOperationException($"{moduleSetting.Path} must be a configuration section with at least one setting.");

                var settings = defaults;
                foreach (var setting in moduleSetting.GetChildren())
                    settings = Apply(settings, setting);
                overrides[moduleSetting.Key] = settings;
            }
        }

        return new ModuleResidencyOptions(defaults, overrides);
    }

    /// <summary>Resolved rule for one module. Unknown modules (non-residency ones) get the defaults.</summary>
    internal ModuleResidencySettings For(string moduleId) =>
        _modules.TryGetValue(moduleId, out var settings) ? settings : Default;

    internal string Describe() =>
        $"strategy={Default.Strategy}, grace={Default.IdleGracePeriod.TotalSeconds:0}s, wakeTimeout={Default.WakeTimeout.TotalSeconds:0}s"
        + (_modules.Count == 0 ? ", perModule=(none)" : ", perModule=" + string.Join(", ",
            _modules.Select(pair => $"{pair.Key}:{pair.Value.Strategy}/{pair.Value.IdleGracePeriod.TotalSeconds:0}s")));

    private static ModuleResidencySettings Apply(ModuleResidencySettings settings, IConfigurationSection setting)
    {
        if (!SettingNames.Contains(setting.Key, StringComparer.OrdinalIgnoreCase) || setting.GetChildren().Any())
            throw new InvalidOperationException($"Unknown module residency setting: {setting.Path}.");

        if (string.Equals(setting.Key, "Strategy", StringComparison.OrdinalIgnoreCase))
            return settings with { Strategy = ParseStrategy(setting) };
        if (string.Equals(setting.Key, "IdleGraceSeconds", StringComparison.OrdinalIgnoreCase))
            return settings with { IdleGracePeriod = TimeSpan.FromSeconds(ReadSeconds(setting)) };
        return settings with { WakeTimeout = TimeSpan.FromSeconds(ReadSeconds(setting)) };
    }

    private static ModuleResidencyStrategy ParseStrategy(IConfigurationSection setting) => setting.Value?.Trim() switch
    {
        null or "" => throw new InvalidOperationException($"{setting.Path} must name a residency strategy."),
        var value when value.Equals("KeepLoaded", StringComparison.OrdinalIgnoreCase) => ModuleResidencyStrategy.KeepLoaded,
        var value when value.Equals("IdleDelayed", StringComparison.OrdinalIgnoreCase) => ModuleResidencyStrategy.IdleDelayed,
        var value when value.Equals("ReleaseOnLogout", StringComparison.OrdinalIgnoreCase) => ModuleResidencyStrategy.ReleaseOnLogout,
        var value => throw new InvalidOperationException(
            $"{setting.Path} must be KeepLoaded, IdleDelayed or ReleaseOnLogout (was '{value}')."),
    };

    private static int ReadSeconds(IConfigurationSection setting)
    {
        if (!int.TryParse(setting.Value, out var seconds) || seconds < 0 || seconds > MaxSeconds)
            throw new InvalidOperationException($"{setting.Path} must be a whole number of seconds between 0 and {MaxSeconds}.");
        return seconds;
    }
}
