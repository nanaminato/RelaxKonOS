using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Protocol.Privileged;

/// <summary>NDJSON on stdout; each Windows pipe frame authenticates exactly one serialized frame.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PrivilegedOperationFrame(string Type, string Version,
    InstallationStage? Stage = null, int? Progress = null, PrivilegedOperationResult? Result = null)
{
    public const int MaximumProgressFrameBytes = 1024;
    public const int MaximumFrames = 20000;
    public static PrivilegedOperationFrame Completed(PrivilegedOperationResult result) => new("result", PrivilegedOperationProtocol.Version, Result: result);
    public static PrivilegedOperationFrame Report(InstallationStage stage, int? progress = null) => new("progress", PrivilegedOperationProtocol.Version, stage, progress);
    public bool IsValid() => Version == PrivilegedOperationProtocol.Version && (Type switch
    {
        "progress" => Result is null && Stage is InstallationStage.Preflight or InstallationStage.Preparing
            or InstallationStage.UpdatingPackageLists or InstallationStage.Downloading or InstallationStage.Copying
            or InstallationStage.Verifying or InstallationStage.Extracting or InstallationStage.Installing
            or InstallationStage.Configuring or InstallationStage.Activating or InstallationStage.HealthChecking or InstallationStage.RollingBack
            && Progress is null or >= 0 and <= 100
            && (Progress is null || Stage is InstallationStage.Downloading or InstallationStage.Copying or InstallationStage.Installing or InstallationStage.UpdatingPackageLists),
        "result" => Stage is null && Progress is null && Result is { } result
            && result.Version == PrivilegedOperationProtocol.Version && Enum.IsDefined(result.ProblemCode),
        _ => false
    });
}
