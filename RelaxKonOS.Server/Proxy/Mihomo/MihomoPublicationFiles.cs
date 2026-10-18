using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>The Server-owned mirror is a drift check, never an independent publication authority.</summary>
internal sealed class MihomoPublicationFiles(IProxyPlatformPaths paths)
{
    private string Active => Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
    internal bool Exists()
    {
        RuntimeSourceStore.RequireNoLinks(Active);
        if (Directory.Exists(Active)) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        return File.Exists(Active);
    }

    internal async Task InitializeAsync(MihomoPublicationState state, CancellationToken ct)
    {
        if (state.Published is not null || state.Pending is not { InstallationPlan: not null } pending)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        if (Exists())
        {
            if (await IdentityAsync(ct) != pending.Identity) throw new RuntimePolicyException("runtime.publication_unconfirmed");
            return; // Exact crash-window candidate only, never adopt an unrelated existing file.
        }
        var directory = Path.GetDirectoryName(Active)!;
        RuntimeSourceStore.RequireNoLinks(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".publication");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options))
            { await output.WriteAsync(Encoding.UTF8.GetBytes(pending.Candidate.Yaml), ct); output.Flush(true); }
            RuntimeSourceStore.RequireNoLinks(Active);
            File.Move(temporary, Active, false);
        }
        finally { File.Delete(temporary); }
    }
    internal async Task<string> IdentityAsync(CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(Active);
        await using var input = new FileStream(Active, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is 0 or > 4 * 1024 * 1024) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal async Task DeleteRemovedAsync(MihomoPublicationState state, CancellationToken ct)
    {
        if (state.Pending is not { RemovalPlan.Request.KeepData: false } || state.Identity is null)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        // Absence is the exact crash window after this deletion and before the encrypted state commit.
        if (!Exists()) return;
        if (await IdentityAsync(ct) != state.Identity) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        RuntimeSourceStore.RequireNoLinks(Active);
        File.Delete(Active);
    }

    internal async Task ReplaceAsync(MihomoPublicationState state, string yaml, CancellationToken ct)
    {
        var identity = await IdentityAsync(ct);
        if (identity != state.Identity && identity != state.Pending?.Identity)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var bytes = Encoding.UTF8.GetBytes(yaml);
        var target = MihomoPublicationStore.Identity(yaml);
        if (target != state.Identity && target != state.Pending?.Identity)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var directory = Path.GetDirectoryName(Active)!;
        RuntimeSourceStore.RequireNoLinks(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".publication");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options)) { await output.WriteAsync(bytes, ct); output.Flush(true); }
            RuntimeSourceStore.RequireNoLinks(Active);
            File.Move(temporary, Active, true);
        }
        finally { File.Delete(temporary); }
    }
}
