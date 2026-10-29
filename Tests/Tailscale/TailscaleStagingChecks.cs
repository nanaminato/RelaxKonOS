using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class TailscaleStagingChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Tailscale staging assertion "+count); count++; }
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Path.Combine(temporary,"tailscale-stage-"+Guid.NewGuid().ToString("N")));
        byte[] bytes = [1,2,3,4]; var calls = 0;
        var release = new ComponentDownloadRelease("1.104.4","win-x64",TailscaleOfficialReleases.Root+"tailscale-setup-1.104.4-amd64.msi",
            "tailscale-setup-1.104.4-amd64.msi","msi",Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var properties = new TailscaleMsiProperties.Evidence("Tailscale","Tailscale Inc.",release.Version,
            Guid.NewGuid().ToString("B").ToUpperInvariant(),TailscaleMsiProperties.KnownUpgradeCode,Guid.NewGuid().ToString("B").ToUpperInvariant(),"x64;1033");
        var evidence = new TailscaleMsiSignature.Evidence(release.Sha256!,"Tailscale Inc. / CA / 1131559-5",new string('A',64),properties);
        Task<ComponentDownloadRelease> Resolve(string rid,string version,CancellationToken ct,RuntimeDownloadProxy? proxy)
        { ct.ThrowIfCancellationRequested(); calls++; return Task.FromResult(release); }
        var transfer = new TailscaleMsiArtifactTransfer(async (_,output,_,ct) => await output.WriteAsync(bytes,ct));
        Task<TailscaleMsiSignature.Evidence> Inspect(string path,ComponentDownloadRelease selected,CancellationToken ct)
        {
            Check(File.ReadAllBytes(path).SequenceEqual(bytes) && selected == release);
            if (OperatingSystem.IsWindows())
            {
                try { using var writer = new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite); throw new Exception("Writable lease"); }
                catch (IOException) { Check(true); }
            }
            return Task.FromResult(evidence);
        }
        TailscaleMsiStaging Stage(Func<string,ComponentDownloadRelease,CancellationToken,Task<TailscaleMsiSignature.Evidence>>? inspector = null,
            Action<string>? require = null) => new(root,path => Directory.CreateDirectory(path),require ?? (_ => { }),Resolve,transfer.CopyAsync,inspector ?? Inspect);
        try
        {
            var stage = Stage(); var id = Guid.NewGuid();
            var lease = await stage.PrepareAsync(id,release.Rid,release.Version,null,default);
            var path = lease.Path;
            Check(lease.Release == release && lease.Evidence == evidence && File.Exists(path));
            using var wait = new CancellationTokenSource();
            var blocked = Stage().PrepareAsync(Guid.NewGuid(),release.Rid,release.Version,null,wait.Token);
            await Task.Delay(80); Check(!blocked.IsCompleted && calls == 1);
            wait.Cancel();
            try { await blocked; throw new Exception("Lock cancellation ignored"); }
            catch (OperationCanceledException) { Check(calls == 1); }
            lease.Dispose(); lease.Dispose(); Check(!File.Exists(path) && !Directory.Exists(Path.GetDirectoryName(path)));
            try { _ = lease.Path; throw new Exception("Disposed lease reused"); }
            catch (ObjectDisposedException) { Check(true); }
            using (var again = await stage.PrepareAsync(id,release.Rid,release.Version,null,default)) Check(File.Exists(again.Path));
            var orphan = Guid.NewGuid(); var orphanPath = Path.Combine(root,orphan.ToString("N")); Directory.CreateDirectory(orphanPath);
            File.WriteAllText(Path.Combine(orphanPath,"original"),"preserve"); var before = calls;
            try { await stage.PrepareAsync(orphan,release.Rid,release.Version,null,default); throw new Exception("Orphan overwritten"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "component.tailscale_staging_exists" && calls == before && File.ReadAllText(Path.Combine(orphanPath,"original")) == "preserve"); }
            var failure = Guid.NewGuid();
            try { await Stage((_,_,_) => throw new IOException("verification fixture")).PrepareAsync(failure,release.Rid,release.Version,null,default); throw new Exception("Verification failure ignored"); }
            catch (IOException) { Check(!Directory.Exists(Path.Combine(root,failure.ToString("N")))); }
            using (var next = await stage.PrepareAsync(Guid.NewGuid(),release.Rid,release.Version,null,default)) Check(File.Exists(next.Path));
            before = calls;
            try { await Stage(require:_ => throw new UnauthorizedAccessException()).PrepareAsync(Guid.NewGuid(),release.Rid,release.Version,null,default); throw new Exception("Protection ignored"); }
            catch (UnauthorizedAccessException) { Check(calls == before); }
            try { await stage.PrepareAsync(Guid.NewGuid(),release.Rid,"../1.104.4",null,default); throw new Exception("Path selector accepted"); }
            catch (RuntimePolicyException) { Check(calls == before); }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await stage.PrepareAsync(Guid.NewGuid(),release.Rid,release.Version,null,cancelled.Token); throw new Exception("Cancelled stage accepted"); }
            catch (OperationCanceledException) { Check(calls == before); }
            Check(Directory.GetDirectories(root).Where(p => Path.GetFileName(p) != "pins").SequenceEqual(new[] { orphanPath }));
            using var late = new CancellationTokenSource(); var lateId = Guid.NewGuid();
            try { await Stage((_,_,_) => { late.Cancel(); return Task.FromResult(evidence); }).PrepareAsync(lateId,release.Rid,release.Version,null,late.Token); throw new Exception("Late inspection accepted"); }
            catch (OperationCanceledException) { Check(!Directory.Exists(Path.Combine(root,lateId.ToString("N")))); }
            var foreignId = Guid.NewGuid();
            var foreign = await Stage((file,_,_) => { File.WriteAllText(Path.Combine(Path.GetDirectoryName(file)!,"foreign"),"preserve"); return Task.FromResult(evidence); })
                .PrepareAsync(foreignId,release.Rid,release.Version,null,default);
            try { foreign.Dispose(); throw new Exception("Unknown content deleted"); }
            catch (IOException) { Check(File.ReadAllText(Path.Combine(root,foreignId.ToString("N"),"foreign")) == "preserve"); }
            using (var afterCleanupFailure = await stage.PrepareAsync(Guid.NewGuid(),release.Rid,release.Version,null,default)) Check(File.Exists(afterCleanupFailure.Path));
            var lockPath = Path.Combine(root,"staging.lock"); File.Delete(lockPath); Directory.CreateDirectory(lockPath);
            try { await stage.PrepareAsync(Guid.NewGuid(),release.Rid,release.Version,null,default); throw new Exception("Corrupt lock path accepted"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "component.tailscale_staging_invalid"); }
            Directory.Delete(lockPath,false);
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(root); var acl = System.IO.FileSystemAclExtensions.GetAccessControl(info);
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid,null),
                    System.Security.AccessControl.FileSystemRights.Write,System.Security.AccessControl.AccessControlType.Allow));
                System.IO.FileSystemAclExtensions.SetAccessControl(info,acl);
                before = calls;
                try { await Stage(require:TailscaleControlProtection.Require).PrepareAsync(Guid.NewGuid(),release.Rid,release.Version,null,default); throw new Exception("World-writable root accepted"); }
                catch (UnauthorizedAccessException) { Check(calls == before); }
            }
        }
        finally
        {
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(temporary) || !Path.GetFileName(root).StartsWith("tailscale-stage-",StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected staging test cleanup target");
            if (Directory.Exists(root)) Directory.Delete(root,true);
        }
        Console.WriteLine($"Tailscale MSI staging checks passed: {count}");
    }
}
