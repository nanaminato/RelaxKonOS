using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.PrivilegedHelper;

[SupportedOSPlatform("windows")]
internal static class WindowsModuleHostAccount
{
    internal sealed record Identity(int SchemaVersion, string ModuleId, string AccountName, string Sid);

    internal static Identity Ensure(string moduleRoot, string moduleId, string isolationRoot)
    {
        var name = "rkmod-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(isolationRoot).ToUpperInvariant() + "\n" + moduleId)))[..14];
        var marker = Path.Combine(moduleRoot, "identity.json");
        if (File.Exists(marker))
        {
            WindowsModuleHostSecurity.RequireProtected(marker);
            var identity = ModulePackageJson.Read<Identity>(File.ReadAllBytes(marker), 8192);
            if (identity.SchemaVersion != 2 || identity.ModuleId != moduleId || identity.AccountName != name
                || Lookup(name)?.Value != identity.Sid) throw new UnauthorizedAccessException("ModuleHost account identity changed.");
            DenyInteractiveLogon(new SecurityIdentifier(identity.Sid));
            return identity;
        }
        // Never adopt a pre-existing account or silently recreate a lost identity marker.
        if (Lookup(name) is not null) throw new UnauthorizedAccessException("ModuleHost account requires recovery.");
        var info = new UserInfo1
        {
            Name = name, Password = "Aa1!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            Privilege = 1, Flags = 0x200 | 0x1 | 0x10000,
            Comment = "RelaxKonOS ModuleHost dedicated account",
        };
        var result = NetUserAdd(null, 1, ref info, out _);
        info.Password = null;
        if (result != 0) throw new Win32Exception((int)result);
        try
        {
            var sid = Lookup(name) ?? throw new UnauthorizedAccessException();
            DenyInteractiveLogon(sid);
            var identity = new Identity(2, moduleId, name, sid.Value);
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(identity, ModulePackageJson.Options));
            stream.Flush(true);
            return identity;
        }
        catch
        {
            // No module has run yet; remove only the account created by this invocation.
            _ = NetUserDel(null, name);
            throw;
        }
    }
    private static SecurityIdentifier? Lookup(string name)
    {
        try { return (SecurityIdentifier)new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier)); }
        catch (IdentityNotMappedException) { return null; }
    }
    /// <summary>Explicit local cleanup of this installation's recorded account; never accepts an arbitrary username.</summary>
    internal static void RemoveRecorded(string moduleRoot, string moduleId, string isolationRoot)
    {
        var marker = Path.Combine(moduleRoot, "identity.json");
        WindowsModuleHostSecurity.RequireProtected(marker);
        var identity = ModulePackageJson.Read<Identity>(File.ReadAllBytes(marker), 8192);
        var expected = "rkmod-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(isolationRoot).ToUpperInvariant() + "\n" + moduleId)))[..14];
        if (identity.SchemaVersion != 2 || identity.ModuleId != moduleId || identity.AccountName != expected
            || Lookup(expected)?.Value != identity.Sid) throw new UnauthorizedAccessException("ModuleHost cleanup identity changed.");
        var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>() };
        Check(LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x810, out var policy));
        var bytes = new byte[new SecurityIdentifier(identity.Sid).BinaryLength];
        new SecurityIdentifier(identity.Sid).GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { Check(LsaRemoveAccountRights(policy, pinned.AddrOfPinnedObject(), true, IntPtr.Zero, 0)); }
        finally { pinned.Free(); LsaClose(policy); }
        var result = NetUserDel(null, expected);
        if (result != 0) throw new Win32Exception((int)result);
        File.Delete(marker);
    }
    private static void DenyInteractiveLogon(SecurityIdentifier sid)
    {
        var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>() };
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x810, out var policy);
        Check(status);
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        var pinned = GCHandle.Alloc(sidBytes, GCHandleType.Pinned);
        var rights = new[] { "SeDenyInteractiveLogonRight", "SeDenyRemoteInteractiveLogonRight", "SeNetworkLogonRight" }
            .Select(right => new UnicodeString { Buffer = Marshal.StringToHGlobalUni(right), Length = (ushort)(right.Length * 2), MaximumLength = (ushort)((right.Length + 1) * 2) }).ToArray();
        try { Check(LsaAddAccountRights(policy, pinned.AddrOfPinnedObject(), rights, (uint)rights.Length)); }
        finally
        {
            foreach (var right in rights) Marshal.FreeHGlobal(right.Buffer);
            pinned.Free();
            LsaClose(policy);
        }
    }
    private static void Check(uint status) { if (status != 0) throw new Win32Exception((int)LsaNtStatusToWinError(status)); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct UserInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name, Password;
        public uint PasswordAge, Privilege;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Home, Comment;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Script;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Descriptor, Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetUserAdd(string? server, uint level, ref UserInfo1 info, out uint parameter);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetUserDel(string? server, string user);
    [DllImport("advapi32.dll")] private static extern uint LsaOpenPolicy(IntPtr system, ref ObjectAttributes attributes, uint access, out IntPtr policy);
    [DllImport("advapi32.dll")] private static extern uint LsaAddAccountRights(IntPtr policy, IntPtr sid, UnicodeString[] rights, uint count);
    [DllImport("advapi32.dll")] private static extern uint LsaRemoveAccountRights(IntPtr policy, IntPtr sid, [MarshalAs(UnmanagedType.Bool)] bool all, IntPtr rights, uint count);
    [DllImport("advapi32.dll")] private static extern uint LsaClose(IntPtr policy);
    [DllImport("advapi32.dll")] private static extern uint LsaNtStatusToWinError(uint status);
}
