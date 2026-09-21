using System.ComponentModel;
using System.Reflection;
using RelaxKonOS.Client.Foundation.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;

namespace RelaxKonOS.Client.Mobile.ViewModels;

public sealed class MobileShellViewModel(IAuthSession session) : INotifyPropertyChanged
{
    private string _serverUrl = "http://10.0.2.2:5090";
    private string _identifier = string.Empty;
    private string _password = string.Empty;
    private string _status = "Connect to a RelaxKonOS server.";
    private bool _isConnecting;
    private bool _isAuthenticated;
    private string _serverName = "RelaxKonOS";

    public event PropertyChangedEventHandler? PropertyChanged;
    public string ServerUrl { get => _serverUrl; set => Set(ref _serverUrl, value); }
    public string Identifier { get => _identifier; set => Set(ref _identifier, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsConnecting { get => _isConnecting; private set => Set(ref _isConnecting, value); }
    public bool IsAuthenticated { get => _isAuthenticated; private set => Set(ref _isAuthenticated, value); }
    public string ServerName { get => _serverName; private set => Set(ref _serverName, value); }

    public async Task ConnectAsync()
    {
        if (IsConnecting) return;
        IsConnecting = true;
        Status = "Connecting…";
        try
        {
            var response = await session.LoginAsync(ServerUrl.Trim(), new LoginRequest(
                Identifier.Trim(), Password, ClientPlatformKind.Android, Environment.MachineName,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0"),
                rememberConnection: true);
            ServerName = response.Workspace.Name;
            Password = string.Empty;
            IsAuthenticated = true;
            Status = "Connected";
        }
        catch (Exception exception) when (exception is HttpRequestException or UriFormatException)
        {
            Status = exception.Message;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
