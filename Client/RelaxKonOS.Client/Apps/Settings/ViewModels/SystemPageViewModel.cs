using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Common;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>「系统」页：只读展示连接与账户信息（版本 / Server URL / 用户 / Workspace / 设备 / 连接状态）。</summary>
public sealed partial class SystemPageViewModel : SettingsPageViewModel
{
    private readonly IAuthSession _session;

    public SystemPageViewModel(ShellSettings settings, IAuthSession session, Action? save)
        : base(settings, save) => _session = session;

    public override string Route => "system";
    public override string DisplayNameKey => "settings.page.system";
    public override string DisplayName => T("settings.page.system", "System");

    public string AppVersion => "RelaxKonOS 0.1";
    public string ServerUrl => _session.ServerUrl ?? T("settings.value.not_connected", "Not connected");
    public string UserName => _session.CurrentUser?.Username ?? "—";
    public string Platform => _session.CurrentUser?.Platform switch
    {
        PlatformKind.Windows => T("settings.platform.windows", "Windows"),
        PlatformKind.Linux => T("settings.platform.linux", "Linux"),
        _ => "—",
    };
    public string WorkspaceName => _session.CurrentWorkspace?.Name ?? "—";
    public string DeviceName => _session.CurrentDevice?.Name ?? "—";
    public string DeviceRole => _session.AssignedRole switch
    {
        RelaxKonOS.Protocol.Workspace.DeviceRole.Controller => T("settings.device_role.controller", "Controller"),
        _ => T("settings.device_role.observer", "Observer"),
    };
    public string ConnectionState => _session.State switch
    {
        AuthSessionState.Authenticated => T("settings.value.connected", "Connected"),
        AuthSessionState.Connecting => T("settings.value.connecting", "Connecting…"),
        _ => T("settings.value.not_connected", "Not connected"),
    };
    public string UserCreated => _session.CurrentUser?.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string LastLogin => _session.CurrentUser?.LastLoginAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "—";

    /// <summary>Provided by SettingsApp so system-property actions always open in a child window.</summary>
    public Func<Task>? RequestEnvironmentVariablesAsync { get; set; }
    public Func<Task>? RequestPerformanceOptionsAsync { get; set; }

    [RelayCommand]
    private Task OpenEnvironmentVariablesAsync() => RequestEnvironmentVariablesAsync?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private Task OpenPerformanceOptionsAsync() => RequestPerformanceOptionsAsync?.Invoke() ?? Task.CompletedTask;
}
