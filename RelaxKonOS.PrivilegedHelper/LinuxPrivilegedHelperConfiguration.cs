using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Root-owned configuration of the persistent Linux Helper. It deliberately contains no operation
/// policy: file roots and allowed services stay in the fixed <c>/etc/relaxkonos</c> files that the
/// installer already owns. Only transport and caller identity are configurable here.
/// </summary>
public sealed record LinuxPrivilegedHelperConfiguration(
    [property: JsonPropertyName("socketPath")] string SocketPath,
    [property: JsonPropertyName("socketGroupId")] uint SocketGroupId,
    [property: JsonPropertyName("allowedUids")] IReadOnlyList<uint> AllowedUserIds,
    [property: JsonPropertyName("maximumConcurrentRequests")] int MaximumConcurrentRequests = 8)
{
    public void Validate()
    {
        // sun_path is 108 bytes including the terminator. Staying under 100 leaves room for the
        // temporary/probe suffix without ever truncating the configured path.
        if (string.IsNullOrWhiteSpace(SocketPath) || !Path.IsPathFullyQualified(SocketPath) || SocketPath.Length > 100
            || SocketPath.EndsWith(Path.DirectorySeparatorChar)
            || SocketPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            throw new InvalidOperationException("The privileged helper socket path is invalid.");
        if (AllowedUserIds is null || AllowedUserIds.Count == 0)
            throw new InvalidOperationException("The privileged helper requires at least one authorized caller uid.");
        if (AllowedUserIds.Count > 64 || AllowedUserIds.Distinct().Count() != AllowedUserIds.Count)
            throw new InvalidOperationException("The privileged helper caller list is invalid.");
        if (MaximumConcurrentRequests is < 1 or > RelaxKonOS.Protocol.Privileged.PrivilegedHelperTransportProtocol.MaximumAllowedConcurrentRequests)
            throw new InvalidOperationException("The privileged helper concurrency limit is invalid.");
    }

    /// <summary>
    /// The caller identity list is the second authorization boundary after the socket ACL. A
    /// configuration that cannot be read is a startup failure, never an empty (allow-none) fallback
    /// that would silently disable the Helper.
    /// </summary>
    public static LinuxPrivilegedHelperConfiguration Load(string path)
    {
        var json = File.ReadAllText(path);
        var configuration = JsonSerializer.Deserialize<LinuxPrivilegedHelperConfiguration>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The privileged helper configuration is invalid.");
        configuration.Validate();
        return configuration;
    }
}

/// <summary>Small stat view used to defend socket and configuration paths.</summary>
internal readonly record struct UnixFileStatus(bool IsSocket, bool IsDirectory, bool IsSymbolicLink,
    uint OwnerId, uint GroupId, UnixFileMode Mode);

/// <summary>
/// Filesystem operations that must be testable without root. The Linux implementation uses statx so
/// a socket can be distinguished from a regular file or directory before it is ever unlinked.
/// </summary>
internal interface IUnixSocketFileSystem
{
    uint EffectiveUserId { get; }
    UnixFileStatus? Stat(string path);
    void CreateDirectory(string path, UnixFileMode mode);
    void SetMode(string path, UnixFileMode mode);
    void SetOwner(string path, uint userId, uint groupId);
    void Delete(string path);
}

[SupportedOSPlatform("linux")]
internal sealed class LinuxUnixSocketFileSystem : IUnixSocketFileSystem
{
    private const int AtFdcwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const uint StatxUid = 0x8;
    private const uint StatxGid = 0x10;
    private const ushort FileTypeMask = 0xF000;
    private const ushort SocketType = 0xC000;
    private const ushort DirectoryType = 0x4000;
    private const ushort SymbolicLinkType = 0xA000;
    private const int StatxBufferSize = 256;

    public uint EffectiveUserId => geteuid();

    public UnixFileStatus? Stat(string path)
    {
        var buffer = new byte[StatxBufferSize];
        if (statx(AtFdcwd, path, AtSymlinkNoFollow, StatxType | StatxUid | StatxGid, buffer) != 0) return null;
        var mode = BitConverter.ToUInt16(buffer, 28);
        var type = (ushort)(mode & FileTypeMask);
        return new UnixFileStatus(type == SocketType, type == DirectoryType, type == SymbolicLinkType,
            BitConverter.ToUInt32(buffer, 20), BitConverter.ToUInt32(buffer, 24), (UnixFileMode)(mode & 0x0FFF));
    }

    public void CreateDirectory(string path, UnixFileMode mode)
    {
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, mode);
    }

    public void SetMode(string path, UnixFileMode mode) => File.SetUnixFileMode(path, mode);

    public void SetOwner(string path, uint userId, uint groupId)
    {
        if (chown(path, userId, groupId) != 0)
            throw new IOException($"Could not assign the socket owner (errno {Marshal.GetLastPInvokeError()}).");
    }

    public void Delete(string path) => File.Delete(path);

    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int statx(int directoryFileDescriptor, string path, int flags, uint mask, byte[] buffer);

    [DllImport("libc", SetLastError = true, EntryPoint = "chown")]
    private static extern int chown(string path, uint owner, uint group);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();
}

/// <summary>Result of the kernel peer-credential check performed before any request is read.</summary>
internal readonly record struct HelperPeerIdentity(bool Allowed, int ProcessId, uint UserId, uint GroupId, string Reason);

internal interface IHelperPeerAuthorizer
{
    HelperPeerIdentity Authorize(Socket socket);
}

/// <summary>
/// Linux <c>SO_PEERCRED</c> check. Socket ownership and mode remain the first boundary; this check
/// makes a caller outside the configured uid list fail even if the ACL is misconfigured, and it is
/// never replaced by resolving <c>/proc/&lt;pid&gt;/exe</c>.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxPeerAuthorizer(IReadOnlyList<uint> allowedUserIds) : IHelperPeerAuthorizer
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    public HelperPeerIdentity Authorize(Socket socket)
    {
        var buffer = new byte[12];
        var length = buffer.Length;
        if (getsockopt((int)socket.Handle, SolSocket, SoPeerCred, buffer, ref length) != 0)
            return new(false, 0, 0, 0, "peer credentials are unavailable");
        var processId = BitConverter.ToInt32(buffer, 0);
        var userId = BitConverter.ToUInt32(buffer, 4);
        var groupId = BitConverter.ToUInt32(buffer, 8);
        var allowed = userId == 0 || allowedUserIds.Contains(userId);
        return new(allowed, processId, userId, groupId, allowed ? "authorized" : "caller uid is not authorized");
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "getsockopt")]
    private static extern int getsockopt(int socket, int level, int option, byte[] value, ref int length);
}
