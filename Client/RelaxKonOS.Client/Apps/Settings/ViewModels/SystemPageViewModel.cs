using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Common;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>「系统」页：连接与账户信息（版本 / Server URL / 用户 / Workspace / 设备 / 连接状态），
/// 归属远程机器的真实主机名编辑入口，以及服务端官方模块清单。
///
/// <para>
/// 模块清单是只读的：它回答「为什么某个应用说缺少服务端能力」，并把停用会被拒绝的两个原因
/// （还有未收尾的操作、有启用中的模块依赖它）直接写在对应模块上。真正的启停仍然只能改服务端
/// 配置并重启，这一页不会也不能改变服务端状态。
/// </para>
/// </summary>
public sealed partial class SystemPageViewModel : SettingsPageViewModel
{
    private readonly IAuthSession _session;
    private readonly IServerModuleCatalog _modules;
    public Func<Task>? RequestServerHttpsAsync { get; set; }
    [RelayCommand]
    private Task OpenServerHttpsAsync() => RequestServerHttpsAsync?.Invoke() ?? Task.CompletedTask;

    public Action? ClearUsageMemoryAction { get; set; }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private LocalizedStatus? _usageMemoryStatus;
    [RelayCommand]
    private void ClearUsageMemory()
    {
        ClearUsageMemoryAction?.Invoke();
        UsageMemoryStatus = LocalizedText.Ref("settings.usage_memory.cleared");
    }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private IReadOnlyList<ServerModuleRow> _serverModules = [];
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _serverModulesStatus = "";
    public bool HasServerModules => ServerModules.Count > 0;
    public bool HasServerModulesStatus => ServerModulesStatus.Length > 0;

    public SystemPageViewModel(ShellSettings settings, IAuthSession session, Action? save, HostIdentityEditorViewModel hostIdentity,
        IServerModuleCatalog modules)
        : base(settings, save)
    {
        _session = session;
        _modules = modules;
        HostIdentity = hostIdentity;
    }

    partial void OnServerModulesChanged(IReadOnlyList<ServerModuleRow> value) => OnPropertyChanged(nameof(HasServerModules));
    partial void OnServerModulesStatusChanged(string value) => OnPropertyChanged(nameof(HasServerModulesStatus));

    /// <summary>
    /// Reads the inventory for display. Resolving the catalog's cache first keeps a page visit from
    /// turning into a request when the login already prefetched it; a failed read reports itself
    /// instead of silently showing an empty list, which would read as "this server has no modules".
    /// </summary>
    public async Task RefreshServerModulesAsync()
    {
        if (_session.State != AuthSessionState.Authenticated)
        {
            ServerModules = [];
            ServerModulesStatus = "";
            return;
        }

        var modules = await _modules.GetAsync();
        if (modules is null)
        {
            ServerModules = [];
            ServerModulesStatus = T("settings.server_modules.unavailable", "The server did not report its modules.");
            return;
        }

        ServerModules = BuildRows(modules, T);
        ServerModulesStatus = "";
    }

    [RelayCommand]
    private Task RefreshServerModules() => RefreshServerModulesAsync();

    /// <summary>
    /// Turns the protocol inventory into display rows. Kept pure and static so the wording of the
    /// two refusal reasons can be checked without building a window or a live login.
    /// </summary>
    public static IReadOnlyList<ServerModuleRow> BuildRows(IReadOnlyList<ServerModuleDto> modules,
        Func<string, string, string> localize)
    {
        var rows = new List<ServerModuleRow>(modules.Count);
        foreach (var module in modules)
        {
            var notes = new List<string>();
            if (module.RequiredBy.Count > 0)
                notes.Add(string.Format(localize("settings.server_modules.required_by",
                        "Required by {0}; disabling it would stop the next startup."),
                    string.Join(", ", module.RequiredBy)));
            if (module.OrphanedWork.Count > 0)
                notes.Add(string.Format(localize("settings.server_modules.orphaned",
                        "{0} unfinished operation(s): {1}"),
                    module.OrphanedWork.Count, string.Join("; ", module.OrphanedWork)));
            rows.Add(new ServerModuleRow(module.Id, module.Enabled,
                module.Enabled
                    ? localize("settings.server_modules.enabled", "Enabled")
                    : localize("settings.server_modules.disabled", "Disabled"),
                string.Join(", ", module.Capabilities),
                string.Join(" ", notes)));
        }
        return rows;
    }

    public override string Route => "system";
    public override string DisplayNameKey => "settings.page.system";
    public override string DisplayName => T("settings.page.system", "System");

    public string AppVersion => "RelaxKonOS 0.1";
    public string ServerUrl => _session.EffectiveBaseUrl ?? T("settings.value.not_connected", "Not connected");
    public string UserName => _session.CurrentUser?.Username ?? "—";
    public string Platform => _session.CurrentUser?.Platform switch
    {
            HostPlatformKind.Windows => T("settings.platform.windows", "Windows"),
            HostPlatformKind.Linux => T("settings.platform.linux", "Linux"),
        _ => "—",
    };
    public string ServerMode => _session.CurrentServer?.Host?.Mode switch
    {
        RelaxKonOS.Protocol.Common.ServerMode.User when _session.CurrentServer?.Platform == HostPlatformKind.Windows
            => T("settings.server_mode.windows_personal", "Windows personal mode"),
        RelaxKonOS.Protocol.Common.ServerMode.User => T("settings.server_mode.user", "Current Linux user mode"),
        RelaxKonOS.Protocol.Common.ServerMode.System => T("settings.server_mode.system", "System mode"),
        _ => "—",
    };
    public string ExecutionIdentity => _session.CurrentServer?.Host is { } host
        ? $"{host.ExecutionIdentity.Username} (uid {host.ExecutionIdentity.Uid})"
        : "—";
    public string ListenerScope => _session.CurrentServer?.Host?.Listener.Scope ?? "—";
    public string ConnectionGuidance => _session.CurrentServer?.Host?.Mode == RelaxKonOS.Protocol.Common.ServerMode.User
        ? _session.CurrentServer.Platform == HostPlatformKind.Windows
            ? T("settings.server_mode.windows_personal_hint", "This installation belongs to the current Windows account. Pair other devices from an owner-device session using a reachable LAN address.")
            : T("settings.server_mode.user_hint", "This server is limited to the current Linux account and loopback. Connect remotely through SSH local forwarding.")
        : "";
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
    public Func<Task>? RequestWorkspaceEnvironmentAsync { get; set; }
    [RelayCommand]
    private Task OpenWorkspaceEnvironmentAsync() => RequestWorkspaceEnvironmentAsync?.Invoke() ?? Task.CompletedTask;
    public Func<Task>? RequestPerformanceOptionsAsync { get; set; }

    /// <summary>The remote machine's host name; it is never the client device's own name.</summary>
    public HostIdentityEditorViewModel HostIdentity { get; }

    protected override void DisposeCore() => HostIdentity.Dispose();

    [RelayCommand]
    private Task OpenEnvironmentVariablesAsync() => RequestEnvironmentVariablesAsync?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private Task OpenPerformanceOptionsAsync() => RequestPerformanceOptionsAsync?.Invoke() ?? Task.CompletedTask;
}

/// <summary>One official server module as the System page shows it. Display-only; no action is bound to it.</summary>
public sealed record ServerModuleRow(string Id, bool Enabled, string StateText, string CapabilitiesText, string NotesText)
{
    public bool HasNotes => NotesText.Length > 0;
    public bool HasCapabilities => CapabilitiesText.Length > 0;
}
