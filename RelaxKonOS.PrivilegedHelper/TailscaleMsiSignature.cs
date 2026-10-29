using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

// A read-only verification primitive, not an installation permit or a persisted trust decision.
internal static class TailscaleMsiSignature
{
    internal sealed record Evidence(string Sha256, string Publisher, string CertificateSha256, TailscaleMsiProperties.Evidence Properties);
    internal static async Task<Evidence> InspectAsync(string path, ComponentDownloadRelease release, CancellationToken ct)
    {
        TailscaleMsiArtifactTransfer.Validate(release);
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new RuntimePolicyException(ComponentProblemCodes.PlatformUnsupported);
        await using var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan);
        if (file.Length is <= 0 or > TailscaleMsiArtifactTransfer.MaximumBytes) throw Invalid();
        var digest = await SHA256.HashDataAsync(file,ct);
        if (!CryptographicOperations.FixedTimeEquals(digest,Convert.FromHexString(release.Sha256!))) throw Invalid();
        ct.ThrowIfCancellationRequested();
        file.Position = 0;
        // Keep this same handle locked against writes/deletion until trust and publisher checks finish.
        using var certificate = TrustedSigner(file);
        ct.ThrowIfCancellationRequested();
        if (!PublisherMatches(certificate.SubjectName.RawData)) throw Invalid();
        var properties = TailscaleMsiProperties.Read(file.Name);
        TailscaleMsiProperties.Validate(properties,release);
        ct.ThrowIfCancellationRequested();
        return new(Convert.ToHexStringLower(digest),"Tailscale Inc. / CA / 1131559-5",certificate.GetCertHashString(HashAlgorithmName.SHA256),properties);
    }
    internal static bool PublisherMatches(ReadOnlyMemory<byte> subject)
    {
        // Legal identity, independent of the certificate's display name, key, thumbprint or RDN ordering.
        var expected = new Dictionary<string,string>(StringComparer.Ordinal)
        {
            ["2.5.4.3"] = "Tailscale Inc.", ["2.5.4.10"] = "Tailscale Inc.", ["2.5.4.7"] = "Toronto",
            ["2.5.4.8"] = "Ontario", ["2.5.4.6"] = "CA", ["2.5.4.5"] = "1131559-5",
            ["2.5.4.15"] = "Private Organization", ["1.3.6.1.4.1.311.60.2.1.3"] = "CA"
        };
        try
        {
            var root = new AsnReader(subject,AsnEncodingRules.DER);
            var sequence = root.ReadSequence(); root.ThrowIfNotEmpty();
            while (sequence.HasData)
            {
                var set = sequence.ReadSetOf(); var attribute = set.ReadSequence(); set.ThrowIfNotEmpty();
                var oid = attribute.ReadObjectIdentifier(); var tag = attribute.PeekTag();
                if (tag.TagClass != TagClass.Universal || tag.IsConstructed || tag.TagValue is not (12 or 19 or 20 or 22 or 30)) return false;
                var value = attribute.ReadCharacterString((UniversalTagNumber)tag.TagValue); attribute.ThrowIfNotEmpty();
                if (!expected.Remove(oid,out var required) || value != required) return false;
            }
            return expected.Count == 0;
        }
        catch (AsnContentException) { return false; }
    }
    private static RuntimePolicyException Invalid() => new("component.tailscale_signature_invalid");
    private static X509Certificate2 TrustedSigner(FileStream file)
    {
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        var info = new FileInfoNative { Size = (uint)Marshal.SizeOf<FileInfoNative>(), Path = file.Name, Handle = file.SafeFileHandle.DangerousGetHandle() };
        var signature = new SignatureSettings { Size = (uint)Marshal.SizeOf<SignatureSettings>(), Flags = 1 }; // WSS_VERIFY_SPECIFIC, primary signature only
        var infoPointer = IntPtr.Zero;
        var signaturePointer = IntPtr.Zero;
        var infoCreated = false;
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, StateAction = 1,
            // Chain revocation excluding root, cache-only retrieval, disable MD2/MD4. Never weaken on cache miss.
            Flags = 0x80 | 0x1000 | 0x2000 };
        try
        {
            infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoNative>());
            signaturePointer = Marshal.AllocHGlobal(Marshal.SizeOf<SignatureSettings>());
            data.File = infoPointer; data.Signature = signaturePointer;
            Marshal.StructureToPtr(info,infoPointer,false); infoCreated = true;
            Marshal.StructureToPtr(signature,signaturePointer,false);
            var status = WinVerifyTrust(new IntPtr(-1),ref action,ref data);
            if (status != 0)
            {
                if (unchecked((uint)status) is 0x80092012 or 0x80092013 or 0x800B010E)
                    throw new RuntimePolicyException("component.tailscale_signature_revocation_unavailable");
                throw Invalid();
            }
            if (data.State == IntPtr.Zero) throw Invalid();
            var provider = WTHelperProvDataFromStateData(data.State);
            if (provider == IntPtr.Zero) throw Invalid();
            var signer = WTHelperGetProvSignerFromChain(provider,0,false,0);
            if (signer == IntPtr.Zero) throw Invalid();
            var certificate = WTHelperGetProvCertFromChain(signer,0);
            if (certificate == IntPtr.Zero) throw Invalid();
            var prefix = Marshal.PtrToStructure<ProviderCertificatePrefix>(certificate);
            if (prefix.Size < Marshal.SizeOf<ProviderCertificatePrefix>() || prefix.Certificate == IntPtr.Zero) throw Invalid();
            var context = Marshal.PtrToStructure<CertificateContext>(prefix.Certificate);
            if (context.Encoded == IntPtr.Zero || context.Length is 0 or > 65536) throw Invalid();
            var encoded = new byte[context.Length]; Marshal.Copy(context.Encoded,encoded,0,encoded.Length);
            // Read from the verified WinTrust state; do not independently re-select a file's signer.
            return X509CertificateLoader.LoadCertificate(encoded);
        }
        finally
        {
            if (data.State != IntPtr.Zero) { data.StateAction = 2; _ = WinVerifyTrust(new IntPtr(-1),ref action,ref data); }
            if (infoCreated) Marshal.DestroyStructure<FileInfoNative>(infoPointer);
            Marshal.FreeHGlobal(infoPointer); Marshal.FreeHGlobal(signaturePointer);
            GC.KeepAlive(file);
        }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct FileInfoNative { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SignatureSettings { public uint Size, Index, Flags, SecondaryCount, VerifiedIndex; public IntPtr Policy; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice; public IntPtr File;
        public uint StateAction; public IntPtr State, Url; public uint Flags, Context; public IntPtr Signature;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificatePrefix { public uint Size; public IntPtr Certificate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext { public uint Encoding; public IntPtr Encoded; public uint Length; public IntPtr Info, Store; }
    [DllImport("wintrust.dll",ExactSpelling=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll",ExactSpelling=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll",ExactSpelling=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counter);
    [DllImport("wintrust.dll",ExactSpelling=true),DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);
}
