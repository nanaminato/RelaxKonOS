using System.Runtime.InteropServices;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

// Database access only: no install session, transforms, actions, or writes.
internal static class TailscaleMsiProperties
{
    internal sealed record Evidence(string ProductName, string Manufacturer, string Version, string ProductCode,
        string UpgradeCode, string PackageCode, string Template);
    internal static Evidence Read(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new RuntimePolicyException(ComponentProblemCodes.PlatformUnsupported);
        Require(MsiOpenDatabaseW(path,IntPtr.Zero,out var database)); // MSIDBOPEN_READONLY
        try
        {
            Require(MsiGetSummaryInformationW(database,null,0,out var summary));
            try
            {
                return new(Property(database,"ProductName"),Property(database,"Manufacturer"),Property(database,"ProductVersion"),
                    Property(database,"ProductCode"),Property(database,"UpgradeCode"),Summary(summary,9),Summary(summary,7));
            }
            finally { _ = MsiCloseHandle(summary); }
        }
        finally { _ = MsiCloseHandle(database); }
    }
    internal static void Validate(Evidence data, ComponentDownloadRelease release)
    {
        RelaxKonOS.ManagedRuntimes.ComponentPackages.TailscaleMsiArtifactTransfer.Validate(release);
        if (data.ProductName != "Tailscale" || data.Manufacturer != "Tailscale Inc." || data.Version != release.Version
            || data.Template != (release.Rid switch { "win-x64" => "x64;1033", "win-arm64" => "Arm64;1033", _ => "" })
            || !GuidValue(data.ProductCode) || !GuidValue(data.PackageCode) || !GuidValue(data.UpgradeCode)
            || data.ProductCode == data.PackageCode || data.ProductCode == data.UpgradeCode || data.PackageCode == data.UpgradeCode)
            throw Invalid();
        // The upgrade code is observed evidence only until the per-architecture lifecycle policy is registered.
        // Never enumerate/uninstall products on the basis of this value alone.
    }
    private static bool GuidValue(string value) => Guid.TryParseExact(value,"B",out var parsed) && parsed != Guid.Empty
        && value == parsed.ToString("B").ToUpperInvariant();
    private static string Property(uint database, string property)
    {
        // All call sites supply fixed identifiers; no user text is interpolated into SQL.
        Require(MsiDatabaseOpenViewW(database,"SELECT `Value` FROM `Property` WHERE `Property` = '"+property+"'",out var view));
        try
        {
            Require(MsiViewExecute(view,0)); Require(MsiViewFetch(view,out var record));
            string value;
            try
            {
                var text = new StringBuilder(257); uint size = 257;
                Require(MsiRecordGetStringW(record,1,text,ref size));
                if (size is 0 or > 256) throw Invalid();
                value = text.ToString();
            }
            finally { _ = MsiCloseHandle(record); }
            var next = MsiViewFetch(view,out var extra);
            if (extra != 0) _ = MsiCloseHandle(extra);
            if (next != 259) throw Invalid(); // ERROR_NO_MORE_ITEMS
            return value;
        }
        finally { _ = MsiCloseHandle(view); }
    }
    private static string Summary(uint summary, uint property)
    {
        var text = new StringBuilder(257); uint size = 257;
        Require(MsiSummaryInfoGetPropertyW(summary,property,out var type,out _,out _,text,ref size));
        if (type != 30 || size is 0 or > 256) throw Invalid(); // VT_LPSTR
        return text.ToString();
    }
    private static void Require(uint result) { if (result != 0) throw Invalid(); }
    private static RuntimePolicyException Invalid() => new("component.tailscale_msi_properties_invalid");
    [DllImport("msi.dll",ExactSpelling=true,CharSet=CharSet.Unicode),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiOpenDatabaseW(string path, IntPtr mode, out uint database);
    [DllImport("msi.dll",ExactSpelling=true,CharSet=CharSet.Unicode),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiDatabaseOpenViewW(uint database,string query,out uint view);
    [DllImport("msi.dll",ExactSpelling=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiViewExecute(uint view,uint record);
    [DllImport("msi.dll",ExactSpelling=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiViewFetch(uint view,out uint record);
    [DllImport("msi.dll",ExactSpelling=true,CharSet=CharSet.Unicode),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiRecordGetStringW(uint record,uint field,StringBuilder value,ref uint size);
    [DllImport("msi.dll",ExactSpelling=true,CharSet=CharSet.Unicode),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiGetSummaryInformationW(uint database,string? path,uint updateCount,out uint summary);
    [DllImport("msi.dll",ExactSpelling=true,CharSet=CharSet.Unicode),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiSummaryInfoGetPropertyW(uint summary,uint property,out uint type,out int integer,
        out System.Runtime.InteropServices.ComTypes.FILETIME time,StringBuilder value,ref uint size);
    [DllImport("msi.dll",ExactSpelling=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiCloseHandle(uint handle);
}
