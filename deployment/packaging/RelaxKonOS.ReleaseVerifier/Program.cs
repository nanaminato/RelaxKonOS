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

if ((args.Length == 4 && args[0] == "verify") || (args.Length == 5 && args[0] == "extract"))
{
    var kind = args[2] switch
    {
        "server" => ServerReleasePackageKind.Server,
        "user-server" => ServerReleasePackageKind.UserServer,
        _ => (ServerReleasePackageKind?)null
    };
    var runtime = args[3] switch
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
        var result = args[0] == "extract"
            ? ServerReleaseArchiveVerifier.VerifyAndExtract(stream, args[4], kind.Value, runtime.Value)
            : ServerReleaseArchiveVerifier.Verify(stream, kind.Value, runtime.Value);
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

Console.Error.WriteLine("usage: RelaxKonOS.ReleaseVerifier verify ZIP KIND RID");
Console.Error.WriteLine("   or: RelaxKonOS.ReleaseVerifier extract ZIP KIND RID DESTINATION");
Console.Error.WriteLine("   or: RelaxKonOS.ReleaseVerifier validate-request REQUEST_JSON");
return 64;
