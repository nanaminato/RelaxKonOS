using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

if (args.Length == 2 && args[0] == "validate-request")
{
    try
    {
        var request = await File.ReadAllBytesAsync(args[1]);
        return ServerDeploymentRequestWireValidation.IsStrictRequest(request) ? 0 : 1;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine("Deployment request could not be read: " + error.Message);
        return 1;
    }
}

if ((args.Length == 6 && args[0] == "verify") || (args.Length == 7 && args[0] == "extract"))
{
    var kind = args[4] switch
    {
        "server" => ServerReleasePackageKind.Server,
        "user-server" => ServerReleasePackageKind.UserServer,
        _ => (ServerReleasePackageKind?)null
    };
    var runtime = args[5] switch
    {
        "win-x64" => ServerRuntimeIdentifier.WinX64,
        "win-arm64" => ServerRuntimeIdentifier.WinArm64,
        "linux-x64" => ServerRuntimeIdentifier.LinuxX64,
        "linux-arm64" => ServerRuntimeIdentifier.LinuxArm64,
        _ => (ServerRuntimeIdentifier?)null
    };
    if (kind is null || runtime is null)
    {
        Console.Error.WriteLine("Package kind or RID is invalid.");
        return 64;
    }

    try
    {
        using var stream = File.OpenRead(args[1]);
        var key = await File.ReadAllTextAsync(args[2]);
        var trustedKeys = new Dictionary<string, string> { [args[3]] = key };
        var result = args[0] == "extract"
            ? ServerReleaseArchiveVerifier.VerifyAndExtract(stream, args[6], kind.Value, runtime.Value, trustedKeys)
            : ServerReleaseArchiveVerifier.Verify(stream, kind.Value, runtime.Value, trustedKeys);
        if (!result.Verified)
        {
            Console.Error.WriteLine(result.ProblemCode);
            return 1;
        }
        Console.WriteLine($"Verified {result.Manifest!.PackageKind} {result.Manifest.Version} " +
                          $"{result.Manifest.Runtime}: {result.Manifest.Files.Count} files");
        return 0;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
    {
        Console.Error.WriteLine("Release verification failed: " + error.Message);
        return 1;
    }
}

if (args.Length != 4 || args[0] != "sign")
{
    Console.Error.WriteLine("usage: RelaxKonOS.ReleaseSigner sign FILE PRIVATE_KEY_PEM KEY_ID");
    Console.Error.WriteLine("   or: RelaxKonOS.ReleaseSigner verify ZIP PUBLIC_KEY_PEM KEY_ID KIND RID");
    Console.Error.WriteLine("   or: RelaxKonOS.ReleaseSigner extract ZIP PUBLIC_KEY_PEM KEY_ID KIND RID DESTINATION");
    Console.Error.WriteLine("   or: RelaxKonOS.ReleaseSigner validate-request REQUEST_JSON");
    return 64;
}

var file = Path.GetFullPath(args[1]);
var keyFile = Path.GetFullPath(args[2]);
var keyId = args[3];
if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 128 ||
    keyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
{
    Console.Error.WriteLine("Release key ID is invalid.");
    return 64;
}

try
{
    var bytes = await File.ReadAllBytesAsync(file);
    var pem = await File.ReadAllTextAsync(keyFile);
    using var rsa = RSA.Create();
    var passphrase = Environment.GetEnvironmentVariable("RELAXKONOS_RELEASE_KEY_PASSPHRASE");
    if (passphrase is null) rsa.ImportFromPem(pem);
    else rsa.ImportFromEncryptedPem(pem, passphrase);

    var signature = new ServerReleaseSignatureDto(
        ServerDeploymentProtocol.Version, keyId, ServerReleaseSignatureAlgorithms.RsaPssSha256,
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    var signaturePath = file + ".sig";
    var temporary = signaturePath + ".new";
    await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(signature, RelaxKonOSJsonOptions.Default));
    File.Move(temporary, signaturePath, overwrite: true);
    Console.WriteLine(signaturePath);
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException
                              or ArgumentException)
{
    Console.Error.WriteLine("Release signing failed: " + error.Message);
    return 1;
}
