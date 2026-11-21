using RelaxKonOS.AppSDK;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.Client.Apps.Databases;

public sealed partial class MySqlViewModel
{
    public InstallationTaskViewModel? RemoteInstallation { get; set; }
    public IServerCertificates? RemoteCertificates { get; set; }
    public Func<Task<bool>>? ConfirmRemoteAsync { get; set; }
    [ObservableProperty] private string remoteListenAddress = "";
    [ObservableProperty] private string remoteSourceAddress = "";
    [ObservableProperty] private string remoteServerName = "";
    [ObservableProperty] private CertificateDto? remoteCertificate;
    [ObservableProperty] private MySqlAccount? remoteAccount;
    [ObservableProperty] private IReadOnlyList<CertificateDto> remoteCertificateChoices = [];

    private bool RemoteReady => !invalid && !Busy && CanManage && submission is null && !Installation.IsActive
        && AccountInstallation?.IsActive != true && AdministratorInstallation?.IsActive != true && MaintenanceInstallation?.IsActive != true
        && RemoteInstallation is { IsActive: false } && State is { PendingOperationId: null, InstanceId: not null, Supported: true };
    public bool CanOpenRemote => RemoteReady && State is { RemoteAccess: null, Verified: true };
    public bool CanCloseRemote => RemoteReady && State?.RemoteAccess is not null;
    public string RemoteConnectionInfo => State?.RemoteAccess is { } policy
        ? $"{policy.ServerName}:3306 · {policy.User}@{policy.SourceAddress} · {policy.Database} · VERIFY_IDENTITY" : "—";

    [RelayCommand] private async Task LoadRemoteCertificatesAsync()
    {
        if (invalid || RemoteCertificates is null) return;
        try
        {
            var choices = await RemoteCertificates.ListAsync();
            if (!invalid)
                RemoteCertificateChoices = choices.Where(x => (x.Status is CertificateStatus.Active or CertificateStatus.Issued)
                    && x.FingerprintSha256 is not null).ToArray();
        }
        catch { Notice = LocalizedStatus.Key("database.tls_invalid"); }
    }

    [RelayCommand] private Task OpenRemoteAsync() => SubmitRemoteAsync(true);
    [RelayCommand] private Task CloseRemoteAsync() => SubmitRemoteAsync(false);

    private async Task SubmitRemoteAsync(bool open)
    {
        if (!(open ? CanOpenRemote : CanCloseRemote) || State is not { InstanceId: { } id } state) return;
        MySqlRemotePolicy? policy = null;
        if (open)
        {
            if (RemoteCertificate is not { FingerprintSha256: { } fingerprint } certificate || RemoteAccount is not { } account)
            {
                Notice = LocalizedStatus.Key("database.remote_request_invalid");
                return;
            }
            policy = new(id, RemoteListenAddress.Trim(), RemoteSourceAddress.Trim(), account.Database, account.User, account.Grant,
                RemoteServerName.Trim(), certificate.Id, fingerprint);
            if (!policy.Valid)
            {
                Notice = LocalizedStatus.Key("database.remote_request_invalid");
                return;
            }
        }
        Busy = true;
        try
        {
            if (ConfirmRemoteAsync is null || !await ConfirmRemoteAsync() || invalid || !connection.IsCurrent) return;
            submission = new(true, MySqlAction.ConfigureRemote, state.Revision, Remote: new(id, policy));
            submissionTask = RemoteInstallation;
            await SendAsync();
        }
        finally { Busy = false; }
    }
}
