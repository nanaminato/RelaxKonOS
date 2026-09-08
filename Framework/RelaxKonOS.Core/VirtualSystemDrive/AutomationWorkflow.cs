namespace RelaxKonOS.Core.VirtualSystemDrive;

/// <summary>Non-executable, declarative VSD automation document.</summary>
public sealed record AutomationWorkflow(int SchemaVersion, string Id, string Name, IReadOnlyList<AutomationStep> Steps);
public enum AutomationInvocationSource { UserShortcut, UserScriptLibrary, Unknown }
public sealed record AutomationStep(string Action, string? AppId = null, string? Uri = null,
    int? Milliseconds = null, string? Title = null, string? Message = null, string? Target = null, int? WindowId = null);

public static class AutomationWorkflowValidator
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSteps = 32;

    public static DescriptorValidationResult Validate(AutomationWorkflow? workflow)
    {
        if (workflow is null || workflow.SchemaVersion != CurrentSchemaVersion || !Guid.TryParse(workflow.Id, out _)
            || string.IsNullOrWhiteSpace(workflow.Name) || workflow.Steps is null || workflow.Steps.Count > MaximumSteps)
            return DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.ScriptInvalid);
        foreach (var step in workflow.Steps)
        {
            if (step is null || !ValidStep(step))
                return DescriptorValidationResult.Invalid(VirtualSystemDriveProblemCode.ScriptInvalid);
        }
        return DescriptorValidationResult.Valid;
    }

    private static bool ValidStep(AutomationStep step) => step.Action switch
    {
        "app.launch" => ApplicationDescriptorValidator.IsValidAppId(step.AppId),
        "uri.activate" => Uri.TryCreate(step.Uri, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals("relaxkonos", StringComparison.OrdinalIgnoreCase),
        "shell.notify" => !string.IsNullOrWhiteSpace(step.Title) && !string.IsNullOrWhiteSpace(step.Message)
            && step.Title.Length <= 160 && step.Message.Length <= 1000,
        "delay" => step.Milliseconds is > 0 and <= 30_000,
        "remote-file.open" or "remote-folder.open" => IsRemotePath(step.Target),
        "window.focus" or "window.close" => step.WindowId is > 0,
        _ => false,
    };

    // RelaxKonOS remote paths commonly begin with '/', so do not confuse them with host-local
    // file URIs. Only an explicit URI scheme/network form is disallowed here; Explorer applies
    // its existing remote-path authorization when the action is executed.
    private static bool IsRemotePath(string? path) => !string.IsNullOrWhiteSpace(path) && !path.Contains('\0')
        && !path.Contains("://", StringComparison.Ordinal) && !path.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
}
