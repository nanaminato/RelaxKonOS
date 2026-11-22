using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;

namespace RelaxKonOS.App.Settings.Views;

public interface IAccountSecurityPage : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    object? Status { get; }
    Bitmap? PairingQrCode { get; }
    string Alias { get; }
    string Capability { get; }
    string Method { get; }
    string OwnerDevicePairingRequirement { get; }
    string PairingCode { get; }
    string PairingServerUrl { get; }
    string SystemLogin { get; }
    string SystemUsername { get; }
    bool CanCreate { get; }
    bool CanCreateOwnerDevicePairing { get; }
    bool CanManage { get; }
    bool CanRestore { get; }
    bool HasPairingQrCode { get; }
    bool OwnerDevicePairingSignInRequired { get; }
    ICommand CreateOwnerDevicePairingCommand { get; }
    ICommand LoadCommand { get; }
    ICommand OperateCommand { get; }
}
