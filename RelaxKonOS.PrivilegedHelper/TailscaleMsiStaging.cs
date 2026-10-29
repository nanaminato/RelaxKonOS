using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

// Preparation only. A lease does not authorize installation, transfer ownership or survive a restart.
internal sealed class TailscaleMsiStaging(
    string root, Action<string> prepareRoot, Action<string> require,
    Func<string,string,CancellationToken,RuntimeDownloadProxy?,Task<ComponentDownloadRelease>> resolve,
    Func<ComponentDownloadRelease,Stream,RuntimeDownloadProxy?,CancellationToken,Task<long>> copy,
    Func<string,ComponentDownloadRelease,CancellationToken,Task<TailscaleMsiSignature.Evidence>> inspect)
{
    internal static TailscaleMsiStaging Create()
    {
        if (!OperatingSystem.IsWindows()) throw new RuntimePolicyException(ComponentProblemCodes.PlatformUnsupported);
        var source = new TailscaleOfficialReleases(); var transfer = new TailscaleMsiArtifactTransfer();
        return new(Path.Combine(TailscaleControlProtection.Root,"msi-staging"),
            path => { TailscaleControlProtection.PrepareRoot(TailscaleControlProtection.Root); TailscaleControlProtection.PrepareRoot(path); },
            TailscaleControlProtection.Require,(rid,version,ct,proxy) => source.ResolveAsync(rid,version,ct,proxy),
            transfer.CopyAsync,TailscaleMsiSignature.InspectAsync);
    }

    internal async Task<Lease> PrepareAsync(Guid operationId,string rid,string version,RuntimeDownloadProxy? proxy,CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException(ComponentProblemCodes.RequestInvalid);
        var architecture = rid switch { "win-x64" => "amd64", "win-arm64" => "arm64", _ => throw new RuntimePolicyException(ComponentProblemCodes.PlatformUnsupported) };
        var name = $"tailscale-setup-{version}-{architecture}.msi";
        // Validate selectors before creating anything; the actual digest is always independently resolved below.
        TailscaleMsiArtifactTransfer.Validate(new(version,rid,TailscaleOfficialReleases.Root+name,name,"msi",new string('0',64)));
        if (proxy is not null) RuntimeDownloadPolicy.Validate(proxy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5)); var token = deadline.Token;
        token.ThrowIfCancellationRequested(); prepareRoot(root); require(root);
        var lockPath = Path.Combine(root,"staging.lock");
        FileStream? gate = null; FileStream? held = null; var created = false;
        var directory = Path.Combine(root,operationId.ToString("N"));
        var path = Path.Combine(directory,"package.msi");
        try
        {
            while (gate is null)
            {
                token.ThrowIfCancellationRequested();
                require(root);
                if (Directory.Exists(lockPath)) throw new RuntimePolicyException("component.tailscale_staging_invalid");
                if (File.Exists(lockPath)) require(lockPath);
                try { gate = new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
                catch (IOException) { await Task.Delay(50,token); }
            }
            require(root); require(lockPath);
            if (Directory.Exists(directory) || File.Exists(directory)) throw new RuntimePolicyException("component.tailscale_staging_exists");
            Directory.CreateDirectory(directory); created = true; require(directory);
            var release = await resolve(rid,version,token,proxy);
            token.ThrowIfCancellationRequested(); TailscaleMsiArtifactTransfer.Validate(release);
            if (release.Version != version || release.Rid != rid) throw new RuntimePolicyException("component.tailscale_artifact_invalid");
            await using (var output = new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read,65536,FileOptions.Asynchronous))
            {
                require(path);
                await copy(release,output,proxy,token);
                token.ThrowIfCancellationRequested();
                await output.FlushAsync(token); output.Flush(flushToDisk:true);
            }
            // The private directory protects the write-to-read handoff; retain this read handle for the entire lease.
            require(directory); require(path);
            held = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            var evidence = await inspect(path,release,token);
            token.ThrowIfCancellationRequested(); require(directory); require(path);
            var lease = new Lease(path,release,evidence,held,gate,() => Cleanup(directory,path));
            held = null; gate = null; created = false;
            return lease;
        }
        catch
        {
            try { held?.Dispose(); if (created) Cleanup(directory,path); }
            finally { gate?.Dispose(); }
            throw;
        }
    }
    private void Cleanup(string directory,string path)
    {
        require(root); require(directory);
        if (File.Exists(path)) { require(path); File.Delete(path); }
        // Never recursively delete unexpected contents or follow a substituted directory.
        Directory.Delete(directory,recursive:false);
    }
    internal sealed class Lease(string path,ComponentDownloadRelease release,TailscaleMsiSignature.Evidence evidence,
        FileStream held,FileStream gate,Action cleanup) : IDisposable
    {
        private int disposed;
        internal string Path => Volatile.Read(ref disposed) == 0 ? path : throw new ObjectDisposedException(nameof(Lease));
        internal ComponentDownloadRelease Release => Volatile.Read(ref disposed) == 0 ? release : throw new ObjectDisposedException(nameof(Lease));
        internal TailscaleMsiSignature.Evidence Evidence => Volatile.Read(ref disposed) == 0 ? evidence : throw new ObjectDisposedException(nameof(Lease));
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed,1) != 0) return;
            try { held.Dispose(); cleanup(); }
            finally { gate.Dispose(); }
        }
    }
}
