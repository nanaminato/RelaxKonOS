using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Git;

public sealed partial class GitClientViewModel
{
    public ObservableCollection<GitCredentialAccountDto> CredentialAccounts { get; } = [];
    public Func<Task>? ShowCredentialAccountsAsync { get; set; }
    [ObservableProperty] private GitCredentialAccountDto? _selectedCredentialAccount;
    [ObservableProperty] private string _credentialHost = "";
    [ObservableProperty] private string _credentialUsername = "";
    [ObservableProperty] private string _credentialToken = "";
    [ObservableProperty] private bool _credentialDefault = true;
    [ObservableProperty] private bool _credentialAccountsBusy;
    [ObservableProperty] private string _credentialAccountsMessage = "";

    partial void OnSelectedCredentialAccountChanged(GitCredentialAccountDto? value)
    {
        CredentialHost = value?.Host ?? "";
        CredentialUsername = value?.Username ?? "";
        CredentialDefault = value?.IsDefault ?? true;
        CredentialToken = "";
    }

    [RelayCommand]
    private async Task ManageCredentialAccountsAsync()
    {
        if (ShowCredentialAccountsAsync is null) return;
        await RefreshCredentialAccountsAsync();
        try { await ShowCredentialAccountsAsync(); }
        finally { CredentialToken = ""; }
    }

    [RelayCommand]
    private void NewCredentialAccount()
    {
        SelectedCredentialAccount = null;
        CredentialHost = CredentialUsername = CredentialToken = "";
        CredentialDefault = true;
        CredentialAccountsMessage = "";
    }

    [RelayCommand]
    private async Task RefreshCredentialAccountsAsync()
    {
        if (CredentialAccountsBusy) return;
        CredentialAccountsBusy = true;
        try { await LoadCredentialAccountsAsync(); CredentialAccountsMessage = ""; }
        catch (Exception ex) { CredentialAccountsMessage = ex.Message; }
        finally { CredentialAccountsBusy = false; }
    }

    private async Task LoadCredentialAccountsAsync(string? selectId = null)
    {
        var selectedId = selectId ?? SelectedCredentialAccount?.Id;
        var accounts = await client.ListCredentialAccountsAsync();
        CredentialAccounts.Clear();
        foreach (var account in accounts) CredentialAccounts.Add(account);
        SelectedCredentialAccount = accounts.FirstOrDefault(x => x.Id == selectedId);
    }

    [RelayCommand]
    private async Task SaveCredentialAccountAsync()
    {
        if (CredentialAccountsBusy) return;
        CredentialAccountsBusy = true;
        var token = CredentialToken;
        CredentialToken = "";
        try
        {
            var account = await client.SaveCredentialAccountAsync(new(CredentialHost, CredentialUsername,
                token.Length == 0 ? null : token, CredentialDefault));
            await LoadCredentialAccountsAsync(account.Id);
            CredentialAccountsMessage = LocalizedText.Get("git.accounts.saved");
        }
        catch (Exception ex) { CredentialAccountsMessage = ex.Message; }
        finally { CredentialAccountsBusy = false; }
    }

    [RelayCommand]
    private async Task DefaultCredentialAccountAsync()
    {
        var account = SelectedCredentialAccount;
        if (account is null || CredentialAccountsBusy) return;
        CredentialAccountsBusy = true;
        try
        {
            await client.SaveCredentialAccountAsync(new(account.Host, account.Username, IsDefault: true));
            await LoadCredentialAccountsAsync(account.Id);
            CredentialAccountsMessage = LocalizedText.Get("git.accounts.saved");
        }
        catch (Exception ex) { CredentialAccountsMessage = ex.Message; }
        finally { CredentialAccountsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteCredentialAccountAsync()
    {
        var account = SelectedCredentialAccount;
        if (account is null || CredentialAccountsBusy) return;
        if (ShowConfirmAsync is not null && !await ShowConfirmAsync(LocalizedText.Format("git.accounts.delete_confirm", account.Username, account.Host))) return;
        CredentialAccountsBusy = true;
        try
        {
            await client.DeleteCredentialAccountAsync(account.Id);
            await LoadCredentialAccountsAsync();
            NewCredentialAccount();
            CredentialAccountsMessage = LocalizedText.Get("git.accounts.deleted");
        }
        catch (Exception ex) { CredentialAccountsMessage = ex.Message; }
        finally { CredentialAccountsBusy = false; }
    }

    [RelayCommand]
    private async Task FetchSelectedRemoteAsync()
    {
        var remote = SelectedRemote;
        if (remote is null || SelectedRepository is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await FetchWithCredentialsAsync(new(remote.Name));
            if (result.Success) StatusText = LocalizedText.Ref("git.vm.fetched");
            else await NotifyAsync(LocalizedText.Format("git.vm.failed_format", result.Message));
            await RefreshAllAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    // Resolve on the server: branch remotes, pushurl and insteadOf can point at different hosts.
    // Secrets are scoped to one request and never retained by this view model.
    private async Task<GitOperationResult> PullWithCredentialsAsync(GitPullRequest request, ManagedWindow? owner = null)
    {
        var id = SelectedRepository?.Id ?? throw new InvalidOperationException("No repository selected.");
        var result = await client.PullAsync(id, request);
        if (result.RequiresCredentials && ShowGitCredentialsDialogAsync is not null)
        {
            var credentials = await ShowGitCredentialsDialogAsync(owner, result.CredentialHost);
            if (credentials is not null) result = await client.PullAsync(id, request with { Credentials = credentials });
        }
        return result;
    }

    private async Task<GitOperationResult> FetchWithCredentialsAsync(GitFetchRequest request)
    {
        var id = SelectedRepository?.Id ?? throw new InvalidOperationException("No repository selected.");
        var result = await client.FetchAsync(id, request);
        if (result.RequiresCredentials && ShowGitCredentialsDialogAsync is not null)
        {
            var credentials = await ShowGitCredentialsDialogAsync(null, result.CredentialHost);
            if (credentials is not null) result = await client.FetchAsync(id, request with { Credentials = credentials });
        }
        return result;
    }
}
