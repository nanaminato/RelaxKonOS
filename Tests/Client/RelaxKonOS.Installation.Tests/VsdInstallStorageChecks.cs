using System.Security.AccessControl;
using System.Security.Principal;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Core.VirtualSystemDrive;

internal static class VsdInstallStorageChecks
{
    private const string PackageFolder = @"C:\Users\example\AppData\Local\RelaxKonOS\SystemDrive\Programs\External";

    public static void Run()
    {
        // 1. A denial has to name the package folder, not the per-attempt staging id the file system
        //    happens to have reported, so the installer can tell the user what to fix.
        var denial = new UnauthorizedAccessException(@"Access to the path 'C:\…\.staging\6c7a' is denied.");
        var translated = PackageStorageUnavailableException.From(denial, PackageFolder);
        var storage = translated as PackageStorageUnavailableException;
        Check(storage is not null && storage.Folder == PackageFolder && ReferenceEquals(storage.InnerException, denial),
            "a denied package write is reported against the package folder and keeps the original error.");
        Check(storage!.ProblemCode == VirtualSystemDriveProblemCode.PackageStorageUnavailable
              && storage.Message.Contains(VirtualSystemDriveProblemCode.PackageStorageUnavailable, StringComparison.Ordinal)
              && storage.Message.Contains(PackageFolder, StringComparison.Ordinal),
            "the storage failure message carries the stable problem code and the folder for diagnostics.");

        // 2. Everything else must keep its own message: a broken archive is not a permissions problem.
        var invalidArchive = new InvalidDataException("The archive entry is corrupt.");
        Check(ReferenceEquals(PackageStorageUnavailableException.From(invalidArchive, PackageFolder), invalidArchive),
            "an invalid package is never reported as unavailable storage.");

        // 3. Exercise a real denial. The reported failure came from the file system refusing a new
        //    folder, so the mapping must hold for the exception Windows actually raises.
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: real access-denial check requires Windows access control.");
            return;
        }

        var root = Path.Combine(AppContext.BaseDirectory, "install-storage-" + Guid.NewGuid().ToString("N"));
        var denied = Path.Combine(root, "Programs", "External");
        Directory.CreateDirectory(denied);
        var directory = new DirectoryInfo(denied);
        var original = directory.GetAccessControl();
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
        try
        {
            var restricted = new DirectorySecurity();
            restricted.AddAccessRule(new FileSystemAccessRule(user,
                FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles, AccessControlType.Deny));
            directory.SetAccessControl(restricted);

            Exception? raised = null;
            try { Directory.CreateDirectory(Path.Combine(denied, Guid.NewGuid().ToString("N"))); }
            catch (UnauthorizedAccessException exception) { raised = exception; }
            Check(raised is not null, "a read-only package folder refuses a new staging directory.");
            Check(PackageStorageUnavailableException.From(raised!, denied) is PackageStorageUnavailableException real
                  && real.Folder == denied,
                "a real access denial inside the package folder becomes the storage problem.");
        }
        finally
        {
            directory.SetAccessControl(original);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS: " + label);
    }
}
