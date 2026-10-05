namespace RelaxKonOS.Protocol.Settings;

/// <summary>Server-resolved Workspace overrides for an ordinary user terminal, never a Helper launch environment.</summary>
public sealed record TerminalEnvironmentOverrides(IReadOnlyList<EnvironmentMutation> Values, EnvironmentPathMode PathMode)
{
    public bool IsValid(bool windows) => Enum.IsDefined(PathMode) && Values is { Count: <= EnvironmentValidation.MaximumChanges }
        && Values.All(value => value is not null && value.Operation == EnvironmentMutationKind.Set)
        && (Values.Count == 0 || EnvironmentValidation.Validate(new(Values, true), windows) is null);

    public Dictionary<string, string> Apply(IReadOnlyDictionary<string, string> baseline, bool windows)
    {
        if (!IsValid(windows)) throw new ArgumentException("settings.environment.invalid_terminal_overrides");
        EnvironmentVariable Variable(string name, string value, SettingsScope scope, EnvironmentValueKind kind = EnvironmentValueKind.String)
            => new(name, value, null, kind, scope, false, false, []);
        return WorkloadEnvironmentBuilder.Build(baseline.Where(pair => !pair.Key.StartsWith('=')).Select(pair => Variable(pair.Key, pair.Value, SettingsScope.HostMachine)).ToArray(), [],
            Values.Select(value => Variable(value.Name, value.Value!, SettingsScope.Workspace, value.ValueKind)).ToArray(), PathMode, windows);
    }
}
