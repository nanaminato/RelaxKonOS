using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

public static class PinnedArchives
{
    public static ComponentPinnedArchive Frp(WindowsFrpRelease pin, long maximum = 128L * 1024 * 1024) => new(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pin.Version + "|" + pin.Rid + "|" + pin.Sha256))),
        pin.Version, pin.Rid, pin.Url, pin.Sha256.ToLowerInvariant(), pin.ArchiveFormat, maximum,
        [ComponentIds.Frpc, ComponentIds.Frps]);
    public static WindowsFrpRelease LinuxFrp(bool arm64) => new("v0.71.0", arm64 ? "linux-arm64" : "linux-x64",
        $"https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_linux_{(arm64 ? "arm64" : "amd64")}.tar.gz",
        arm64 ? "f33c293c275d8fc68c654b6fba8f10b2551d6463d09a9fc9cffb7227eae82266" : "84f27e39f11169f7adcef8e8b70c9329de17747b1f14dad9fb95eef5682ea716", "tar.gz");
    public static bool Same(ComponentPinnedArchive a, ComponentPinnedArchive b) => a.ReleaseId == b.ReleaseId
        && a.Version == b.Version && a.Rid == b.Rid && a.SourceUri == b.SourceUri && a.Sha256 == b.Sha256
        && a.Format == b.Format && a.MaximumBytes == b.MaximumBytes && a.AffectedComponents.SequenceEqual(b.AffectedComponents);
}
