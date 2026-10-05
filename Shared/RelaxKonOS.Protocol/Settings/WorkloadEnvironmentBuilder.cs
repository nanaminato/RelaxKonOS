namespace RelaxKonOS.Protocol.Settings;

/// <summary>Builds ordinary user workload data. Never use for Helper or privileged process startup.</summary>
public static class WorkloadEnvironmentBuilder
{
    public static Dictionary<string, string> Build(IReadOnlyList<EnvironmentVariable> machine,
        IReadOnlyList<EnvironmentVariable> user, IReadOnlyList<EnvironmentVariable> workspace,
        EnvironmentPathMode pathMode, bool windows)
    {
        if (!Enum.IsDefined(pathMode)) throw new ArgumentException("settings.environment.invalid_path_mode");
        if (!windows && user.Count != 0) throw new ArgumentException("settings.environment.linux_user_scope_unsupported");
        var comparer = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var values = new Dictionary<string, EnvironmentVariable>(comparer);
        Merge(machine, SettingsScope.HostMachine, false);
        Merge(user, SettingsScope.HostUser, windows);
        Merge(workspace, SettingsScope.Workspace, pathMode == EnvironmentPathMode.Append);
        var raw = values.ToDictionary(pair => pair.Key, pair => pair.Value.RawValue!, comparer);
        var result = new Dictionary<string, string>(comparer);
        foreach (var pair in values)
        {
            if (!windows || pair.Value.ValueKind == EnvironmentValueKind.String)
            { result[pair.Key] = pair.Value.RawValue!; continue; }
            var expanded = EnvironmentExpansion.Expand(pair.Value.RawValue!, raw, true);
            if (expanded.Warnings.Any(warning => warning != "settings.environment.unresolved_reference"))
                throw new ArgumentException("settings.environment.invalid_expansion");
            result[pair.Key] = expanded.Value;
        }
        if (result.Values.Any(value => value.Length > EnvironmentValidation.MaximumValueLength)
            || result.Sum(pair => System.Text.Encoding.UTF8.GetByteCount(pair.Key) + System.Text.Encoding.UTF8.GetByteCount(pair.Value)) > EnvironmentValidation.MaximumChangeBytes)
            throw new ArgumentException("settings.environment.workload_too_large");
        return result;

        void Merge(IReadOnlyList<EnvironmentVariable> layer, SettingsScope expectedScope, bool appendPath)
        {
            if (layer.Count == 0) return;
            if (layer.Any(value => value is null || value.Masked || value.RawValue is null || value.Source != expectedScope))
                throw new ArgumentException("settings.environment.invalid_source");
            if (layer.Select(value => value.Name).Distinct(comparer).Count() != layer.Count)
                throw new ArgumentException("settings.environment.duplicate_name");
            foreach (var chunk in layer.Chunk(EnvironmentValidation.MaximumChanges))
            {
                var change = new EnvironmentChangeSet(chunk.Select(value => new EnvironmentMutation(value.Name,
                    EnvironmentMutationKind.Set, value.RawValue, value.ValueKind)).ToArray(), true);
                if (EnvironmentValidation.Validate(change, windows) is { } problem) throw new ArgumentException(problem);
            }
            foreach (var value in layer)
            {
                var next = value;
                if (appendPath && value.Name.Equals("PATH", windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    && values.TryGetValue(value.Name, out var inherited))
                    next = value with { RawValue = inherited.RawValue + (windows ? ";" : ":") + value.RawValue,
                        ValueKind = inherited.ValueKind == EnvironmentValueKind.ExpandString ? inherited.ValueKind : value.ValueKind };
                values[value.Name] = next;
            }
        }
    }
}
