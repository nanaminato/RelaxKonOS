using System.ComponentModel;
using System.Windows.Input;

namespace RelaxKonOS.App.Settings.Views;

public interface INetworkPage : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    IHostNetworkEditor HostNetwork { get; }
    IOutboundProxyEditor OutboundProxy { get; }
    string ConnectionState { get; }
    string ServerUrl { get; }
    string UserName { get; }
    string WorkspaceName { get; }
    string LatencyText { get; }
    ICommand TestConnectionCommand { get; }
}

public interface IOutboundProxyEditor : INotifyPropertyChanged
{
    bool IsEnabled { get; set; }
    bool UseCustomProxy { get; set; }
    bool UseManagedProxy { get; set; }
    string HttpProxy { get; set; }
    string HttpsProxy { get; set; }
    string NoProxy { get; set; }
    bool ApplyToEngine { get; set; }
    bool ApplyToBuild { get; set; }
    bool ApplyToImageTags { get; set; }
    bool ApplyToRuntimeDownloads { get; set; }
    string ManagedProxyEndpoint { get; }
    bool IsManagedProxyAvailable { get; }
    bool RestartsDocker { get; }
    bool IsSaving { get; }
    bool IsLoading { get; }
    object? StatusText { get; }
    object? ProblemText { get; }
    ICommand SaveCommand { get; }
    ICommand RefreshCommand { get; }
}
