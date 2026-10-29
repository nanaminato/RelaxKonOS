using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

// No ownership is inferred from registry names, a service, files, or a downloaded MSI.
internal sealed class TailscaleWindowsInstallation(
    Func<CancellationToken,IReadOnlyList<TailscaleWindowsInventory.Product>> registrations,
    Func<TailscaleWindowsInstallation.Presence> programs,
    Func<TailscaleWindowsInstallation.Presence> data,
    Func<TailscaleWindowsInstallation.Presence> service)
{
    internal enum Presence { Absent, Present, Unavailable }
    internal enum Kind { NoKnownArtifacts, ExistingUnowned, Unavailable }
    internal sealed record Observation(Kind Kind,Presence Programs,Presence Data,Presence Service,
        IReadOnlyList<TailscaleWindowsInventory.Product>? Registrations,string? ProblemCode);
    internal static TailscaleWindowsInstallation Create()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess
            || RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new RuntimePolicyException(ComponentProblemCodes.PlatformUnsupported);
        var inventory = TailscaleWindowsInventory.Create();
        return new(inventory.Read,
            () => Combine(DirectoryPresence(Environment.SpecialFolder.ProgramFiles),DirectoryPresence(Environment.SpecialFolder.ProgramFilesX86)),
            () => DirectoryPresence(Environment.SpecialFolder.CommonApplicationData),ServicePresence);
    }
    internal Observation Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<TailscaleWindowsInventory.Product>? items = null; string? problem = null;
        try { items = Array.AsReadOnly(registrations(ct).ToArray()); }
        catch (RuntimePolicyException ex) { problem = ex.ProblemCode; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { problem = "component.tailscale_inventory_unavailable"; }
        ct.ThrowIfCancellationRequested();
        Presence Probe(Func<Presence> probe)
        {
            ct.ThrowIfCancellationRequested(); Presence result;
            try { result = probe(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { result = Presence.Unavailable; }
            ct.ThrowIfCancellationRequested();
            return Enum.IsDefined(result) ? result : Presence.Unavailable;
        }
        var files = Probe(programs); var retained = Probe(data); var scm = Probe(service);
        var unknown = items is null || files == Presence.Unavailable || retained == Presence.Unavailable || scm == Presence.Unavailable;
        var exists = items?.Count > 0 || files == Presence.Present || retained == Presence.Present || scm == Presence.Present;
        return new(unknown ? Kind.Unavailable : exists ? Kind.ExistingUnowned : Kind.NoKnownArtifacts,
            files,retained,scm,items,unknown ? problem ?? "component.tailscale_installation_unavailable" : null);
    }
    private static Presence Combine(Presence first,Presence second) => first == Presence.Unavailable || second == Presence.Unavailable
        ? Presence.Unavailable : first == Presence.Present || second == Presence.Present ? Presence.Present : Presence.Absent;
    private static Presence DirectoryPresence(Environment.SpecialFolder folder)
    {
        var parent = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(parent) || parent.Length < 3 || !char.IsAsciiLetter(parent[0]) || parent[1] != ':' || parent[2] != '\\')
            return Presence.Unavailable;
        try
        {
            // Observation is not a path-ownership/permission check (ProgramData permits creating siblings).
            // Mutation must independently establish private directories and protected installation ownership.
            for (var current = Path.GetFullPath(parent); current is not null; current = Path.GetDirectoryName(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) return Presence.Unavailable;
            }
            // Any object (including a link or an empty directory) at the reserved name blocks fresh installation.
            try { _ = File.GetAttributes(Path.Combine(parent,"Tailscale")); return Presence.Present; }
            catch (FileNotFoundException) { return Presence.Absent; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Presence.Unavailable; }
    }
    private static Presence ServicePresence()
    {
        using var manager = OpenSCManagerW(null,null,1); // SC_MANAGER_CONNECT, local only
        if (manager.IsInvalid) return Presence.Unavailable;
        using var handle = OpenServiceW(manager,"Tailscale",4); // SERVICE_QUERY_STATUS; no start/stop/delete rights
        if (!handle.IsInvalid) return Presence.Present;
        return Marshal.GetLastPInvokeError() == 1060 ? Presence.Absent : Presence.Unavailable;
    }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ServiceHandle OpenSCManagerW(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",ExactSpelling=true,CharSet=CharSet.Unicode,SetLastError=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager,string name,uint access);
    [DllImport("advapi32.dll",ExactSpelling=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
