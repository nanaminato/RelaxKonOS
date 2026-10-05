using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Tunnels;

if (args.Length != 2 || args[1] != "--confirm-isolated-host") return 64;
var ids = new[] { Guid.Parse("b2222222-2222-4222-8222-222222222222"), Guid.Parse("b3333333-3333-4333-8333-333333333333") };
try {
    if (args[0] == "setup-mihomo") {
        await SetupMihomo(); Console.WriteLine("PASS: independent Mihomo setup.");
    } else if (args[0] == "cleanup-mihomo") {
        await Require(new(PrivilegedOperationKind.ProxyMihomoServiceAction, ProxyMihomoServiceAction: ProxyMihomoServiceAction.Stop));
        await Require(new(PrivilegedOperationKind.ProxyMihomoRemoveSystemService));
        var release = new MihomoRuntimeManifest().Find(null)!;
        var root = OperatingSystem.IsWindows() ? @"C:\ProgramData\RelaxKonOS\Proxy" : "/var/lib/relaxkonos/proxy";
        var versions = Path.Combine(root, "engines", "mihomo", "versions");
        // Remove only the runner's known, fixed-version staging material. The runner does
        // not create Server management records, so these are not a managed installation.
        var current = Path.Combine(versions, "current");
        if (!OperatingSystem.IsWindows() && new DirectoryInfo(current).LinkTarget is { } target) {
            if (target != release.ReleaseDirectoryId) throw new Exception("Unexpected fixture runtime link.");
            Directory.Delete(current);
        }
        foreach (var file in new[] { Path.Combine(versions, "current.txt"), Path.Combine(versions, release.ReleaseDirectoryId + ".zip"),
                     Path.Combine(versions, release.ReleaseDirectoryId, "mihomo") })
            if (File.Exists(file)) { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new Exception("Unsafe fixture file."); File.Delete(file); }
        var directory = Path.Combine(versions, release.ReleaseDirectoryId);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
        if (Directory.Exists(versions)) Directory.Delete(versions, recursive: false);
        Console.WriteLine("PASS: test Mihomo service and fixed fixture staging removed.");
    } else if (args[0] == "setup") {
        var supplied = Path.Combine(AppContext.BaseDirectory,OperatingSystem.IsWindows()?"frp-windows.zip":"frp-linux.tar.gz");
        string? staged = null;
        if(File.Exists(supplied)) {
            var root=OperatingSystem.IsWindows()?@"C:\ProgramData\RelaxKonOS\server\runtimes\frp":File.ReadAllText("/etc/relaxkonos/frp-archive-root").Trim();
            Directory.CreateDirectory(root);staged=Path.Combine(root,".archive-"+Guid.NewGuid().ToString("N"));File.Copy(supplied,staged);
        }
        try { await Runtime(new(ManagedRuntime.Frpc,ManagedRuntimeAction.Install,"v0.71.0",ArchivePath:staged)); }
        finally { if(staged is not null)File.Delete(staged); }
        await Runtime(new(ManagedRuntime.Frps,ManagedRuntimeAction.Start,"v0.71.0",Server:new("127.0.0.1",17099,[new(19090,19091)],null,null,false,"isolated-acceptance-token",false,"127.0.0.1",null,null,null),AppliedIdentity:"1"));
        for(var i=0;i<ids.Length;i++) await StartClient(i);
        if(OperatingSystem.IsWindows()) {
            await Runtime(new(ManagedRuntime.Nginx,ManagedRuntimeAction.Install,"1.31.3"));
            var config=@"C:\ProgramData\RelaxKonOS\webserver\nginx\conf\nginx.conf";
            var text="worker_processes 1;\npid logs/nginx.pid;\nevents { worker_connections 64; }\nhttp { include mime.types; server { listen 127.0.0.1:18080; location / { return 200 \"acceptance\"; } } }\n";
            await Require(new(PrivilegedOperationKind.NginxWriteManagedFile,Path:config,ContentBase64:Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))));
            await Runtime(new(ManagedRuntime.Nginx,ManagedRuntimeAction.Start));
        }
        await SetupMihomo(); Console.WriteLine("PASS: independent component setup.");
    } else if(args[0]=="restore") {
        await Runtime(new(ManagedRuntime.Frps,ManagedRuntimeAction.Start,"v0.71.0",Server:new("127.0.0.1",17099,[new(19090,19091)],null,null,false,"isolated-acceptance-token",false,"127.0.0.1",null,null,null),AppliedIdentity:"1"));
        Console.WriteLine("PASS: restored baseline FRPS configuration.");
    } else if(args[0]=="status") {
        await Task.Delay(1000);
        var server=await Runtime(new(ManagedRuntime.Frps,ManagedRuntimeAction.Status));
        if(server.ComponentProcess is not {Running:true,AppliedIdentity:"1"}) throw new Exception("FRPS persistent status mismatch.");
        for(var i=0;i<ids.Length;i++) {
            var result=await Runtime(new(ManagedRuntime.Frpc,ManagedRuntimeAction.Status,ProfileId:ids[i]));
            if(result.ComponentProcess is not {Running:true,Connected:true} || result.ComponentProcess.AppliedIdentity!=new string((char)('a'+i),64)) throw new Exception("FRPC persistent status mismatch.");
        }
        Console.WriteLine("PASS: two FRPC instances and FRPS persisted status/applied proof.");
    } else if(args[0]=="stop-start") {
        await Runtime(new(ManagedRuntime.Frpc,ManagedRuntimeAction.Stop,ProfileId:ids[0]));
        var stopped=await Runtime(new(ManagedRuntime.Frpc,ManagedRuntimeAction.Status,ProfileId:ids[0]));
        var other=await Runtime(new(ManagedRuntime.Frpc,ManagedRuntimeAction.Status,ProfileId:ids[1]));
        if(stopped.ComponentProcess?.Running!=false || other.ComponentProcess?.Running!=true) throw new Exception("FRPC instance isolation failed.");
        await StartClient(0); Console.WriteLine("PASS: isolated stop/start.");
    } else if(args[0]=="rollback") {
        using var occupied=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,17100);occupied.Start();
        var rejected=false;
        try { await Runtime(new(ManagedRuntime.Frps,ManagedRuntimeAction.Start,"v0.71.0",Server:new("127.0.0.1",17100,[new(19090,19091)],null,null,false,"isolated-acceptance-token",false,"127.0.0.1",null,null,null),AppliedIdentity:"2")); }
        catch(Exception){rejected=true;}
        var restored=await Runtime(new(ManagedRuntime.Frps,ManagedRuntimeAction.Status));
        if(!rejected || restored.ComponentProcess is not {Running:true,AppliedIdentity:"1"})throw new Exception("Failed service apply did not restore prior active configuration.");
        Console.WriteLine("PASS: failed service apply restores previous configuration and proof.");
    } else if(args[0]=="cleanup") {
        await Runtime(new(ManagedRuntime.Frpc,ManagedRuntimeAction.Uninstall));
        if(OperatingSystem.IsWindows())await Runtime(new(ManagedRuntime.Nginx,ManagedRuntimeAction.Uninstall));
        await Require(new(PrivilegedOperationKind.ProxyMihomoServiceAction, ProxyMihomoServiceAction: ProxyMihomoServiceAction.Stop));
        await Require(new(PrivilegedOperationKind.ProxyMihomoRemoveSystemService)); Console.WriteLine("PASS: independent service removal.");
    } else return 64;
    return 0;
} catch(Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }

Task<PrivilegedOperationResult> Runtime(ManagedRuntimeRequest request)=>Require(new(PrivilegedOperationKind.ManagedRuntime,ManagedRuntime:request));
Task<PrivilegedOperationResult> StartClient(int i)=>Runtime(new(ManagedRuntime.Frpc,ManagedRuntimeAction.Start,"v0.71.0",ids[i],
    Client:new("127.0.0.1",17099,TunnelTlsMode.Default,"isolated-acceptance-token",[new("acceptance"+i,TunnelProtocol.Tcp,"127.0.0.1",19999,19090+i,null,false,false)]),AppliedIdentity:new string((char)('a'+i),64)));

async Task SetupMihomo() {
    var release=new MihomoRuntimeManifest().Find(null)!;
    var root=OperatingSystem.IsWindows()?@"C:\ProgramData\RelaxKonOS\Proxy":"/var/lib/relaxkonos/proxy";
    var versions=Path.Combine(root,"engines","mihomo","versions");Directory.CreateDirectory(versions);
    var directory=Path.Combine(versions,release.ReleaseDirectoryId);Directory.CreateDirectory(directory);
    using var client=new HttpClient {Timeout=TimeSpan.FromMinutes(3)};
    var supplied=Path.Combine(AppContext.BaseDirectory,OperatingSystem.IsWindows()?"mihomo-windows.zip":"mihomo-linux.gz");
    var bytes=File.Exists(supplied)?await File.ReadAllBytesAsync(supplied):await client.GetByteArrayAsync(release.DownloadUri);
    if(!Convert.ToHexString(SHA256.HashData(bytes)).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase))throw new Exception("Mihomo archive integrity failed.");
    if(OperatingSystem.IsWindows()) {
        await File.WriteAllBytesAsync(Path.Combine(versions,release.ReleaseDirectoryId+".zip"),bytes);
        await File.WriteAllTextAsync(Path.Combine(versions,"current.txt"),release.ReleaseDirectoryId);
    } else {
        using var gzip=new GZipStream(new MemoryStream(bytes),CompressionMode.Decompress);
        var binary=Path.Combine(directory,"mihomo");await using(var output=File.Create(binary))await gzip.CopyToAsync(output);
        File.SetUnixFileMode(binary,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute);
        Directory.CreateSymbolicLink(Path.Combine(versions,"current"),release.ReleaseDirectoryId);
    }
    Directory.CreateDirectory(Path.Combine(root,"engines","mihomo","data"));
    var config=OperatingSystem.IsWindows()?Path.Combine(root,"config"):"/etc/relaxkonos/proxy";Directory.CreateDirectory(config);
    await File.WriteAllTextAsync(Path.Combine(config,"active.yaml"),"mixed-port: 17890\nexternal-controller: 127.0.0.1:17991\nsecret: isolated-controller-token\nmode: direct\nlog-level: error\ntun:\n  enable: false\n");
    await Require(new(PrivilegedOperationKind.ProxyMihomoInstallSystemService));
    if(OperatingSystem.IsWindows())await Require(new(PrivilegedOperationKind.ProxyMihomoServiceAction,ProxyMihomoServiceAction:ProxyMihomoServiceAction.Start));
    else { using var p=Process.Start(new ProcessStartInfo("/usr/bin/systemctl"){ArgumentList={"enable","--now","relaxkonos-mihomo.service"}})!;await p.WaitForExitAsync();if(p.ExitCode!=0)throw new Exception("Mihomo service failed."); }
}
async Task<PrivilegedOperationResult> Require(PrivilegedOperationRequest request) {
    var operation=Guid.NewGuid();request=request with {OperationId=operation,Correlation=CorrelationContext.Create(operation)};
    PrivilegedOperationResult? result=null;
    if(OperatingSystem.IsWindows()) {
        using var config=JsonDocument.Parse(await File.ReadAllTextAsync(@"C:\ProgramData\RelaxKonOS\privileged-helper\helper.json"));
        var secret=Convert.FromBase64String(config.RootElement.GetProperty("sharedSecret").GetString()!);
        await using var pipe=new NamedPipeClientStream(".",config.RootElement.GetProperty("pipeName").GetString()!,PipeDirection.InOut,PipeOptions.Asynchronous);await pipe.ConnectAsync(30000);
        var payload=JsonSerializer.SerializeToUtf8Bytes(request);
        var envelope=JsonSerializer.SerializeToUtf8Bytes(new Envelope(Convert.ToBase64String(payload),Convert.ToBase64String(HMACSHA256.HashData(secret,payload))));
        await pipe.WriteAsync(BitConverter.GetBytes(envelope.Length));await pipe.WriteAsync(envelope);await pipe.FlushAsync();
        while(result is null) {
            var header=new byte[4];await pipe.ReadExactlyAsync(header);var length=BitConverter.ToInt32(header);
            if(length<=0||length>PrivilegedOperationProtocol.MaximumRequestBytes)throw new Exception("Invalid pipe frame.");
            var buffer=new byte[length];await pipe.ReadExactlyAsync(buffer);var frame=JsonSerializer.Deserialize<Envelope>(buffer)!;
            payload=Convert.FromBase64String(frame.PayloadBase64);
            if(!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(frame.SignatureBase64),HMACSHA256.HashData(secret,payload)))throw new Exception("Invalid Helper signature.");
            result=JsonSerializer.Deserialize<PrivilegedOperationFrame>(payload)?.Result;
        }
    } else {
        var path=File.ReadLines("/etc/relaxkonos/server.env").Single(line=>line.StartsWith("PrivilegedHelper__HelperPath=")).Split('=',2)[1];
        using var process=Process.Start(new ProcessStartInfo(path){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));process.StandardInput.Close();var error=process.StandardError.ReadToEndAsync();
        for(string? line;(line=await process.StandardOutput.ReadLineAsync())is not null;)if(!string.IsNullOrWhiteSpace(line))result=JsonSerializer.Deserialize<PrivilegedOperationFrame>(line)?.Result??result;
        await process.WaitForExitAsync();await error;
    }
    if(result is not {Success:true})throw new Exception($"{request.Operation}/{request.ManagedRuntime?.Runtime}/{request.ManagedRuntime?.Action}: {result?.ProblemCode}: {result?.Error}");
    return result;
}
record Envelope(string PayloadBase64,string SignatureBase64);
