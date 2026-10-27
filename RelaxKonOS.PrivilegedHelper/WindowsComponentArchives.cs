using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

[SupportedOSPlatform("windows")]
internal static partial class WindowsManagedRuntimeHost
{
    private sealed record NginxArchiveReceipt(Guid OperationId, ComponentPinnedArchive Pin);
    private static ComponentPinnedArchive NginxPin(WindowsNginxRelease release) => new(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(release.Version + "|" + release.Sha256.ToLowerInvariant()))),
        release.Version, WindowsManagedRuntimePolicy.Rid, $"https://nginx.org/download/nginx-{release.Version}.zip",
        release.Sha256.ToLowerInvariant(), "zip", MaximumArchiveBytes, [ComponentIds.Nginx]);
    private static bool SamePin(ComponentPinnedArchive expected, ComponentPinnedArchive actual) =>
        expected.ReleaseId == actual.ReleaseId && expected.Version == actual.Version && expected.Rid == actual.Rid
        && expected.SourceUri == actual.SourceUri && expected.Sha256 == actual.Sha256 && expected.Format == actual.Format
        && expected.MaximumBytes == actual.MaximumBytes && expected.AffectedComponents.SequenceEqual(actual.AffectedComponents);
    public static async Task<PrivilegedOperationResult> PinnedNginxAsync(PrivilegedOperationRequest request, WindowsManagedRuntimePolicy? policy)
    {
        if (policy is null) return Failure(PrivilegedProblemCode.ResourceNotAllowed);
        var clean = new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId,
            Correlation: request.Correlation, Version: request.Version,
            ArchiveExpectation: request.Operation == PrivilegedOperationKind.NginxArchiveInstall ? request.ArchiveExpectation : null,
            DownloadProxy: request.Operation == PrivilegedOperationKind.NginxArchiveInstall ? request.DownloadProxy : null);
        if (clean != request) return Failure(PrivilegedProblemCode.InvalidRequest);
        await Gate.WaitAsync();
        try
        {
            policy.Validate();
            WindowsManagedRuntimePolicy.RequireNoLinks(policy.NginxRoot);
            var pins = policy.NginxReleases.Select(NginxPin).ToArray();
            var receiptPath = Path.Combine(policy.NginxRoot, ".helper-component-install.json");
            NginxArchiveReceipt? receipt = null;
            if (File.Exists(receiptPath))
            {
                WindowsManagedRuntimePolicy.RequireNoLinks(receiptPath);
                if (new FileInfo(receiptPath).Length > 262144) return Failure(PrivilegedProblemCode.InvalidRequest);
                receipt = JsonSerializer.Deserialize<NginxArchiveReceipt>(await File.ReadAllBytesAsync(receiptPath));
                if (receipt is null || receipt.OperationId == Guid.Empty || receipt.Pin.AffectedComponents is not [ComponentIds.Nginx])
                    return Failure(PrivilegedProblemCode.InvalidRequest);
            }
            var installed = Directory.Exists(policy.NginxRoot) || File.Exists(policy.NginxRoot);
            var managed = installed && await VerifyIntegrityAsync(policy.NginxRoot, "nginx.exe");
            if (request.Operation == PrivilegedOperationKind.NginxArchiveQuery)
                return new(true, ComponentArchive: new(installed, managed, managed ? await ReadNginxVersionAsync(policy.NginxRoot) : null,
                    pins, managed ? receipt?.OperationId : null, receipt is not null && !managed));
            if (request.ArchiveExpectation is not { } expected || request.OperationId is not { } operationId || operationId == Guid.Empty
                || !pins.Any(pin => SamePin(expected, pin))) return Failure(PrivilegedProblemCode.AccessDenied);
            if (installed)
                return managed && receipt is not null && receipt.OperationId == operationId && SamePin(receipt.Pin, expected)
                    && await VerifyPinnedNginxAsync(policy.NginxRoot, expected) ? new(true) : Failure(PrivilegedProblemCode.Conflict);
            return await NginxAsync(new(ManagedRuntime.Nginx, ManagedRuntimeAction.Install, Version: expected.Version), policy, expected, operationId, request.DownloadProxy);
        }
        catch (RelaxKonOS.ManagedRuntimes.RuntimePolicyException) { return Failure(PrivilegedProblemCode.AccessDenied); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return Failure(PrivilegedProblemCode.ResourceNotAllowed); }
        finally { Gate.Release(); }
    }
    private static async Task<string?> ReadNginxVersionAsync(string root)
    {
        using var process = new System.Diagnostics.Process { StartInfo = StartInfo(Path.Combine(root, "nginx.exe"), root, ["-v"]) };
        if (!process.Start()) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout + await stderr;
            var match = System.Text.RegularExpressions.Regex.Match(output, @"nginx/([0-9]+\.[0-9]+\.[0-9]+)");
            return process.ExitCode == 0 && output.Length <= 4096 && match.Success ? match.Groups[1].Value : null;
        }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); return null; }
    }
    private static async Task<bool> VerifyPinnedNginxAsync(string directory, ComponentPinnedArchive pin)
    {
        using var process = new System.Diagnostics.Process { StartInfo = StartInfo(Path.Combine(directory, "nginx.exe"), directory, ["-v"]) };
        if (!process.Start()) return false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var text = await stdout + await stderr;
            if (text.Length > 4096 || process.ExitCode != 0 || System.Text.RegularExpressions.Regex.Match(text, @"nginx/([0-9]+\.[0-9]+\.[0-9]+)").Groups[1].Value != pin.Version) return false;
            return (await NginxCommandAsync(directory, ["-t"])).Success;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            return false;
        }
    }
}
