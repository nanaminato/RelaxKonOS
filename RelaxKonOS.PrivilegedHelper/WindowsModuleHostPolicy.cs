using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Machine administrator-owned paths. Launch requests cannot provide or override them.</summary>
public sealed record WindowsModuleHostPolicy(string ExecutablePath, string SourceRoot, string IsolationRoot)
{
    public void Validate()
    {
        foreach (var path in new[] { ExecutablePath, SourceRoot, IsolationRoot })
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
                || Path.GetFullPath(path).TrimEnd('\\', '/') == Path.GetPathRoot(path)?.TrimEnd('\\', '/')
                || path.Split(['/', '\\']).Any(segment => segment is "." or ".."))
                throw new InvalidOperationException("ModuleHost paths must be local absolute paths below a volume root.");
        if (Path.GetFileName(ExecutablePath) != "RelaxKonOS.ServerModuleHost.exe"
            || WindowsManagedRuntimePolicy.Contains(SourceRoot, ExecutablePath)
            || WindowsManagedRuntimePolicy.Contains(SourceRoot, IsolationRoot)
            || WindowsManagedRuntimePolicy.Contains(IsolationRoot, SourceRoot))
            throw new InvalidOperationException("ModuleHost runtime, ingress and private roots must be separate.");
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsModuleHostSecurity
{
    internal static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    internal static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly Lazy<SecurityIdentifier> Installer = new(() => (SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier)));
    private const FileSystemRights Mutation = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    internal static DirectorySecurity DirectoryAcl(SecurityIdentifier? module, FileSystemRights rights)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(System);
        Add(acl, System, FileSystemRights.FullControl);
        Add(acl, Administrators, FileSystemRights.FullControl);
        if (module is not null)
        {
            if (module == System || module == Administrators) throw new UnauthorizedAccessException();
            Add(acl, module, rights);
        }
        return acl;
    }
    private static void Add(DirectorySecurity acl, SecurityIdentifier sid, FileSystemRights rights) =>
        acl.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

    internal static void RequireProtected(string path, bool ancestors = true)
    {
        WindowsManagedRuntimePolicy.RequireNoLinks(path);
        var current = Path.GetFullPath(path);
        var first = true;
        while (!string.IsNullOrEmpty(current))
        {
            FileSystemSecurity acl = Directory.Exists(current)
                ? new DirectoryInfo(current).GetAccessControl() : new FileInfo(current).GetAccessControl();
            var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ?? throw new UnauthorizedAccessException();
            // Windows volume/Program Files ancestors can be owned by TrustedInstaller.
            var installer = Installer.Value;
            if (owner != System && owner != Administrators && owner != installer) throw new UnauthorizedAccessException("ModuleHost path owner is not trusted.");
            foreach (var rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>())
            {
                if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference.Equals(System)
                    || rule.IdentityReference.Equals(Administrators) || rule.IdentityReference.Equals(installer)
                    || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
                var forbidden = first ? Mutation : FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
                    | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if ((rule.FileSystemRights & forbidden) != 0) throw new UnauthorizedAccessException("ModuleHost path is writable by an untrusted identity.");
            }
            if (!ancestors || current == Path.GetPathRoot(current)) break;
            first = false;
            current = Path.GetDirectoryName(current)!;
        }
    }
    internal static void CreateProtectedDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            RequireProtected(Path.GetDirectoryName(path)!);
            new DirectoryInfo(path).Create(DirectoryAcl(null, 0));
        }
        RequireProtected(path);
    }
    internal static void RequirePrivateData(string path, SecurityIdentifier module)
    {
        WindowsManagedRuntimePolicy.RequireNoLinks(path);
        var acl = new DirectoryInfo(path).GetAccessControl();
        if (!acl.AreAccessRulesProtected || acl.GetOwner(typeof(SecurityIdentifier))?.Equals(System) != true)
            throw new UnauthorizedAccessException("ModuleHost private data ACL changed.");
        foreach (var rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>())
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(System)
                && !rule.IdentityReference.Equals(Administrators) && !rule.IdentityReference.Equals(module))
                throw new UnauthorizedAccessException("ModuleHost private data ACL is too broad.");
    }

    internal static void GrantRuntimeRead(string root, SecurityIdentifier module)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
        {
            RequireProtected(path);
            if (Directory.Exists(path))
            {
                var acl = new DirectoryInfo(path).GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(module, FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(acl);
            }
            else
            {
                var acl = new FileInfo(path).GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(module, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(acl);
            }
        }
    }
}
