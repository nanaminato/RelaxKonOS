using RelaxKonOS.Core.VirtualSystemDrive;

namespace RelaxKonOS.Client.Services.Developer;

/// <summary>
/// The Virtual System Drive refused a write that an installation requires.
/// </summary>
/// <remarks>
/// The drive has exactly one root, <c>{LocalApplicationData}/RelaxKonOS/SystemDrive</c>, so an install
/// can never relocate itself when that root is read-only, owned by another account, or blocked by a
/// sandbox policy. Reporting the package folder (not a random staging id) lets the installer tell the
/// user which folder to fix, instead of surfacing the file system's own "Access to the path … is
/// denied" for an internal path nobody can act on.
/// </remarks>
public sealed class PackageStorageUnavailableException(string folder, Exception inner)
    : IOException($"{VirtualSystemDriveProblemCode.PackageStorageUnavailable}: {folder}", inner)
{
    /// <summary>Package folder that the host could not write, for example <c>…/Programs/External</c>.</summary>
    public string Folder { get; } = folder;

    /// <summary>Stable diagnostics code shared by logs and the installer message.</summary>
    public string ProblemCode => VirtualSystemDriveProblemCode.PackageStorageUnavailable;

    /// <summary>
    /// Maps a file system denial into this problem. Anything else is returned unchanged so that
    /// invalid archives and manifest errors keep their own message.
    /// </summary>
    public static Exception From(Exception exception, string folder) =>
        exception is UnauthorizedAccessException or DirectoryNotFoundException
            ? new PackageStorageUnavailableException(folder, exception)
            : exception;
}
