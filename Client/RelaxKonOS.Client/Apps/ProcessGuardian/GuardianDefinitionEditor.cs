using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Client.Apps.ProcessGuardian;

/// <summary>Updates visible desktop fields without resetting the complete saved declaration.</summary>
public static class GuardianDefinitionEditor
{
    public static ProcessDefinitionDto Merge(ProcessDefinitionDto? baseline, string id, string name,
        string executable, string argumentsText, string directory, bool enabledOnBoot, string runAs, bool windows)
    {
        if (baseline is not null && baseline.Id != id.Trim()) throw new InvalidOperationException("Guardian editor target changed.");
        var arguments = baseline is not null && argumentsText == string.Join(Environment.NewLine, baseline.Arguments)
            ? baseline.Arguments
            : string.IsNullOrEmpty(argumentsText) ? Array.Empty<string>()
            : argumentsText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var sameAccount = string.Equals(baseline?.RunAs?.Trim(), runAs.Trim(), windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return (baseline ?? new ProcessDefinitionDto(id.Trim(), name.Trim(), executable.Trim(), arguments, directory.Trim())) with
        {
            Id = id.Trim(), Name = name.Trim(), ExecutablePath = executable.Trim(), Arguments = arguments,
            WorkingDirectory = directory, EnabledOnBoot = enabledOnBoot, RunAs = runAs.Trim(),
            RunAsIdentity = sameAccount ? baseline?.RunAsIdentity : null,
        };
    }
}
