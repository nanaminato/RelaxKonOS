using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

// MSI registration observation only. Neither an empty result nor a product match grants ownership.
internal sealed class TailscaleWindowsInventory(TailscaleWindowsInventory.IBackend backend)
{
    internal const int MaximumProducts = 512;
    internal sealed record Instance(string ProductCode,uint Context,[property:JsonIgnore] string? UserSid)
    { public override string ToString() => "Windows Installer product instance"; }
    internal sealed record Product(string ProductCode,uint Context,bool Advertised,string? Version,string? Publisher);
    internal interface IBackend
    {
        Instance? Enumerate(uint index);
        string Property(Instance instance,string name);
    }
    internal static TailscaleWindowsInventory Create()
    {
        if (!OperatingSystem.IsWindows()) throw new RuntimePolicyException(ComponentProblemCodes.PlatformUnsupported);
        return new(new Native());
    }
    internal IReadOnlyList<Product> Read(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew(); var products = new List<Product>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Check() { ct.ThrowIfCancellationRequested(); if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw Unavailable(); }
        string Property(Instance item,string name)
        {
            Check(); var value = backend.Property(item,name); Check();
            if (string.IsNullOrEmpty(value) || value.Length > 512 || value.Any(char.IsControl)) throw Unavailable();
            return value;
        }
        for (uint i = 0; i <= MaximumProducts; i++)
        {
            Check(); var item = backend.Enumerate(i); Check();
            if (item is null) return products.AsReadOnly();
            if (i == MaximumProducts || !Guid.TryParseExact(item.ProductCode,"B",out var id) || id == Guid.Empty
                || item.ProductCode != id.ToString("B").ToUpperInvariant() || item.Context is not (1 or 2 or 4)
                || (item.Context == 4 ? !string.IsNullOrEmpty(item.UserSid) : string.IsNullOrEmpty(item.UserSid))
                || !seen.Add(item.ProductCode+"/"+item.Context+"/"+item.UserSid)) throw Unavailable();
            var state = Property(item,"State");
            if (state is not ("1" or "5")) throw Unavailable();
            var name = Property(item,state == "5" ? "InstalledProductName" : "ProductName");
            if (!string.Equals(name,"Tailscale",StringComparison.OrdinalIgnoreCase)) continue;
            products.Add(new(item.ProductCode,item.Context,state == "1",state == "5" ? Property(item,"VersionString") : null,
                state == "5" ? Property(item,"Publisher") : null));
        }
        throw Unavailable();
    }
    private static RuntimePolicyException Unavailable() => new("component.tailscale_inventory_unavailable");
    private static void RequireNative(uint result)
    {
        if (result == 5) throw new RuntimePolicyException("component.tailscale_inventory_access_denied");
        if (result != 0) throw Unavailable();
    }
    private sealed class Native : IBackend
    {
        public Instance? Enumerate(uint index)
        {
            var product = new StringBuilder(39); var sid = new StringBuilder(257); uint size = 257;
            // Everyone + ALL: include other users and machine context; never silently narrow to the current user.
            var result = MsiEnumProductsExW(null,"S-1-1-0",7,index,product,out var context,sid,ref size);
            if (result == 259) return null;
            RequireNative(result);
            if (size > 256) throw Unavailable();
            return new(product.ToString(),context,sid.Length == 0 ? null : sid.ToString());
        }
        public string Property(Instance item,string name)
        {
            var text = new StringBuilder(513); uint size = 513;
            RequireNative(MsiGetProductInfoExW(item.ProductCode,item.UserSid,item.Context,name,text,ref size));
            if (size > 512) throw Unavailable();
            return text.ToString();
        }
    }
    [DllImport("msi.dll",ExactSpelling=true,CharSet=CharSet.Unicode),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiEnumProductsExW(string? product,string sid,uint context,uint index,StringBuilder code,out uint installedContext,StringBuilder userSid,ref uint size);
    [DllImport("msi.dll",ExactSpelling=true,CharSet=CharSet.Unicode),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint MsiGetProductInfoExW(string product,string? sid,uint context,string property,StringBuilder value,ref uint size);
}
