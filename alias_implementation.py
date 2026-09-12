from pathlib import Path
def edit(path, old, new):
 p=Path(path); s=p.read_text(encoding='utf-8-sig'); assert old in s,(path,old[:100]); p.write_text(s.replace(old,new),encoding='utf-8')
def write(path,s):
 Path(path).write_text(s,encoding='utf-8')
edit('RelaxKonOS.Server/Identity/LinuxPamProvider.cs', 'IntPtr pamHandle = IntPtr.Zero;', '''var before = Lookup(username);
        if (before.Identity is null) return CredentialVerifyResult.Failed("Identity unavailable", CredentialError.BadCredentials);
        IntPtr pamHandle = IntPtr.Zero;''')
edit('RelaxKonOS.Server/Identity/LinuxPamProvider.cs', '''return pamStatus == PamResult.Success
                ? CredentialVerifyResult.Ok(Environment.MachineName, username)
                : MapFailure(pamStatus, pamHandle);''','''if (pamStatus != PamResult.Success) return MapFailure(pamStatus, pamHandle);
            if (pam_get_item(pamHandle, 2, out var pamUser) != 0 || Utf8(pamUser) is not { } verifiedName)
                return CredentialVerifyResult.Failed("Identity unavailable", CredentialError.BadCredentials);
            var after = Lookup(verifiedName);
            return after.Identity is { } verified && verified.Uid == before.Identity.Uid
                ? CredentialVerifyResult.Ok(verified)
                : CredentialVerifyResult.Failed("Identity mismatch", CredentialError.BadCredentials);''')
edit('RelaxKonOS.Server/Identity/LinuxPamProvider.cs','new PlatformUserInfo(entry.Uid.ToString(), displayName, Utf8(entry.Directory))','new PlatformUserInfo(entry.Uid.ToString(), canonicalName, RelaxKonOS.Protocol.Common.PlatformKind.Linux, displayName, Utf8(entry.Directory))')
edit('RelaxKonOS.Server/Identity/LinuxPamProvider.cs','    private static CredentialVerifyResult MapFailure', '''    public IdentityLookup Lookup(string identifier)
    {
        try { return new(IdentityLookupStatus.Found, GetUserInfo(identifier)); }
        catch (KeyNotFoundException) { return new(IdentityLookupStatus.NotFound); }
        catch { return new(IdentityLookupStatus.Unavailable); }
    }

    public IdentityLookup LookupIdentity(string identity)
    {
        if (!uint.TryParse(identity, out var uid)) return new(IdentityLookupStatus.Unavailable);
        var buffer = Marshal.AllocHGlobal(1_048_576);
        try
        {
            var error = getpwuid_r(uid, out var entry, buffer, 1_048_576, out var found);
            if (error != 0) return new(IdentityLookupStatus.Unavailable);
            if (found == IntPtr.Zero) return new(IdentityLookupStatus.NotFound);
            return Lookup(Utf8(entry.Name)!);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // Enabling an unverified PAM stack would bypass account policy. Distribution integration
    // must explicitly establish account-only semantics before this capability is enabled.
    public AliasEligibility CheckAliasEligibility(PlatformUserInfo identity)
        => new(false, "linux-pam-account-check-unverified");

    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pam_get_item(IntPtr handle, int item, out IntPtr value);
    [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
    private static extern int getpwuid_r(uint uid, out Passwd entry, IntPtr buffer, nuint length, out IntPtr result);

    private static CredentialVerifyResult MapFailure''')
edit('RelaxKonOS.Server/Identity/WindowsLogonProvider.cs','using System.Text;', 'using System.Text;\nusing System.Security.Principal;\nusing RelaxKonOS.Protocol.Common;')
edit('RelaxKonOS.Server/Identity/WindowsLogonProvider.cs','domain ??= Environment.MachineName;   // 纯用户名 → 默认验证本机','if (!userName.Contains(\'@\')) domain ??= Environment.MachineName;')
edit('RelaxKonOS.Server/Identity/WindowsLogonProvider.cs','''if (ok)
                return CredentialVerifyResult.Ok(domain, user);''','''if (ok)
            {
                using var identity = new WindowsIdentity(token);
                var sid = identity.User?.Value;
                var lookup = Lookup(userName);
                return lookup.Identity is { } info && info.Uid == sid
                    ? CredentialVerifyResult.Ok(info)
                    : CredentialVerifyResult.Failed("Identity mismatch", CredentialError.BadCredentials);
            }''')
start=Path('RelaxKonOS.Server/Identity/WindowsLogonProvider.cs').read_text().index('    public PlatformUserInfo GetUserInfo')
end=Path('RelaxKonOS.Server/Identity/WindowsLogonProvider.cs').read_text().index('    private static void EnsureAccountExists')
p=Path('RelaxKonOS.Server/Identity/WindowsLogonProvider.cs'); s=p.read_text(); s=s[:start]+'''    public PlatformUserInfo GetUserInfo(string userName)
    {
        var lookup = Lookup(userName);
        return lookup.Identity ?? throw new InvalidOperationException("Windows identity unavailable.");
    }

    public IdentityLookup Lookup(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Contains('\\0')) return new(IdentityLookupStatus.NotFound);
        var name = identifier.Contains('\\\\') || identifier.Contains('@') ? identifier : Environment.MachineName + "\\\\" + identifier;
        uint sidLength = 0, domainLength = 0;
        LookupAccountName(null, name, IntPtr.Zero, ref sidLength, null, ref domainLength, out _);
        var error = Marshal.GetLastWin32Error();
        if (error == 1332) return new(IdentityLookupStatus.NotFound);
        if (error != ERROR_INSUFFICIENT_BUFFER || sidLength == 0) return new(IdentityLookupStatus.Unavailable);
        var buffer = Marshal.AllocHGlobal((int)sidLength);
        try
        {
            var domain = new StringBuilder((int)domainLength);
            if (!LookupAccountName(null, name, buffer, ref sidLength, domain, ref domainLength, out var use))
                return new(IdentityLookupStatus.Unavailable);
            if (use != SidNameUse.User) return new(IdentityLookupStatus.NotFound);
            var sid = new SecurityIdentifier(buffer);
            var canonical = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            return new(IdentityLookupStatus.Found, new(sid.Value, canonical, PlatformKind.Windows, canonical, GetProfileDirectory(canonical)));
        }
        catch { return new(IdentityLookupStatus.Unavailable); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public IdentityLookup LookupIdentity(string identity)
    {
        try { return Lookup(((NTAccount)new SecurityIdentifier(identity).Translate(typeof(NTAccount))).Value); }
        catch (IdentityNotMappedException) { return new(IdentityLookupStatus.NotFound); }
        catch { return new(IdentityLookupStatus.Unavailable); }
    }

    public AliasEligibility CheckAliasEligibility(PlatformUserInfo identity)
    {
        var parts = identity.Username.Split('\\\\', 2);
        if (parts.Length != 2 || !parts[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return new(false, "windows-domain-account-check-unverified");
        var status = NetUserGetInfo(null, parts[1], 4, out var buffer);
        if (status != 0) return new(false, "account-state-unavailable");
        try
        {
            var info = Marshal.PtrToStructure<UserInfo4>(buffer);
            if ((info.Flags & (2u | 16u)) != 0 || info.AccountExpires != uint.MaxValue && info.AccountExpires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return new(false, "account-ineligible");
            if (info.UserSid == IntPtr.Zero || new SecurityIdentifier(info.UserSid).Value != identity.Uid)
                return new(false, "identity-mismatch");
            return new(true);
        }
        finally { NetApiBufferFree(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo4
    {
        public IntPtr Name, Password;
        public uint PasswordAge, Privilege;
        public IntPtr HomeDirectory, Comment;
        public uint Flags;
        public IntPtr ScriptPath;
        public uint AuthFlags;
        public IntPtr FullName, UserComment, Parameters, Workstations;
        public uint LastLogon, LastLogoff, AccountExpires, MaxStorage, UnitsPerWeek;
        public IntPtr LogonHours;
        public uint BadPasswordCount, NumberLogons;
        public IntPtr LogonServer;
        public uint CountryCode, CodePage;
        public IntPtr UserSid;
        public uint PrimaryGroupId;
        public IntPtr Profile, HomeDirectoryDrive;
        public uint PasswordExpired;
    }
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);
    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

'''+s[end:]; p.write_text(s,encoding='utf-8')
edit(str(p), '''user = raw[..at];
            domain = raw[(at + 1)..];''','''user = raw;
            domain = null;''')
edit(str(p),'string lpszUsername, string lpszDomain, string lpszPassword','string lpszUsername, string? lpszDomain, string lpszPassword')
edit('RelaxKonOS.Server/Storage/UserRepository.cs','    User? FindById(Guid id);','    User? FindById(Guid id);\n    User? FindByIdentity(string identity, PlatformKind platform);\n    void Update(User user);')
edit('RelaxKonOS.Server/Storage/UserRepository.cs','    public User Add(User user)','''    public User? FindByIdentity(string identity, PlatformKind platform) => _byId.Values.SingleOrDefault(u => u.PlatformIdentity == identity && u.Platform == platform);
    public void Update(User user) => _byId[user.Id] = user;
    public User Add(User user)''')
edit('RelaxKonOS.Server/Storage/Sqlite/SqliteUserRepository.cs','    public User Add(User user)','''    public User? FindByIdentity(string identity, PlatformKind platform)
        => _db.Users.AsNoTracking().SingleOrDefault(u => u.PlatformIdentity == identity && u.Platform == platform);
    public void Update(User user)
    {
        var tracked = _db.Users.Local.FirstOrDefault(u => u.Id == user.Id);
        if (tracked is not null) _db.Entry(tracked).State = EntityState.Detached;
        _db.Users.Update(user);
        _db.SaveChanges();
    }
    public User Add(User user)''')
edit('RelaxKonOS.Server/Storage/Sqlite/RelaxKonOSDbContext.cs','e.HasIndex(u => new { u.Username, u.Platform }).IsUnique();','e.HasIndex(u => new { u.Platform, u.PlatformIdentity }).IsUnique();')
