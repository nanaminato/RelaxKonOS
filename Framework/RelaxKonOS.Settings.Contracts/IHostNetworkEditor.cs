using System.ComponentModel;
using System.Windows.Input;

namespace RelaxKonOS.App.Settings.Views;

public interface INetworkAdapterPage
{
    IHostNetworkEditor Editor { get; }
}

public interface IHostNetworkEditor : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    IReadOnlyList<INetworkAdapterItem> Adapters { get; }
    INetworkAdapterItem? SelectedAdapter { get; }
    void OpenAdapter(INetworkAdapterItem adapter);
    bool AutomaticDns { get; set; }
    bool Dhcp { get; set; }
    string Address { get; set; }
    string DnsServers { get; set; }
    string Gateway { get; set; }
    string PrefixLength { get; set; }
    bool CanEdit { get; }
    bool HasDetails { get; }
    bool HasPendingConfirmation { get; }
    bool HasProblem { get; }
    bool HasUnavailableReason { get; }
    bool IsBusy { get; }
    string ConfirmationText { get; }
    string Problem { get; }
    string Target { get; }
    string UnavailableReason { get; }
    ICommand ApplyCommand { get; }
    ICommand ConfirmCommand { get; }
    ICommand ReloadCommand { get; }
}

public interface INetworkAdapterItem : INotifyPropertyChanged
{
    string Name { get; }
    string Kind { get; }
    string Status { get; }
    string Description { get; }
    string IpAddresses { get; }
    string Gateways { get; }
    string Dns { get; }
    string Mac { get; }
    string Speed { get; }
    bool IsWifi { get; }
}
