using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.Client.Apps.Databases;

public sealed record MySqlGrantChoice(MySqlGrant Value, LocalizedStatus Label);

public sealed partial class MySqlViewModel
{
    public InstallationTaskViewModel? AccountInstallation { get; set; }
    public Func<Task<bool>>? AuthorizeAccountsAsync { get; set; }
    private InstallationTaskViewModel? submissionTask;
    [ObservableProperty] private string databaseName = "";
    [ObservableProperty] private string userName = "";
    [ObservableProperty] private string accountPassword = "";
    [ObservableProperty] private string? selectedDatabase;
    [ObservableProperty] private MySqlAccount? selectedAccount;
    [ObservableProperty] private MySqlGrantChoice selectedGrant = new(MySqlGrant.ReadOnly, LocalizedStatus.Key("database.permission.ReadOnly"));
    public IReadOnlyList<string> Databases => State?.Databases ?? [];
    public IReadOnlyList<MySqlAccount> Accounts => State?.Accounts ?? [];
    public IReadOnlyList<MySqlGrantChoice> Grants { get; } = Enum.GetValues<MySqlGrant>()
        .Select(value => new MySqlGrantChoice(value, LocalizedStatus.Key("database.permission." + value))).ToArray();
    public bool CanManageAccounts => Writable && AccountInstallation is { IsActive: false } && State is { Running: true, Verified: true };

    [RelayCommand] private Task CreateDatabaseAsync() => SubmitAccountAsync(MySqlAction.CreateDatabase);
    [RelayCommand] private Task CreateAccountAsync() => SubmitAccountAsync(MySqlAction.CreateAccount);
    [RelayCommand] private Task SetGrantAsync() => SubmitAccountAsync(MySqlAction.SetGrant);
    [RelayCommand] private Task ChangePasswordAsync() => SubmitAccountAsync(MySqlAction.ChangePassword);

    private async Task SubmitAccountAsync(MySqlAction action)
    {
        if (!CanManageAccounts || State is not { InstanceId: { } instance } snapshot) return;
        var secret = action is MySqlAction.CreateAccount or MySqlAction.ChangePassword ? AccountPassword : null;
        var reference = secret is not null ? Guid.NewGuid() : (Guid?)null;
        var account = action switch
        {
            MySqlAction.CreateDatabase => new MySqlAccountCommand(instance, DatabaseName.Trim()),
            MySqlAction.CreateAccount => new MySqlAccountCommand(instance, SelectedDatabase ?? "", UserName.Trim(), "localhost", SelectedGrant?.Value, reference),
            _ when SelectedAccount is { } target => new MySqlAccountCommand(instance, target.Database, target.User, target.Host,
                action == MySqlAction.SetGrant ? SelectedGrant?.Value : null, reference),
            _ => null
        };
        if (account?.Valid(action) != true || secret is not null && !MySqlAccountCommand.Password(secret))
        {
            Notice = LocalizedStatus.Key("database.mysql_account_invalid");
            return;
        }
        Busy = true;
        try
        {
            if (ConfirmAccountAsync is null || !await ConfirmAccountAsync(action, account) || invalid) return;
            if (reference is { } id && !await StageAccountSecretAsync(new(id, instance, secret!))) return;
            if (invalid || !ReferenceEquals(login, session.CurrentSession)) return;
            submission = new(true, action, snapshot.Revision, Account: account);
            submissionTask = AccountInstallation;
            await SendAsync();
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        { Notice = LocalizedStatus.Key("database.credential_unavailable"); }
        finally
        {
            AccountPassword = "";
            Busy = false;
        }
    }

    public Func<MySqlAction, MySqlAccountCommand, Task<bool>>? ConfirmAccountAsync { get; set; }

    private async Task<bool> StageAccountSecretAsync(PostgreSqlCredentialRequest value)
    {
        Task<HttpStatusCode> Stage() => invalid || !ReferenceEquals(login, session.CurrentSession)
            ? Task.FromResult(HttpStatusCode.Unauthorized)
            : client.StageCredentialAsync(value, lifetime.Token);
        var status = await Stage();
        if (status == HttpStatusCode.Forbidden && AuthorizeAccountsAsync is not null && await AuthorizeAccountsAsync())
            status = await Stage();
        if ((int)status is >= 200 and < 300) return true;
        Notice = LocalizedStatus.Key("database.credential_unavailable");
        return false;
    }
}
