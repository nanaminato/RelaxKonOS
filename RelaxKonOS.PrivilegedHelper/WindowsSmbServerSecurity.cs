using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Fixed binding to the Windows SMB Server WMI provider. This is deliberately not a general WMI
/// surface: namespace, class, query, method, and writable properties are all compile-time constants.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSmbServerSecurity
{
    private const string ScopePath = @"\\.\ROOT\Microsoft\Windows\Smb";
    private const string ClassName = "MSFT_SmbServerConfiguration";
    private const string Query = "SELECT EnableSMB1Protocol,EnableSMB2Protocol,EnableAuthenticateUserSharing,NullSessionShares,NullSessionPipes FROM MSFT_SmbServerConfiguration";

    public static PrivilegedOperationResult Read()
    {
        try
        {
            using var configuration = OpenConfiguration();
            return Output(Snapshot(configuration));
        }
        catch (ManagementException) { return Fail(PrivilegedProblemCode.NotFound, "Windows SMB Server configuration API is unavailable"); }
        catch (UnauthorizedAccessException) { return Fail(PrivilegedProblemCode.AccessDenied, "Windows SMB Server configuration access was denied"); }
        catch (InvalidOperationException) { return Fail(PrivilegedProblemCode.NotFound, "Windows SMB Server configuration API is unavailable"); }
    }

    /// <summary>Applies the one frozen V1 baseline after checking the last observed server snapshot.</summary>
    public static PrivilegedOperationResult ApplyBaseline(string? expectedSnapshot)
    {
        try
        {
            using var configuration = OpenConfiguration();
            var before = Snapshot(configuration);
            if (!string.IsNullOrWhiteSpace(expectedSnapshot) && !string.Equals(before.SnapshotHash, expectedSnapshot, StringComparison.Ordinal))
                return Fail(PrivilegedProblemCode.Conflict, "Windows SMB Server security changed externally");

            if (!before.Compliant)
            {
                using var parameters = configuration.GetMethodParameters("SetConfiguration");
                CopyCurrentMethodValues(configuration, parameters);
                // These are the only five global SMB server values V1 may mutate. No caller can
                // supply a WMI class, method, property name, or value.
                parameters["EnableSMB1Protocol"] = false;
                parameters["EnableSMB2Protocol"] = true;
                parameters["EnableAuthenticateUserSharing"] = true;
                parameters["NullSessionShares"] = string.Empty;
                parameters["NullSessionPipes"] = string.Empty;
                var result = configuration.InvokeMethod("SetConfiguration", parameters, null);
                if (ReturnCode(result) != 0) return Fail(PrivilegedProblemCode.InternalError, "Windows SMB Server security apply failed");
            }

            using var verified = OpenConfiguration();
            var after = Snapshot(verified);
            return after.Compliant ? Output(after) : Fail(PrivilegedProblemCode.InternalError, "Windows SMB Server security verification failed");
        }
        catch (ManagementException) { return Fail(PrivilegedProblemCode.NotFound, "Windows SMB Server configuration API is unavailable"); }
        catch (UnauthorizedAccessException) { return Fail(PrivilegedProblemCode.AccessDenied, "Windows SMB Server configuration access was denied"); }
        catch (InvalidOperationException) { return Fail(PrivilegedProblemCode.NotFound, "Windows SMB Server configuration API is unavailable"); }
    }

    private static ManagementObject OpenConfiguration()
    {
        var scope = new ManagementScope(ScopePath);
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(Query));
        using var results = searcher.Get();
        var found = results.Cast<ManagementObject>().SingleOrDefault()
            ?? throw new InvalidOperationException("SMB server configuration was not found");
        var configuration = new ManagementObject(scope, found.Path, null);
        configuration.Get();
        return configuration;
    }

    private static void CopyCurrentMethodValues(ManagementObject configuration, ManagementBaseObject parameters)
    {
        foreach (PropertyData parameter in parameters.Properties)
        {
            try { parameters[parameter.Name] = configuration[parameter.Name]; }
            catch (ManagementException) { /* Output-only method properties are intentionally untouched. */ }
        }
    }

    private static SmbWindowsServerSecuritySnapshot Snapshot(ManagementObject configuration)
    {
        var smb1 = ReadBoolean(configuration, "EnableSMB1Protocol");
        var smb2 = ReadBoolean(configuration, "EnableSMB2Protocol");
        var authenticated = ReadBoolean(configuration, "EnableAuthenticateUserSharing");
        var nullShares = ReadString(configuration, "NullSessionShares");
        var nullPipes = ReadString(configuration, "NullSessionPipes");
        var nullSessionsDisabled = string.IsNullOrWhiteSpace(nullShares) && string.IsNullOrWhiteSpace(nullPipes);
        var snapshot = Hash($"{smb1}\n{smb2}\n{authenticated}\n{nullShares}\n{nullPipes}");
        return new(snapshot, smb1, smb2, authenticated, nullSessionsDisabled, !smb1 && smb2 && authenticated && nullSessionsDisabled);
    }

    private static bool ReadBoolean(ManagementObject configuration, string property) => configuration[property] is bool value && value;
    private static string ReadString(ManagementObject configuration, string property) => configuration[property] as string ?? string.Empty;
    private static uint ReturnCode(ManagementBaseObject? result) => result?["ReturnValue"] is null ? 0 : Convert.ToUInt32(result["ReturnValue"]);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static PrivilegedOperationResult Output(SmbWindowsServerSecuritySnapshot snapshot) => new(true, OutputBase64: Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot)));
    private static PrivilegedOperationResult Fail(PrivilegedProblemCode code, string error) => new(false, 1, Error: error, ProblemCode: code);
}
