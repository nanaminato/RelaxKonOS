using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.VirtualSystemDrive;
using RemoteOS.Runtime;

namespace Client.Services.VirtualSystemDrive;

/// <summary>Single Host router for shortcut targets; ViewModels never interpret target strings.</summary>
public sealed class ShortcutActivationRouter
{
    private readonly ApplicationManager _applications;
    private readonly IAppActivationService _activations;
    private readonly IAutomationRunner _automation;

    public ShortcutActivationRouter(ApplicationManager applications, IAppActivationService activations, IAutomationRunner automation)
    {
        _applications = applications;
        _activations = activations;
        _automation = automation;
    }

    public async Task<ShortcutActivationResult> ActivateAsync(RemoteOsShortcut shortcut, CancellationToken cancellationToken = default)
    {
        if (!RemoteOsShortcutValidator.Validate(shortcut).IsValid)
            return new(false, VirtualSystemDriveProblemCode.ShortcutInvalid);
        return shortcut.Kind switch
        {
            RemoteOsShortcutKind.Application => Result(_applications.Launch(new AppId(shortcut.Target)), "vsd.shortcut.app-unavailable"),
            RemoteOsShortcutKind.Uri => Result(_activations.Activate(new Uri(shortcut.Target)).Succeeded, "vsd.shortcut.uri-unavailable"),
            RemoteOsShortcutKind.RemoteFile or RemoteOsShortcutKind.RemoteFolder => ActivateRemotePath(shortcut.Target),
            RemoteOsShortcutKind.Script => await ActivateScriptAsync(shortcut.Target, cancellationToken),
            _ => new ShortcutActivationResult(false, VirtualSystemDriveProblemCode.ShortcutInvalid),
        };
    }

    private ShortcutActivationResult ActivateRemotePath(string path) =>
        Result(_activations.Activate(RemoteOsActivationUris.ExplorerPath(path)).Succeeded, "vsd.shortcut.remote-target-unavailable");

    private static ShortcutActivationResult Result(bool succeeded, string failureCode) =>
        new(succeeded, succeeded ? "vsd.shortcut.completed" : failureCode);

    private async Task<ShortcutActivationResult> ActivateScriptAsync(string target, CancellationToken cancellationToken)
    {
        var result = await _automation.RunAsync(target, AutomationInvocationSource.UserShortcut, cancellationToken);
        return new ShortcutActivationResult(result.Succeeded, result.ProblemCode);
    }
}

public sealed record ShortcutActivationResult(bool Succeeded, string ProblemCode);
