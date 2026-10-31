using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ComponentCenter;

namespace RelaxKonOS.Client.Services;

/// <summary>
/// Evaluates package-declared client and server requirements before the runtime activates an app.
/// It deliberately reads the server descriptor from the authenticated session instead of UserDto,
/// whose platform is the platform of the connecting client device.
///
/// <para>
/// A missing capability is answered from the server's own module inventory when it is available:
/// "the module that provides it is disabled" is actionable, while a bare capability name is not.
/// The inventory is diagnostic only, so the explanation is strictly additive — an unread inventory
/// leaves the previous message intact rather than changing the verdict.
/// </para>
/// </summary>
public sealed class ApplicationCompatibilityService : IApplicationCompatibilityEvaluator, IApplicationCompatibilityNotifier
{
    private static readonly AppId SystemAppId = new("relaxkonos.system");
    private readonly IAuthSession _session;
    private readonly IWindowManager _windows;
    private readonly LocalizationService _localization;
    private readonly IServerModuleCatalog _modules;
    private readonly IAppActivationService _activations;

    public ApplicationCompatibilityService(IAuthSession session, IWindowManager windows, LocalizationService localization,
        IServerModuleCatalog modules, IAppActivationService activations)
    {
        _session = session;
        _windows = windows;
        _localization = localization;
        _modules = modules;
        _activations = activations;
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
        if (missingCapability is null) return ApplicationCompatibilityResult.Compatible;
        // Start the diagnostic read now so the dialog that follows can name the module instead of
        // repeating the capability. Never awaited: a slow or absent inventory must not delay a launch.
        _modules.EnsureLoaded();
        return new(ApplicationCompatibilityStatus.MissingServerCapability, missingCapability);
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
            ApplicationCompatibilityStatus.MissingServerCapability => MissingCapabilityMessage(appName, result.Expected),
            ApplicationCompatibilityStatus.ServerUnavailable => string.Format(
                T("app_compatibility.server_unavailable", "{0} requires a connected RelaxKonOS Server before it can start."), appName),
            _ => T("app_compatibility.generic", "This application cannot run in the current environment."),
        };

        var content = new StackPanel
        {
            Spacing = 12, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = title, FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap } }
        };
        var explanation = result.Status == ApplicationCompatibilityStatus.MissingServerCapability
            ? ServerCapabilityExplanation.Explain(_modules.Cached, result.Expected ?? "") : ServerCapabilityExplanation.Unknown;
        if (explanation.ModuleId is { } moduleId && _session.ServiceId is { } host)
        {
            var login = _session.CurrentSession;
            var manage = new Button { Content = T("server_modules.open_detail", "View server module") };
            manage.Click += (_, _) =>
            {
                if (_session.State == AuthSessionState.Authenticated && _session.ServiceId == host && ReferenceEquals(login, _session.CurrentSession))
                    _activations.Activate(new AppActivationRequest(ComponentCenterNavigation.ServerModules(host, moduleId), SystemAppId));
            };
            content.Children.Add(manage);
        }
        _windows.Create(new WindowCreateOptions(
            OwnerAppId: SystemAppId,
            Title: title,
            Content: content,
            IconGlyph: "⚠",
            CanResize: false,
            CanMinimize: false,
            CanMaximize: false));
    }

    /// <summary>
    /// The base sentence names the capability; the second line says who withholds it. When the
    /// inventory is unavailable the second line is absent — an unverified guess about the cause is
    /// worse than no cause at all.
    /// </summary>
    private string MissingCapabilityMessage(string appName, string? capability)
    {
        var message = string.Format(
            T("app_compatibility.missing_capability", "{0} requires a server capability that is unavailable: {1}."),
            appName, capability);
        var explanation = ServerCapabilityExplanation.Explain(_modules.Cached, capability ?? string.Empty);
        var reason = explanation.Availability switch
        {
            ServerCapabilityAvailability.ProvidedByDisabledModule => string.Format(
                T("app_compatibility.reason_module_disabled",
                    "The server module that provides it ({0}) is disabled on this server."), explanation.ModuleId),
            ServerCapabilityAvailability.ProvidedByEnabledModule => string.Format(
                T("app_compatibility.reason_module_enabled",
                    "The module that provides it ({0}) is enabled, so the server's deployment mode or host platform withholds it."),
                explanation.ModuleId),
            ServerCapabilityAvailability.NotProvidedByAnyModule => T("app_compatibility.reason_no_module",
                "No official server module provides this capability; the server's deployment mode or platform withholds it."),
            _ => null,
        };
        return reason is null ? message : message + Environment.NewLine + reason;
    }

    private static string DetectClientPlatform() => OperatingSystem.IsWindows()
        ? ApplicationPlatformNames.Windows
        : ApplicationPlatformNames.Linux;

    private static string ToManifestPlatform(HostPlatformKind platform) => platform == HostPlatformKind.Windows
        ? ApplicationPlatformNames.Windows
        : ApplicationPlatformNames.Linux;

    private string T(string key, string fallback) => _localization.Get(key, fallback);
}
