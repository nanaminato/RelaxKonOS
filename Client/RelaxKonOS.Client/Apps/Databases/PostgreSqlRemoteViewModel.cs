using RelaxKonOS.AppSDK;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Apps.Certificates;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.Installations;
namespace RelaxKonOS.Client.Apps.Databases;
public sealed partial class PostgreSqlRemoteViewModel(IServerCertificates certificates, InstallationTaskViewModel installation) : ObservableObject, IDisposable
{
    public InstallationTaskViewModel Installation => installation;
    public Func<Task<bool>>? ConfirmAsync { get; set; }
    public Func<bool>? OtherBusy { get; set; }
    public bool CanManage { get; set; }
    private bool invalid;
    private PostgreSqlInstallationRequest? pending;
    [ObservableProperty] private PostgreSqlState? snapshot;
    [ObservableProperty] private string listenAddress="";
    [ObservableProperty] private string serverName="";
    [ObservableProperty] private string sources="";
    [ObservableProperty] private PostgreSqlAccount? selectedAccount;
    [ObservableProperty] private CertificateDto? selectedCertificate;
    [ObservableProperty] private IReadOnlyList<CertificateDto> choices=[];
    [ObservableProperty] private bool busy;
    [ObservableProperty] private LocalizedStatus notice=LocalizedStatus.Literal("");
    public IEnumerable<PostgreSqlAccount> Accounts => Snapshot?.Accounts?.Where(x=>x.Permission!=PostgreSqlPermission.Migration)??[];
    public bool HasPending => pending is not null;
    private bool Ready => !invalid && !Busy && CanManage && !installation.IsActive && OtherBusy?.Invoke()!=true && pending is null && Snapshot is { Supported:true, Installed:true, InstanceId:not null };
    public bool CanEnable => (Snapshot?.Instances?.FirstOrDefault()?.Id == Snapshot?.InstanceId || Snapshot?.Instances is null) && Ready && Snapshot is { RemoteAccess:null, PendingOperationId:null, ConnectionVerified:true };
    public bool CanClose => Ready && Snapshot is { RemoteAccess:not null, PendingOperationId:null };
    public bool CanRecover => Ready && Snapshot?.PendingRemoteAccess is not null;
    public bool CanResume => !invalid && !Busy && CanManage && !installation.IsActive && pending is not null;
    public string ConnectionInfo => Snapshot?.RemoteAccess is {} r ? $"host={r.ServerName} port={Snapshot.Port} dbname={r.Database} user={r.Account} sslmode=verify-full" : "";
    partial void OnSnapshotChanged(PostgreSqlState? value) { OnPropertyChanged(nameof(Accounts));Notify(); }
    partial void OnBusyChanged(bool value)=>Notify();
    public void Notify(){foreach(var n in new[]{nameof(CanEnable),nameof(CanClose),nameof(CanRecover),nameof(CanResume),nameof(HasPending),nameof(ConnectionInfo)})OnPropertyChanged(n);}
    [RelayCommand] public async Task RefreshCertificatesAsync()
    {
        try { Choices=(await certificates.ListAsync()).Where(x=>x.Status is CertificateStatus.Active or CertificateStatus.Issued && x.FingerprintSha256 is not null).ToArray(); }
        catch { Notice=LocalizedStatus.Key("database.tls_invalid"); }
    }
    [RelayCommand] private Task EnableAsync()=>SubmitAsync(true);
    [RelayCommand] private Task CloseAsync()=>SubmitAsync(false);
    private async Task SubmitAsync(bool enabled)
    {
        if(!(enabled?CanEnable:CanClose))return;
        var command=enabled ? new PostgreSqlRemoteCommand(Snapshot!.InstanceId!.Value,Snapshot.Revision,true,ListenAddress.Trim(),ServerName.Trim(),SelectedAccount?.Database,SelectedAccount?.Name,
            Sources.Split([',',';','\n'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries),SelectedCertificate?.Id,SelectedCertificate?.FingerprintSha256)
            :new PostgreSqlRemoteCommand(Snapshot!.InstanceId!.Value,Snapshot.Revision,false);
        if(!PostgreSqlRemoteValidation.Valid(command)){Notice=LocalizedStatus.Key("database.remote_request_invalid");return;}
        Busy=true;
        try { if(ConfirmAsync is null || !await ConfirmAsync() || invalid)return;pending=new(true,RemoteAccess:command);await Send(); }
        finally { Busy=false; }
    }
    [RelayCommand] private async Task RecoverAsync()
    {
        if(!CanRecover)return;Busy=true;
        try { if(ConfirmAsync is null || !await ConfirmAsync() || invalid)return;pending=new(true,RecoverOperationId:Snapshot!.PendingOperationId);await Send(); }
        finally { Busy=false; }
    }
    [RelayCommand] private async Task ResumeAsync(){if(!CanResume)return;Busy=true;try{await Send();}finally{Busy=false;}}
    private async Task Send()
    {
        var previous=installation.Operation?.OperationId;
        await installation.SubmitAsync(pending!.RecoverOperationId is null?InstallationOperationKind.Configure:InstallationOperationKind.Repair,pending);
        if(!installation.HasPendingSubmission || installation.Operation?.OperationId is {} accepted && accepted!=previous)pending=null;
        Notify();
    }
    public void Invalidate(){invalid=true;Snapshot=null;pending=null;Notify();}
    public void Dispose(){Invalidate();installation.Dispose();}
}
