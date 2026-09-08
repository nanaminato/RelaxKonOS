using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Services;

/// <summary>
/// Evaluates package-declared client and server requirements before the runtime activates an app.
/// It deliberately reads the server descriptor from the authenticated session instead of UserDto,
/// whose platform is the platform of the connecting client device.
/// </summary>
public sealed class ApplicationCompatibilityService : IApplicationCompatibilityEvaluator, IApplicationCompatibilityNotifier
{
    private static readonly AppId SystemAppId = new("remoteos.system");
    private readonly IAuthSession _session;
    private readonly IWindowManager _windows;
    private readonly LocalizationService _localization;

    public ApplicationCompatibilityService(IAuthSession session, IWindowManager windows, LocalizationService localization)
    {
        _session = session;
        _windows = windows;
        _localization = localization;
    }

    public ApplicationCompatibilityResult Evaluate(ApplicationManifest manifest)
    {
        var clientPlatform = DetectClientPlatform();
        if (manifest.SupportedClientPlatforms.Count > 0 &&
            !manifest.SupportedClientPlatforms.Contains(clientPlatform, StringComparer.Ordinal))
        {
            return new(ApplicationCompatibilityStatus.ClientPlatformMismatch,
                string.Join(", ", manifest.SupportedClientPlatforms), clientPlatform);
        }

        var requirements = manifest.Server;
        if (requirements.SupportedPlatforms.Count == 0 && requirements.RequiredCapabilities.Count == 0)
            return ApplicationCompatibilityResult.Compatible;

        var server = _session.CurrentServer;
        if (server is null)
            return new(ApplicationCompatibilityStatus.ServerUnavailable);

        var serverPlatform = ToManifestPlatform(server.Platform);
        if (requirements.SupportedPlatforms.Count > 0 &&
            !requirements.SupportedPlatforms.Contains(serverPlatform, StringComparer.Ordinal))
        {
            return new(ApplicationCompatibilityStatus.ServerPlatformMismatch,
                string.Join(", ", requirements.SupportedPlatforms), serverPlatform);
        }

        var missingCapability = requirements.RequiredCapabilities.FirstOrDefault(required =>
            !server.Capabilities.Contains(required, StringComparer.Ordinal));
        return missingCapability is null
            ? ApplicationCompatibilityResult.Compatible
            : new(ApplicationCompatibilityStatus.MissingServerCapability, missingCapability);
    }

    public void Notify(ApplicationManifest manifest, ApplicationCompatibilityResult result)
    {
        var title = T("app_compatibility.title", "Application unavailable");
        // Package-localized metadata is exposed to desktop entries. The manifest itself remains
        // deliberately small, so use its stable display name for a system diagnostic.
        var appName = manifest.DisplayName;
        var message = result.Status switch
        {
            ApplicationCompatibilityStatus.ClientPlatformMismatch => string.Format(
                T("app_compatibility.client_platform", "{0} supports only these client platforms: {1}. This client is {2}."),
                appName, result.Expected, result.Actual),
            ApplicationCompatibilityStatus.ServerPlatformMismatch => string.Format(
                T("app_compatibility.server_platform", "{0} supports only these server platforms: {1}. The connected server is {2}."),
                appName, result.Expected, result.Actual),
            ApplicationCompatibilityStatus.MissingServerCapability => string.Format(
                T("app_compatibility.missing_capability", "{0} requires a server capability that is unavailable: {1}."),
                appName, result.Expected),
            ApplicationCompatibilityStatus.ServerUnavailable => string.Format(
                T("app_compatibility.server_unavailable", "{0} requires a connected RelaxKonOS Server before it can start."), appName),
            _ => T("app_compatibility.generic", "This application cannot run in the current environment."),
        };

        _windows.Create(new WindowCreateOptions(
            OwnerAppId: SystemAppId,
            Title: title,
            Content: new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(24),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                },
            },
            IconGlyph: "⚠",
            CanResize: false,
            CanMinimize: false,
            CanMaximize: false));
    }

    private static string DetectClientPlatform() => OperatingSystem.IsWindows()
        ? ApplicationPlatformNames.Windows
        : ApplicationPlatformNames.Linux;

    private static string ToManifestPlatform(PlatformKind platform) => platform == PlatformKind.Windows
        ? ApplicationPlatformNames.Windows
        : ApplicationPlatformNames.Linux;

    private string T(string key, string fallback) => _localization.Get(key, fallback);
}
