using System.Text;
using System.Text.Json;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Server.Tailscale;

internal static class TailscaleLoginChecks
{
    internal static async Task RunAsync()
    {
        var count=0;
        void Check(bool value) { if(!value) throw new Exception("Tailscale login adapter assertion " + count); count++; }
        void Reject(string json,int exit=1)
        {
            try { using var ignored=TailscaleLoginOutput.Parse(Encoding.UTF8.GetBytes(json),exit); throw new Exception("Login output accepted"); }
            catch(InvalidDataException ex) { Check(ex.Message=="tailscale.login.invalid_output" && ex.InnerException is null); }
        }
        const string url="https://login.tailscale.com/a/0123456789abcdef";
        var frame=JsonSerializer.Serialize(new { AuthURL=url,QR="private-marker",BackendState="NeedsLogin" });
        Check(TailscaleLoginOutput.IsOfficialUrl(url));
        foreach(var bad in new[]{"http://login.tailscale.com/a/0123456789abcdef",url+"\n",url+"?redirect=bad",url+"#bad",
            url.Replace(".com/",".com:443/"),url.Replace(".com/",".com.evil/"),url.Replace("login.","user@login."),
            url.Replace("/a/","/a/%2F"),url.Replace("/a/","/admin/"),url.Replace("https:","HTTPS:"),url+"/",
            "https://login.tailscale.com/a/short","https://login.tailscale.com/a/"+new string('a',257)}) Check(!TailscaleLoginOutput.IsOfficialUrl(bad));
        using(var pending=TailscaleLoginOutput.Parse(Encoding.UTF8.GetBytes(frame),1))
        {
            Check(pending.State=="awaiting-browser" && !JsonSerializer.Serialize(pending).Contains("0123456789abcdef") && !pending.ToString().Contains("0123456789abcdef"));
            Check(pending.TakeAuthenticationUrl()==url && pending.TakeAuthenticationUrl() is null);
        }
        using(var done=TailscaleLoginOutput.Parse(Encoding.UTF8.GetBytes(frame+"\n{\"BackendState\":\"Running\"}"),0))
            Check(done.State=="completed" && done.TakeAuthenticationUrl() is null);
        using(var approval=TailscaleLoginOutput.Parse(Encoding.UTF8.GetBytes("{\"BackendState\":\"NeedsMachineAuth\"}"),1))
            Check(approval.State=="approval-required" && approval.TakeAuthenticationUrl() is null);
        using(var disposed=TailscaleLoginOutput.Parse(Encoding.UTF8.GetBytes(frame),1)) { disposed.Dispose(); Check(disposed.TakeAuthenticationUrl() is null); }
        foreach(var bad in new[]{"", "[]", "null", "{", "{\"BackendState\":false}", "{\"BackendState\":\"Stopped\"}",
            frame.Replace("NeedsLogin","Running"), frame.Replace("https://login.tailscale.com/a/","https://evil.invalid/a/"),
            "{\"BackendState\":\"NeedsLogin\",\"BackendState\":\"Running\"}", "{\"BackendState\":\"NeedsLogin\",\"AuthURL\":12}",
            "{\"BackendState\":\"NeedsLogin\",\"Error\":\"private-marker\"}","{\"BackendState\":\"NeedsLogin\",\"PrivateKey\":\"private-marker\"}",
            frame.Replace("\"private-marker\"","{}"),frame+"invalid",frame+"\n"+frame.Replace("0123456789abcdef","abcdef0123456789"),
            string.Join("\n",Enumerable.Repeat(frame,5)),"{\"BackendState\":\"Running\"}\n"+frame}) Reject(bad);
        Reject("{\"BackendState\":\"Running\"}",1);
        Reject(new string(' ',1024*1024+1));
        var captured=new TailscaleCapturedResult("exited",1,frame);
        Check(!JsonSerializer.Serialize(captured).Contains("private-marker") && !captured.ToString().Contains("0123456789abcdef"));
        var raw=new TailscaleCommandResult("observed",frame);
        Check(!JsonSerializer.Serialize(raw).Contains("private-marker") && !raw.ToString().Contains("0123456789abcdef"));
        var commands=new Commands(frame,url);
        var native=new TailscaleNativeLogin(commands.Run,()=>commands.TrustChecks++);
        var baseline=await native.ReadAsync(default);
        Check(baseline.CanStart && baseline.BackendState=="NeedsLogin");
        using(var pending=await native.BeginAsync(baseline.Revision,default))
        {
            Check(pending.State=="awaiting-browser" && pending.Observation?.PreferencesRevision==baseline.PreferencesRevision);
            Check(pending.TakeAuthenticationUrl()==url && pending.TakeAuthenticationUrl() is null);
            Check(commands.UpCalls==1 && commands.TrustChecks>=4);
        }
        Check(!JsonSerializer.Serialize(await native.ReadAsync(default)).Contains("0123456789abcdef"));
        var fingerprint=TailscaleNativeLogin.FingerprintChallenge(url);
        Check(fingerprint.Length==64 && !fingerprint.Contains("0123456789abcdef"));
        using(var challenge=await native.ReadChallengeAsync(fingerprint,baseline.PreferencesRevision,default))
            Check(challenge.TakeAuthenticationUrl()==url && challenge.TakeAuthenticationUrl() is null && commands.UpCalls==1);
        foreach(var digest in new[]{"",new string('Z',64),new string('F',64)})
        {
            try { using var ignored=await native.ReadChallengeAsync(digest,baseline.PreferencesRevision,default);throw new Exception("Foreign challenge fingerprint accepted"); }
            catch(InvalidDataException ex) { Check(ex.Message=="tailscale.login.invalid_output" && !ex.ToString().Contains("0123456789abcdef")); }
        }
        try { using var ignored=await native.ReadChallengeAsync(fingerprint,new string('F',64),default);throw new Exception("Foreign preference revision accepted"); }
        catch(InvalidDataException) { Check(commands.UpCalls==1); }
        commands.WrongChallenge=true;
        try { using var ignored=await native.ReadChallengeAsync(fingerprint,baseline.PreferencesRevision,default);throw new Exception("Rotated challenge accepted"); }
        catch(InvalidDataException) { Check(commands.UpCalls==1); }
        commands.WrongChallenge=false;
        var reads=0;
        var changing=new TailscaleNativeLogin((args,ct)=>
        {
            if(args[0]=="status" && ++reads==4) commands.WrongChallenge=true;
            return commands.Run(args,ct);
        },()=>{});
        try { using var ignored=await changing.ReadChallengeAsync(fingerprint,baseline.PreferencesRevision,default);throw new Exception("Mid-read challenge change accepted"); }
        catch(InvalidDataException) { Check(commands.UpCalls==1); }
        commands.WrongChallenge=false;
        using(var cancelledRead=new CancellationTokenSource())
        {
            cancelledRead.Cancel();
            try { using var ignored=await native.ReadChallengeAsync(fingerprint,baseline.PreferencesRevision,cancelledRead.Token);throw new Exception("Cancelled challenge read accepted"); }
            catch(OperationCanceledException) { Check(commands.UpCalls==1); }
        }
        commands.Complete=true;
        try { using var ignored=await native.ReadChallengeAsync(fingerprint,baseline.PreferencesRevision,default);throw new Exception("Completed challenge exposed"); }
        catch(InvalidDataException) { Check(commands.UpCalls==1); }
        commands.Complete=false;
        var before=commands.UpCalls;
        try { await native.BeginAsync(baseline.Revision,default);throw new Exception("Existing challenge was replaced"); }
        catch(InvalidDataException ex) { Check(ex.Message=="tailscale.login.state_changed" && commands.UpCalls==before); }
        commands.Started=false;
        try { await native.BeginAsync(new('F',64),default);throw new Exception("Stale revision accepted"); }
        catch(InvalidDataException) { Check(commands.UpCalls==before); }
        commands.ChangePreferences=true;
        try { using var ignored=await native.BeginAsync(baseline.Revision,default);throw new Exception("Preference changes hidden"); }
        catch(InvalidDataException ex) { Check(ex.Message=="tailscale.login.invalid_output"); }
        commands=new Commands(frame,url) { WrongChallenge=true }; native=new(commands.Run,()=>{});baseline=await native.ReadAsync(default);
        try { using var ignored=await native.BeginAsync(baseline.Revision,default);throw new Exception("Foreign challenge accepted"); }
        catch(InvalidDataException ex) { Check(ex.Message=="tailscale.login.invalid_output"); }
        commands=new Commands(frame+"\n{\"BackendState\":\"Running\"}",url) { Complete=true };native=new(commands.Run,()=>{});baseline=await native.ReadAsync(default);
        using(var done=await native.BeginAsync(baseline.Revision,default)) Check(done.State=="completed" && done.Observation?.BackendState=="Running" && done.TakeAuthenticationUrl() is null);
        commands=new Commands(frame,url) { CaptureTimedOut=true }; native=new(commands.Run,()=>{});baseline=await native.ReadAsync(default);
        try { using var ignored=await native.BeginAsync(baseline.Revision,default);throw new Exception("Process timeout accepted as a challenge"); }
        catch(InvalidDataException ex) { Check(ex.Message=="tailscale.login.invalid_output" && commands.UpCalls==1); }
        using(var cancellation=new CancellationTokenSource())
        {
            commands=new Commands(frame,url) { CancelAfterUp=cancellation }; native=new(commands.Run,()=>{});baseline=await native.ReadAsync(default);
            try { using var ignored=await native.BeginAsync(baseline.Revision,cancellation.Token);throw new Exception("In-flight cancellation ignored"); }
            catch(OperationCanceledException ex) { Check(commands.UpCalls==1 && !ex.ToString().Contains("0123456789abcdef")); }
        }
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel(); var calls=commands.UpCalls;
            try { await native.BeginAsync(baseline.Revision,cancelled.Token);throw new Exception("Cancellation ignored"); }
            catch(OperationCanceledException) { Check(commands.UpCalls==calls); }
        }
        Console.WriteLine($"{count} Tailscale first-login native/privacy checks passed.");
    }
    internal sealed class Commands(string output,string url)
    {
        internal bool Started,ChangePreferences,WrongChallenge,Complete,CaptureTimedOut;
        internal CancellationTokenSource? CancelAfterUp;
        internal int UpCalls,TrustChecks;
        internal Task<TailscaleCapturedResult> Run(IReadOnlyList<string> arguments,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(arguments[0]=="up")
            {
                if(!arguments.SequenceEqual(new[]{"up","--json","--timeout=5s"})) throw new Exception("Unbounded or unsafe login arguments");
                UpCalls++;Started=true;CancelAfterUp?.Cancel();return Task.FromResult(CaptureTimedOut ? new TailscaleCapturedResult("timeout",Output:output) : new TailscaleCapturedResult("exited",Complete?0:1,output));
            }
            if(!arguments.SequenceEqual(new[]{arguments[0],"--json"}) || arguments[0] is not ("status" or "get")) throw new Exception("Unexpected login observation command");
            var text=arguments[0]=="get" ? JsonSerializer.Serialize(new Dictionary<string,object> { ["accept-dns"]=!(Started&&ChangePreferences),["accept-routes"]=false })
                : JsonSerializer.Serialize(new { Version="1.test",BackendState=Started&&Complete?"Running":"NeedsLogin",Self=new { ID=Started&&Complete?"node":"" },
                    AuthURL=Started&&!Complete?(WrongChallenge?url.Replace("0123456789abcdef","abcdef0123456789"):url):"" });
            return Task.FromResult(new TailscaleCapturedResult("exited",0,text));
        }
    }
}
