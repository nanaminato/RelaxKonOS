using InstallationApiException = RelaxKonOS.AppSDK.InstallationApiException;
using IServerPostgreSql = RelaxKonOS.AppSDK.IServerPostgreSql;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Databases;
using RelaxKonOS.Client.Apps.Databases.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;

internal static class PostgreSqlAccountsWorkspaceChecks
{
    public static void Run(ShellSettings settings)
    {
        void Check(bool ok,string label) { if(!ok)throw new Exception(label);Console.WriteLine("PASS Database accounts "+label); }
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var task = new InstallationTaskViewModel(new InstallationClient(http,DispatchProxy.Create<IAuthSession,EntrySession>()),null!,InstallationServiceId.PostgreSqlAccounts,
            "test",()=>Task.CompletedTask,()=>Task.FromResult(false));
        var client = new Client();
        using var vm = new PostgreSqlAccountsViewModel(client,task) { CanManage=true, Snapshot=client.State };
        task.PropertyChanged += (_,_)=>vm.Notify();
        vm.NewDatabase="sample"; vm.ConfirmAsync=_=>Task.FromResult(false);
        vm.CreateDatabaseCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(handler.Bodies.Count==0 && client.Credentials.Count==0,"cancellation creates neither credentials nor native work");
        vm.NewAccount="app_reader";vm.Password="Fixture-Only-Password-30!";vm.PasswordAgain=vm.Password;
        Check(vm.CanCreateAccount,"valid inputs enable a least-privilege account");
        vm.ConfirmAsync=_=>Task.FromResult(true);var elevations=0;
        vm.AuthorizeAsync=()=>{elevations++;return Task.FromResult(true);};
        vm.CreateAccountCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(elevations==1 && client.Credentials.Count==2 && client.Credentials[0]==client.Credentials[1],"credential staging retries the exact reference after scoped elevation");
        Check(vm.Password=="" && vm.PasswordAgain=="" && vm.CanResume && !vm.CanCreateDatabase,"lost submission clears password fields and blocks new work");
        Check(!handler.Bodies[0].Contains("Fixture-Only-Password-30!"),"installation HTTP body contains only the credential reference");
        vm.Snapshot=vm.Snapshot! with { Revision=100 };
        vm.ResumeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(handler.Bodies.Count==2 && handler.Bodies[0]==handler.Bodies[1] && handler.Keys[0]==handler.Keys[1],"resume preserves exact account command and operation key");
        Check(!vm.CanResume && client.Credentials.Count==2,"accepted retry does not create another credential");
        vm.SelectedAccount=vm.Accounts.Single();
        Check(vm.ApplicationsNeedUpdate && !vm.ConnectionInfo.Contains("Password"),"partial application update is explicit and connection display omits passwords");
        vm.SelectedPermission=vm.Permissions.Single(x=>x.Value==PostgreSqlPermission.ReadOnly);
        Check(vm.CanSetGrant,"selected account can change to a distinct permission template");
        vm.CanManage=false;vm.Notify();Check(!vm.CanSetGrant && !vm.CanChangePassword,"viewer cannot mutate accounts");vm.CanManage=true;
        var output=Path.Combine(AppContext.BaseDirectory,"preview-qa","database-accounts");Directory.CreateDirectory(output);
        foreach(var language in new[]{"zh-CN","en-US","ja-JP"})
        {
            settings.Language=language;Dispatcher.UIThread.RunJobs();
            var view=new DatabaseAccounts{DataContext=vm}; var window=new Window{Width=760,Height=680,Content=view};
            window.Show();Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            Check(!view.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.StartsWith("database.")==true),"localized account page in "+language);
            Check(view.GetVisualDescendants().OfType<TextBox>().Count(t=>t.PasswordChar=='●')==2,"both password fields remain masked");
            using(var bitmap=new RenderTargetBitmap(new PixelSize(760,680))){bitmap.Render(window);bitmap.Save(Path.Combine(output,language+".png"));}
            window.Close();Dispatcher.UIThread.RunJobs();
        }
        vm.Password="Fixture-Only-Password-30!";vm.Invalidate();Check(vm.Password=="" && !vm.CanResume && !vm.CanCreateDatabase,"session invalidation clears secret drafts and disables actions");
    }
    private sealed class Client:IServerPostgreSql
    {
        public List<PostgreSqlCredentialRequest> Credentials=[];
        public PostgreSqlState State=new(true,true,true,"16.15",5,Guid.NewGuid(),55432,true,true,null,null,null,
            Databases:[new("sample",100,"rk_owner_sample",200)], Accounts:[new("sample","app_writer",300,PostgreSqlPermission.ReadWrite,Guid.NewGuid(),2,true)]);
        public Task<PostgreSqlState> MigrationPlanAsync(CancellationToken ct)=>ReadAsync(ct);
        public Task<PostgreSqlState> MaintenancePlanAsync(CancellationToken ct)=>ReadAsync(ct);
        public Task<PostgreSqlState> ReadAsync(CancellationToken ct, Guid? instance = null)=>Task.FromResult(State);
        public Task StageCredentialAsync(PostgreSqlCredentialRequest request,CancellationToken ct)
        { Credentials.Add(request);if(Credentials.Count==1)throw new InstallationApiException("elevation-required",HttpStatusCode.Forbidden);return Task.CompletedTask; }
    }
    private sealed class Handler:HttpMessageHandler
    {
        public List<string> Bodies=[]; public List<string> Keys=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.Method==HttpMethod.Post)
            {
                Bodies.Add(await request.Content!.ReadAsStringAsync(ct));Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
                if(Bodies.Count==1)throw new HttpRequestException("fixture response lost");
            }
            return new(HttpStatusCode.Accepted){Content=JsonContent.Create(new InstallationOperationDto(Guid.NewGuid(),InstallationServiceId.PostgreSqlAccounts,
                InstallationOperationKind.Configure,InstallationOperationState.Succeeded,InstallationStage.Completed,null,null,DateTimeOffset.UtcNow,null,DateTimeOffset.UtcNow,false),options:RelaxKonOSJsonOptions.Default)};
        }
    }
}
