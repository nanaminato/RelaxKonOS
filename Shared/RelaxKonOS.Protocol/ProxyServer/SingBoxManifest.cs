using System.Runtime.InteropServices;

namespace RelaxKonOS.Protocol.ProxyServer;

public sealed record SingBoxRelease(string Platform, string Architecture, string Sha256)
{
    public string FileName => $"sing-box-{SingBoxManifest.Version}-{Platform}-{Architecture}." + (Platform == "windows" ? "zip" : "tar.gz");
    public Uri Url => new($"https://github.com/SagerNet/sing-box/releases/download/v{SingBoxManifest.Version}/{FileName}");
}

public static class SingBoxManifest
{
    public const string Version = "1.14.3";
    public const long MaximumArchiveBytes = 128 * 1024 * 1024;
    public static IReadOnlyList<SingBoxRelease> Releases { get; } =
    [
        new("windows", "amd64", "d8b86ef58936dba9230ec6a57ffa4243933f3c14ef4809828b6c553440ed1518"),
        new("windows", "arm64", "195b35424c6e3f5913954c7f08c5d8ba2d2199f6d18eb27a3972a5a29cdaa78c"),
        new("linux", "amd64", "e2bdf179c15a3652955dc44867e33ca0965c9c9b91b34267dcc5a5d639a5feee"),
        new("linux", "arm64", "29dae24d76bea3bc62d07dd3e60cfc12003de7debbd6b768df9be9c68360174e"),
    ];
    public static SingBoxRelease? Current => Releases.SingleOrDefault(r =>
        r.Platform == (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "")
        && r.Architecture == (RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "amd64", Architecture.Arm64 => "arm64", _ => "" }));
}
