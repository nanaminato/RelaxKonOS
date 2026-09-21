namespace RelaxKonOS.Protocol.Common;

/// <summary>Server 或其本地身份提供方所在的宿主操作系统。</summary>
public enum HostPlatformKind
{
    Linux,
    Windows
}

/// <summary>连接到 RelaxKonOS 的客户端所运行的平台。</summary>
public enum ClientPlatformKind
{
    Windows,
    Linux,
    Android,
    iOS
}
