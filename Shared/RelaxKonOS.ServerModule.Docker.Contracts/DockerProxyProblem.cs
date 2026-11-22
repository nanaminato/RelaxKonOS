namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Stable problem codes and diagnostic keys for the Docker proxy feature. Each value is also the
/// localization key the client resolves, so a new code needs a matching string in the Docker
/// Manager resources; an unmapped code still shows up verbatim instead of being swallowed.
/// </summary>
public static class DockerProxyProblem
{
    /// <summary>The stored proxy cannot be decrypted with the current data protection keys.</summary>
    public const string StoredValueUnreadable = "docker.proxy.problem.stored_value_unreadable";
    /// <summary>The selected managed proxy runtime has no listener on its configured port.</summary>
    public const string ManagedProxyUnavailable = "docker.proxy.problem.managed_proxy_unavailable";
    /// <summary>The saved values fail validation, so nothing was applied.</summary>
    public const string ConfigurationInvalid = "docker.proxy.problem.configuration_invalid";
    /// <summary>The daemon layer restarts Docker, so it needs an explicit confirmation.</summary>
    public const string ConfirmationRequired = "docker.proxy.problem.confirmation_required";
    /// <summary>This host has no supported mechanism for the requested layer.</summary>
    public const string PlatformUnsupported = "docker.proxy.problem.platform_unsupported";
    /// <summary>The privileged helper that owns the host configuration is unavailable.</summary>
    public const string HelperUnavailable = "docker.proxy.problem.helper_unavailable";
    /// <summary>The host configuration was rejected or could not be written.</summary>
    public const string EngineApplyFailed = "docker.proxy.problem.engine_apply_failed";
    /// <summary>The preference asks for a daemon proxy that is not installed on this host.</summary>
    public const string EngineNotApplied = "docker.proxy.problem.engine_not_applied";
    /// <summary>No Docker Desktop settings file was found for any profile.</summary>
    public const string DesktopSettingsNotFound = "docker.proxy.problem.desktop_settings_not_found";
    /// <summary>The Docker Desktop settings file is not valid JSON and was left untouched.</summary>
    public const string DesktopSettingsInvalid = "docker.proxy.problem.desktop_settings_invalid";
    /// <summary>The Docker Desktop settings file could not be read.</summary>
    public const string DesktopSettingsUnreadable = "docker.proxy.problem.desktop_settings_unreadable";
    /// <summary>Docker Desktop holds the settings file open; quitting it first releases the lock.</summary>
    public const string DesktopSettingsLocked = "docker.proxy.problem.desktop_settings_locked";
    /// <summary>Writing the Docker Desktop settings file was denied for the Server account.</summary>
    public const string DesktopSettingsDenied = "docker.proxy.problem.desktop_settings_denied";
    /// <summary>Docker Desktop could not be stopped, so its settings were not replaced.</summary>
    public const string DesktopStopFailed = "docker.proxy.problem.desktop_stop_failed";
    /// <summary>Docker Desktop did not keep the value that was written to its settings file.</summary>
    public const string DesktopSettingsNotApplied = "docker.proxy.problem.desktop_settings_not_applied";
    /// <summary>The daemon does not report proxy information, so nothing can be verified.</summary>
    public const string DaemonUnavailable = "docker.proxy.problem.daemon_unavailable";
}

/// <summary>Localization keys for the free-form explanation attached to a layer result.</summary>
public static class DockerProxyDetail
{
    /// <summary>The host is configured; its daemon must restart before the value takes effect.</summary>
    public const string RestartPending = "docker.proxy.detail.restart_pending";
    /// <summary>Docker Desktop could not be restarted by the Server and needs a manual restart.</summary>
    public const string DesktopRestartPending = "docker.proxy.detail.desktop_restart_pending";
    /// <summary>Docker Desktop is running, but it forwards to a different upstream than the saved one.</summary>
    public const string DesktopUpstreamDiffers = "docker.proxy.detail.desktop_upstream_differs";
    /// <summary>The build command carries the proxy as a build argument, so no host change was needed.</summary>
    public const string BuildOnly = "docker.proxy.detail.build_only";
    /// <summary>
    /// The build layer is installed, but the address it names is the local machine, which a build
    /// container cannot reach: inside the container that address is the container itself.
    /// </summary>
    public const string BuildLoopbackUnreachable = "docker.proxy.detail.build_loopback_unreachable";
}
