using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

// Immutable artifact observations, not source approvals, installation ownership, or execution permits.
internal sealed class TailscaleMsiPins(string root,Action<string> require)
{
    internal sealed record Pin(ComponentDownloadRelease Release,TailscaleMsiSignature.Evidence Evidence);
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true, MaxDepth = 16
    };
    internal void Check(ComponentDownloadRelease release)
    {
        TailscaleMsiArtifactTransfer.Validate(release);
        var pin = Read(release);
        if (pin is not null && pin.Release != Normalize(release)) throw Conflict();
    }
    internal void Remember(ComponentDownloadRelease release,TailscaleMsiSignature.Evidence evidence)
    {
        var candidate = new Pin(Normalize(release),evidence); Validate(candidate);
        var existing = Read(release);
        if (existing is not null)
        {
            if (candidate != existing) throw Conflict();
            return;
        }
        require(root); var directory = Path.Combine(root,"pins");
        Directory.CreateDirectory(directory); require(directory);
        var path = FileName(release); var temporary = path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using (var file = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                require(temporary); file.Write(JsonSerializer.SerializeToUtf8Bytes(candidate,Json)); file.Flush(true);
            }
            require(directory); require(temporary); File.Move(temporary,path,overwrite:false); require(path);
        }
        finally { if (File.Exists(temporary)) { require(temporary); File.Delete(temporary); } }
    }
    private Pin? Read(ComponentDownloadRelease release)
    {
        require(root); var directory = Path.Combine(root,"pins");
        if (!Directory.Exists(directory)) return null;
        require(directory); var path = FileName(release);
        if (!File.Exists(path)) return null;
        require(path);
        try
        {
            using var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if (file.Length is <= 0 or > 16384) throw Invalid();
            var bytes = new byte[file.Length]; file.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth = 16 });
            _ = TailscaleNativeControl.Canonical(document.RootElement); // Reject duplicate properties recursively.
            var pin = JsonSerializer.Deserialize<Pin>(bytes,Json) ?? throw Invalid(); Validate(pin);
            if (pin.Release.Version != release.Version || pin.Release.Rid != release.Rid) throw Invalid();
            return pin;
        }
        catch (JsonException) { throw Invalid(); }
        catch (ArgumentException) { throw Invalid(); }
        catch (IOException) { throw Invalid(); }
    }
    private string FileName(ComponentDownloadRelease release) => Path.Combine(root,"pins",release.Rid+"-"+release.Version+".json");
    private static ComponentDownloadRelease Normalize(ComponentDownloadRelease release) => release with { Sha256 = release.Sha256?.ToLowerInvariant() };
    private static void Validate(Pin pin)
    {
        TailscaleMsiArtifactTransfer.Validate(pin.Release);
        TailscaleMsiProperties.Validate(pin.Evidence.Properties,pin.Release);
        if (pin.Evidence.Sha256 != pin.Release.Sha256 || pin.Evidence.Publisher != "Tailscale Inc. / CA / 1131559-5"
            || pin.Evidence.CertificateSha256 is not { Length:64 } || !pin.Evidence.CertificateSha256.All(Uri.IsHexDigit)) throw Invalid();
    }
    private static RuntimePolicyException Invalid() => new("component.tailscale_pin_invalid");
    private static RuntimePolicyException Conflict() => new("component.tailscale_artifact_conflict");
}
