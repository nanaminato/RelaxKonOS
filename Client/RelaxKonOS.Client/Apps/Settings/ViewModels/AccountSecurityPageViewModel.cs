using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Identity;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed partial class AccountSecurityPageViewModel : SettingsPageViewModel, IDisposable
{
    private readonly AccountSecurityClient client;
    private readonly IAuthSession session;
    private readonly IRememberedSessionStore remembered;
    private CancellationTokenSource lifetime = new();
    private bool disposed;
    public AccountSecurityPageViewModel(ShellSettings settings, AccountSecurityClient client, IAuthSession session, IRememberedSessionStore remembered) : base(settings, null)
    {
        this.client = client; this.session = session; this.remembered = remembered;
        session.StateChanged += OnSessionChanged;
    }
    public override string Route => "account-security";
    public override string DisplayNameKey => "settings.account.title";
    public override string DisplayName => "Account & Security";
    [ObservableProperty] private AliasConfigurationDto? configuration;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool busy;
    public string SystemUsername => Configuration?.SystemUsername ?? session.CurrentUser?.Username ?? "—";
    public string Alias => Configuration?.Alias ?? T("settings.account.not_set", "Not configured");
    public string Method => session.CurrentSession?.AuthenticationMethod == "alias" ? T("settings.account.alias", "Login alias") : T("settings.account.system", "System account");
    public string SystemLogin => T(Configuration?.SystemLoginEnabled == false ? "settings.account.off" : "settings.account.on", Configuration?.SystemLoginEnabled == false ? "Off" : "On");
    public bool CanCreate => !Busy && Configuration is { Available: true, Alias: null } && session.CurrentSession?.AuthenticationMethod == "system";
    public bool CanManage => !Busy && Configuration is { Available: true, Alias: not null };
    public bool CanRestore => !Busy && Configuration is { Alias: not null };
    public string Capability => Configuration is { Available: false } ? T("settings.account.unavailable." + Configuration.UnavailableReason,
        T("settings.account.unavailable", "Account eligibility could not be confirmed. System login remains available when enabled; contact the server operator.")) : "";
    public Func<string, AliasConfigurationDto, CancellationToken, Task<object?>>? RequestOperationAsync { get; set; }
    partial void OnConfigurationChanged(AliasConfigurationDto? value) => OnPropertyChanged(string.Empty);
    partial void OnBusyChanged(bool value) => OnPropertyChanged(string.Empty);

    [RelayCommand]
    public async Task LoadAsync()
    {
        var current = lifetime;
        try
        {
            var connection = client.Capture();
            var result = await client.ReadAsync(connection, current.Token);
            if (!disposed && current == lifetime && client.IsCurrent(connection)) { Configuration = result; Status = ""; }
        }
        catch (OperationCanceledException) { }
        catch { if (current == lifetime && !disposed) Status = T("settings.account.load_failed", "Could not read account security. Reconnect or reload."); }
    }

    [RelayCommand]
    private async Task OperateAsync(string operation)
    {
        if (Busy || Configuration is not { } config || RequestOperationAsync is null) return;
        var current = lifetime;
        var connection = client.Capture();
        Busy = true;
        try
        {
            var request = await RequestOperationAsync(operation, config, current.Token);
            if (request is null || !client.IsCurrent(connection)) return;
            var result = await client.ChangeAsync(connection, request, current.Token);
            if (current != lifetime || !client.IsCurrent(connection)) return;
            if (config.Alias is not null && request is RenameAliasRequest or ChangeAliasPasswordRequest or DeleteAliasRequest)
            {
                var profiles = await remembered.LoadAsync(current.Token);
                var old = profiles.FirstOrDefault(p => SavedLoginProfile.SameProfile(p.ServerUrl, p.Username, connection.ServerUrl, config.Alias));
                var save = await remembered.RemoveAsync(connection.ServerUrl, config.Alias, current.Token);
                if (save != RememberedProfileSaveResult.Saved) Status = T("settings.account.saved_cleanup_failed", "Update the saved login for this server manually.");
                if (old is not null && request is RenameAliasRequest rename)
                    await remembered.UpsertAsync(old with { Username = rename.Alias }, current.Token);
            }
            if (request is ChangeAliasPasswordRequest or DeleteAliasRequest)
            {
                await session.LogoutAsync(current.Token);
                return;
            }
            Configuration = result;
            Status = T("settings.account.saved", "Account security updated.");
        }
        catch (OperationCanceledException) { if (current == lifetime && client.IsCurrent(connection)) await LoadAsync(); }
        catch (RelaxKonOSAuthException exception)
        {
            if (current != lifetime) return;
            await LoadAsync();
            Status = T("settings.account.error." + exception.Type.Split('/').Last(), T("settings.account.failed", "The operation was not confirmed. Review the refreshed configuration before trying again."));
        }
        catch { if (current == lifetime) { await LoadAsync(); Status = T("settings.account.failed", "The operation was not confirmed. Review the refreshed configuration before trying again."); } }
        finally { if (current == lifetime) Busy = false; }
    }
    private void OnSessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (disposed) return;
        lifetime.Cancel(); lifetime.Dispose(); lifetime = new(); Configuration = null; Status = ""; Busy = false;
        if (session.State == AuthSessionState.Authenticated) _ = LoadAsync();
    });
    public void Dispose() { disposed = true; lifetime.Cancel(); lifetime.Dispose(); session.StateChanged -= OnSessionChanged; }
}
