using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Read-only ACL grants for explicitly selected public static content.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsNginxStaticSiteAccess
{
    public static void Grant(string? path, WindowsManagedRuntimePolicy policy)
    {
        policy.Validate();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute static-site directory is required.");
        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volume = Path.GetPathRoot(root);
        if (string.IsNullOrEmpty(volume) || root.StartsWith(@"\\", StringComparison.Ordinal)
            || root == volume.TrimEnd('\\', '/') || !Directory.Exists(root))
            throw new UnauthorizedAccessException("An existing local directory below a volume root is required.");
        foreach (var protectedRoot in new[] { policy.PrivateRoot, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            if (!string.IsNullOrEmpty(protectedRoot) && (WindowsManagedRuntimePolicy.Contains(protectedRoot, root)
                || WindowsManagedRuntimePolicy.Contains(root, protectedRoot)))
                throw new UnauthorizedAccessException("Protected directories cannot be published.");
        if ((WindowsManagedRuntimePolicy.Contains(policy.NginxRoot, root)
                && !WindowsManagedRuntimePolicy.Contains(Path.Combine(policy.NginxRoot, "sites"), root))
            || WindowsManagedRuntimePolicy.Contains(root, policy.NginxRoot))
            throw new UnauthorizedAccessException("Runtime executables and configuration cannot be published.");
        WindowsManagedRuntimePolicy.RequireNoLinks(root);

        // Inspect before changing ACLs. Never descend through a junction or symbolic link.
        var directories = new List<string> { root };
        var files = new List<string>();
        for (var index = 0; index < directories.Count; index++)
            foreach (var entry in Directory.EnumerateFileSystemEntries(directories[index]))
            {
                WindowsManagedRuntimePolicy.RequireNoLinks(entry);
                if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory)) directories.Add(entry);
                else files.Add(entry);
                if (directories.Count + files.Count > 100_000)
                    throw new UnauthorizedAccessException("Static-site directory tree is too large for ACL grant.");
            }

        using var identity = WindowsIdentity.GetCurrent();
        var worker = identity.User
            ?? throw new UnauthorizedAccessException("Nginx runtime identity is unavailable.");
        var readers = policy.ReaderSids.Select(sid => new SecurityIdentifier(sid)).Distinct().ToArray();
        GrantContentAccess(directories, files, worker, readers);
        for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
        {
            WindowsManagedRuntimePolicy.RequireNoLinks(parent.FullName);
            var security = parent.GetAccessControl();
            foreach (var sid in readers.Append(worker).Distinct())
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Traverse,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            parent.SetAccessControl(security);
        }
    }

    internal static void GrantContentAccess(IReadOnlyList<string> directories, IReadOnlyList<string> files,
        SecurityIdentifier worker, IReadOnlyList<SecurityIdentifier> readers)
    {
        foreach (var directory in directories)
        {
            WindowsManagedRuntimePolicy.RequireNoLinks(directory);
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(worker, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            // Server identities can validate directory metadata without reading website content.
            foreach (var reader in readers)
                security.AddAccessRule(new FileSystemAccessRule(reader, FileSystemRights.ReadAttributes | FileSystemRights.Traverse,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        foreach (var file in files)
        {
            WindowsManagedRuntimePolicy.RequireNoLinks(file);
            var info = new FileInfo(file);
            var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(worker, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
    }
}
