using RelaxKonOS.AppSDK;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.App.Databases;

public sealed record PostgreSqlPermissionChoice(PostgreSqlPermission Value, DatabaseStatus Label);
public sealed partial class PostgreSqlAccountsViewModel(IServerPostgreSql client, InstallationTaskViewModel installation) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool invalid;
    private PostgreSqlAccountCommand? pending;
    private PostgreSqlCredentialRequest? pendingSecret;
    public InstallationTaskViewModel Installation { get; } = installation;
    public bool CanManage { get; set; }
    public Func<bool>? LifecycleBusy { get; set; }
    public bool HasPendingRequest => pending is not null;
    public Func<PostgreSqlAccountCommand, Task<bool>>? ConfirmAsync { get; set; }
    public Func<Task<bool>>? AuthorizeAsync { get; set; }
    public Func<Task<bool>>? ConfirmRecoveryAsync { get; set; }
    [ObservableProperty] private PostgreSqlState? snapshot;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private string newDatabase = "";
    [ObservableProperty] private string newAccount = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string passwordAgain = "";
    [ObservableProperty] private PostgreSqlDatabase? selectedDatabase;
    [ObservableProperty] private PostgreSqlAccount? selectedAccount;
    [ObservableProperty] private DatabaseStatus notice = DatabaseStatus.Literal("");
    public IReadOnlyList<PostgreSqlPermissionChoice> Permissions { get; } = Enum.GetValues<PostgreSqlPermission>()
        .Select(x => new PostgreSqlPermissionChoice(x, DatabaseStatus.Key("database.permission." + x))).ToArray();
    [ObservableProperty] private PostgreSqlPermissionChoice? selectedPermission;
    public IReadOnlyList<PostgreSqlDatabase> Databases => Snapshot?.Databases ?? [];
    public IReadOnlyList<PostgreSqlAccount> Accounts => Snapshot?.Accounts?.Where(x => x.Database == SelectedDatabase?.Name).ToArray() ?? [];
    public bool Ready => LifecycleBusy?.Invoke() != true && !invalid && !Busy && pending is null && !Installation.IsActive && CanManage
        && Snapshot is { Running: true, Managed: true, ConnectionVerified: true, PendingOperationId: null, ProblemCode: null };
    public bool CanCreateDatabase => Ready && PostgreSqlAccountValidation.Name(NewDatabase);
    private bool ValidPassword => PostgreSqlAccountValidation.Password(Password) && Password == PasswordAgain;
    public bool CanCreateAccount => Ready && SelectedDatabase is not null && PostgreSqlAccountValidation.Name(NewAccount) && ValidPassword && SelectedPermission is not null;
    public bool CanChangePassword => Ready && SelectedAccount is not null && ValidPassword;
    public bool CanSetGrant => Ready && SelectedAccount is not null && SelectedPermission is not null && SelectedPermission.Value != SelectedAccount.Permission;
    public bool CanResume => !invalid && !Busy && CanManage && pending is not null && !Installation.IsActive;
    public bool CanRecover => !invalid && !Busy && CanManage && pending is null && !Installation.IsActive && Snapshot?.PendingAccount is not null;
    public bool ApplicationsNeedUpdate => SelectedAccount?.ApplicationsNeedUpdate == true;
    public string ConnectionInfo => SelectedAccount is { } a ? $"127.0.0.1:{Snapshot?.Port}  /  {a.Database}  /  {a.Name}" : "—";
    public string OwnerRole => SelectedDatabase?.Owner ?? "—";
    partial void OnSnapshotChanged(PostgreSqlState? value)
    {
        var db = SelectedDatabase?.Name; var account = SelectedAccount?.Name;
        SelectedDatabase = Databases.FirstOrDefault(x => x.Name == db) ?? Databases.FirstOrDefault();
        SelectedAccount = Accounts.FirstOrDefault(x => x.Name == account);
        SelectedPermission ??= Permissions[0]; Notify();
    }
    partial void OnSelectedDatabaseChanged(PostgreSqlDatabase? value) { SelectedAccount = null; Notify(); }
    partial void OnSelectedAccountChanged(PostgreSqlAccount? value) { if (value is not null) SelectedPermission = Permissions.Single(x => x.Value == value.Permission); Notify(); }
    partial void OnSelectedPermissionChanged(PostgreSqlPermissionChoice? value) => Notify();
    partial void OnPasswordChanged(string value) => Notify();
    partial void OnPasswordAgainChanged(string value) => Notify();
    partial void OnNewAccountChanged(string value) => Notify();
    partial void OnNewDatabaseChanged(string value) => Notify();
    partial void OnBusyChanged(bool value) => Notify();
    public void Notify()
    {
        foreach (var name in new[] { nameof(HasPendingRequest), nameof(Databases), nameof(Accounts), nameof(Ready), nameof(CanCreateDatabase), nameof(CanCreateAccount), nameof(CanSetGrant), nameof(CanChangePassword), nameof(CanResume), nameof(CanRecover), nameof(ApplicationsNeedUpdate), nameof(ConnectionInfo), nameof(OwnerRole) }) OnPropertyChanged(name);
    }
    [RelayCommand] private Task CreateDatabaseAsync() => SubmitAsync(PostgreSqlAccountAction.CreateDatabase, CanCreateDatabase);
    [RelayCommand] private Task CreateAccountAsync() => SubmitAsync(PostgreSqlAccountAction.CreateAccount, CanCreateAccount);
    [RelayCommand] private Task SetGrantAsync() => SubmitAsync(PostgreSqlAccountAction.SetGrant, CanSetGrant);
    [RelayCommand] private Task ChangePasswordAsync() => SubmitAsync(PostgreSqlAccountAction.ChangePassword, CanChangePassword);
    private async Task SubmitAsync(PostgreSqlAccountAction action, bool allowed)
    {
        if (!allowed || Snapshot?.InstanceId is not { } instance) return;
        Busy = true;
        try
        {
            var reference = action is PostgreSqlAccountAction.CreateAccount or PostgreSqlAccountAction.ChangePassword ? Guid.NewGuid() : (Guid?)null;
            var command = new PostgreSqlAccountCommand(action, instance, Snapshot.Revision,
                action == PostgreSqlAccountAction.CreateDatabase ? NewDatabase : SelectedDatabase!.Name,
                action == PostgreSqlAccountAction.CreateDatabase ? null : action == PostgreSqlAccountAction.CreateAccount ? NewAccount : SelectedAccount!.Name,
                action is PostgreSqlAccountAction.CreateAccount or PostgreSqlAccountAction.SetGrant ? SelectedPermission!.Value : null, reference);
            if (ConfirmAsync is null || !await ConfirmAsync(command) || invalid) return;
            pending = command;
            if (reference is { } id) pendingSecret = new(id, instance, Password);
            Password = ""; PasswordAgain = "";
            await RunPendingAsync();
        }
        finally { Busy = false; }
    }
    private async Task RunPendingAsync()
    {
        try
        {
            if (pendingSecret is { } secret)
            {
                try { await client.StageCredentialAsync(secret, lifetime.Token); }
                catch (InstallationApiException ex) when (ex.ProblemCode == "elevation-required")
                {
                    if (AuthorizeAsync is null || !await AuthorizeAsync() || invalid) return;
                    await client.StageCredentialAsync(secret, lifetime.Token);
                }
                pendingSecret = null;
            }
            if (invalid || pending is null) return;
            await Installation.SubmitAsync(InstallationOperationKind.Configure, new PostgreSqlInstallationRequest(true, AccountCommand: pending));
            if (!Installation.HasPendingSubmission) pending = null;
            Notice = DatabaseStatus.Literal("");
        }
        catch (InstallationApiException ex)
        {
            Notice = DatabaseStatus.Key(ex.ProblemCode);
            if (ex.Status is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Conflict) { pending = null; pendingSecret = null; }
        }
        catch (OperationCanceledException) { }
        catch { Notice = DatabaseStatus.Key("database.account_request_pending"); }
        finally { Notify(); }
    }
    [RelayCommand] private async Task ResumeAsync() { if (!CanResume) return; Busy = true; try { await RunPendingAsync(); } finally { Busy = false; } }
    [RelayCommand] private async Task RecoverAsync()
    {
        if (!CanRecover || Snapshot?.PendingOperationId is not { } original) return;
        Busy = true;
        try
        {
            if (ConfirmRecoveryAsync is null || !await ConfirmRecoveryAsync() || invalid) return;
            await Installation.SubmitAsync(InstallationOperationKind.Repair, new PostgreSqlInstallationRequest(true, RecoverOperationId: original));
        }
        finally { Busy = false; }
    }
    public void Invalidate() { invalid = true; lifetime.Cancel(); Password = ""; PasswordAgain = ""; pendingSecret = null; pending = null; Snapshot = null; Notify(); }
    public void Dispose() { Invalidate(); lifetime.Dispose(); Installation.Dispose(); }
}
