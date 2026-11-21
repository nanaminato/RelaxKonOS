using System.Diagnostics;
using RelaxKonOS.Client.Apps.Docker;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Remote adapter settings, connection details, outbound proxy, and a lightweight latency test.</summary>
public sealed partial class NetworkPageViewModel : SettingsPageViewModel
{
    private readonly IAuthSession _session;
    private readonly IRelaxKonOSClient _remote;

    public NetworkPageViewModel(
        ShellSettings settings,
        IAuthSession session,
        IRelaxKonOSClient remote,
        IRemoteDockerClient docker,
        Action? save,
        HostNetworkEditorViewModel hostNetwork)
        : base(settings, save)
    {
        HostNetwork = hostNetwork;
        _session = session;
        _remote = remote;
        OutboundProxy = new DockerProxyViewModel(docker);
    }

    public HostNetworkEditorViewModel HostNetwork { get; }

    protected override void DisposeCore() => HostNetwork.Dispose();

    public override string Route => "network";
    public override string DisplayNameKey => "settings.page.network";
    public override string DisplayName => "Network";

    public string ConnectionState => _session.State switch
    {
        AuthSessionState.Authenticated => T("settings.value.connected", "Connected"),
        AuthSessionState.Connecting => T("settings.value.connecting", "Connecting…"),
        _ => T("settings.value.not_connected", "Not connected"),
    };

    public string ServerUrl => _session.EffectiveBaseUrl ?? "—";
    public string UserName => _session.CurrentUser?.Username ?? "—";
    public string WorkspaceName => _session.CurrentWorkspace?.Name ?? "—";
    /// <summary>One host-wide outbound proxy preference shared by the built-in download features.</summary>
    public DockerProxyViewModel OutboundProxy { get; }

    public Task LoadOutboundProxyAsync() => OutboundProxy.LoadAsync();

    /// <summary>Latency measurement state. The displayed text is derived so it re-localizes on a language switch.</summary>
    private enum LatencyState { NotTested, CannotTest, Testing, Measured, Failed }

    private LatencyState _latencyState = LatencyState.NotTested;
    private long _latencyMilliseconds;
    private string? _latencyFailure;

    public string LatencyText => _latencyState switch
    {
        LatencyState.CannotTest => T("settings.network.cannot_test", "Not connected; unable to test."),
        LatencyState.Testing => T("settings.network.testing", "Testing…"),
        LatencyState.Measured => $"{_latencyMilliseconds} ms",
        LatencyState.Failed => string.Format(T("settings.network.test_failed", "Failed: {0}"), _latencyFailure),
        _ => T("settings.network.not_tested", "Not tested"),
    };

    [ObservableProperty] private bool _isTesting;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        if (_session is not { State: AuthSessionState.Authenticated, EffectiveBaseUrl: { } url, Tokens: { } tokens })
        {
            _latencyState = LatencyState.CannotTest;
            OnPropertyChanged(nameof(LatencyText));
            return;
        }

        IsTesting = true;
        _latencyState = LatencyState.Testing;
        OnPropertyChanged(nameof(LatencyText));
        try
        {
            var sw = Stopwatch.StartNew();
            await _remote.GetMeAsync(url, tokens.AccessToken);
            sw.Stop();
            _latencyMilliseconds = sw.ElapsedMilliseconds;
            _latencyState = LatencyState.Measured;
        }
        catch (Exception ex)
        {
            _latencyFailure = ex.Message;
            _latencyState = LatencyState.Failed;
        }
        finally
        {
            IsTesting = false;
            OnPropertyChanged(nameof(LatencyText));
        }
    }

    private bool CanTest => !IsTesting;
    partial void OnIsTestingChanged(bool value) => TestConnectionCommand.NotifyCanExecuteChanged();
}
