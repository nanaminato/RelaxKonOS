using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.VirtualSystemDrive;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;

namespace Client.Services.VirtualSystemDrive;

public interface IAutomationRunner
{
    Task<AutomationRunResult> RunAsync(string scriptRelativePath, AutomationInvocationSource source,
        CancellationToken cancellationToken = default);
}

public interface IAutomationNotificationSink
{
    void Notify(string title, string message);
}

/// <summary>
/// Executes a finite allow-list of Host APIs. It intentionally has no process, filesystem,
/// reflection, HTTP, environment, or arbitrary URI adapter.
/// </summary>
public sealed class AutomationRunner : IAutomationRunner
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly VirtualSystemDrive _drive;
    private readonly ApplicationManager _applications;
    private readonly IAppActivationService _activations;
    private readonly IAutomationNotificationSink _notifications;
    private readonly IWindowManager _windows;

    public AutomationRunner(VirtualSystemDrive drive, ApplicationManager applications,
        IAppActivationService activations, IAutomationNotificationSink notifications, IWindowManager windows)
    {
        _drive = drive;
        _applications = applications;
        _activations = activations;
        _notifications = notifications;
        _windows = windows;
    }

    public async Task<AutomationRunResult> RunAsync(string scriptRelativePath, AutomationInvocationSource source,
        CancellationToken cancellationToken = default)
    {
        if (source is not AutomationInvocationSource.UserShortcut and not AutomationInvocationSource.UserScriptLibrary)
            return await CompleteAsync("<denied>", source, false, "vsd.script.user-initiation-required", 0, cancellationToken);
        if (!ApplicationDescriptorValidator.IsSafeRelativePath(scriptRelativePath)
            || !scriptRelativePath.StartsWith("Scripts/", StringComparison.Ordinal))
            return await CompleteAsync("<invalid>", source, false, VirtualSystemDriveProblemCode.ScriptInvalid, 0, cancellationToken);

        if (!await Gate.WaitAsync(0, cancellationToken))
            return await CompleteAsync("<busy>", source, false, "vsd.script.busy", 0, cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var scriptPath = _drive.ResolveRootChild($"Users/{_drive.LocalProfileId}/{scriptRelativePath}");
            var workflow = await _drive.ReadJsonAsync<AutomationWorkflow>(scriptPath, timeout.Token);
            if (!AutomationWorkflowValidator.Validate(workflow).IsValid)
                return await CompleteAsync(workflow.Id, source, false, VirtualSystemDriveProblemCode.ScriptInvalid, 0, cancellationToken);

            var completed = 0;
            foreach (var step in workflow.Steps)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var code = await ExecuteAsync(step, timeout.Token);
                if (code is not null)
                    return await CompleteAsync(workflow.Id, source, false, code, completed, cancellationToken);
                completed++;
            }
            return await CompleteAsync(workflow.Id, source, true, "vsd.script.completed", completed, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await CompleteAsync("<cancelled>", source, false, "vsd.script.cancelled", 0, CancellationToken.None);
        }
        catch (VirtualSystemDriveException exception)
        {
            return await CompleteAsync("<invalid>", source, false, exception.ProblemCode, 0, CancellationToken.None);
        }
        finally { Gate.Release(); }
    }

    private Task<string?> ExecuteAsync(AutomationStep step, CancellationToken cancellationToken) => step.Action switch
    {
        "app.launch" => Task.FromResult(_applications.Launch(new AppId(step.AppId!)) ? null : "vsd.script.app-unavailable"),
        "uri.activate" => Task.FromResult<string?>(_activations.Activate(new AppActivationRequest(new Uri(step.Uri!))).Succeeded ? null : "vsd.script.uri-unavailable"),
        "shell.notify" => NotifyAsync(step),
        "delay" => DelayAsync(step.Milliseconds!.Value, cancellationToken),
        "remote-file.open" or "remote-folder.open" => Task.FromResult<string?>(_activations.Activate(new AppActivationRequest(RemoteOsActivationUris.ExplorerPath(step.Target!))).Succeeded
            ? null : "vsd.script.remote-target-unavailable"),
        "window.focus" => Task.FromResult(Focus(step.WindowId!.Value)),
        "window.close" => Task.FromResult(Close(step.WindowId!.Value)),
        _ => Task.FromResult<string?>(VirtualSystemDriveProblemCode.ScriptInvalid),
    };

    private string? Focus(int windowId)
    {
        var window = _windows.Windows.FirstOrDefault(candidate => candidate.Info.Id.Value == windowId);
        if (window is null) return "vsd.script.window-unavailable";
        _windows.Restore(window);
        _windows.Focus(window);
        return null;
    }

    private string? Close(int windowId)
    {
        var window = _windows.Windows.FirstOrDefault(candidate => candidate.Info.Id.Value == windowId);
        if (window is null) return "vsd.script.window-unavailable";
        _windows.Close(window);
        return null;
    }

    private Task<string?> NotifyAsync(AutomationStep step)
    {
        _notifications.Notify(step.Title!, step.Message!);
        return Task.FromResult<string?>(null);
    }

    private static async Task<string?> DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        await Task.Delay(milliseconds, cancellationToken);
        return null;
    }

    private async Task<AutomationRunResult> CompleteAsync(string id, AutomationInvocationSource source,
        bool succeeded, string code, int steps, CancellationToken cancellationToken)
    {
        var result = new AutomationRunResult(id, source, succeeded, code, steps, DateTimeOffset.UtcNow);
        try
        {
            var audit = _drive.ResolveRootChild($"System/automation-audit/{Guid.NewGuid():N}.json");
            await _drive.WriteJsonAtomicallyAsync(audit, result, cancellationToken);
        }
        catch (Exception) { }
        return result;
    }
}

public sealed class DiagnosticAutomationNotificationSink(IAppActivationDiagnostics diagnostics) : IAutomationNotificationSink
{
    public void Notify(string title, string message) => diagnostics.Record($"Automation notification: title={title.Length}chars, message={message.Length}chars.");
}

public sealed record AutomationRunResult(string ScriptId, AutomationInvocationSource Source, bool Succeeded,
    string ProblemCode, int CompletedSteps, DateTimeOffset CompletedAtUtc);
