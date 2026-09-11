using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Fixed binding to the Windows SMB Server WMI provider. This is deliberately not a general WMI
/// surface: namespace, class, methods, and writable properties are all compile-time constants.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSmbServerSecurity
{
    private const string ScopePath = @"\\.\ROOT\Microsoft\Windows\Smb";
    private const string ClassName = "MSFT_SmbServerConfiguration";

    public static PrivilegedOperationResult Read()
    {
        try
        {
            using var configurationClass = OpenConfigurationClass();
            using var configuration = GetConfiguration(configurationClass);
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
            using var configurationClass = OpenConfigurationClass();
            using var configuration = GetConfiguration(configurationClass);
            var before = Snapshot(configuration);
            if (!string.IsNullOrWhiteSpace(expectedSnapshot) && !string.Equals(before.SnapshotHash, expectedSnapshot, StringComparison.Ordinal))
                return Fail(PrivilegedProblemCode.Conflict, "Windows SMB Server security changed externally");

            if (!before.Compliant)
            {
                using var parameters = configurationClass.GetMethodParameters("SetConfiguration");
                CopyCurrentMethodValues(configuration, parameters);
                // These are the only five global SMB server values V1 may mutate. No caller can
                // supply a WMI class, method, property name, or value.
                parameters["EnableSMB1Protocol"] = false;
                parameters["EnableSMB2Protocol"] = true;
                parameters["EnableAuthenticateUserSharing"] = true;
                parameters["NullSessionShares"] = Array.Empty<string>();
                parameters["NullSessionPipes"] = Array.Empty<string>();
                var result = configurationClass.InvokeMethod("SetConfiguration", parameters, null);
                if (ReturnCode(result) != 0) return Fail(PrivilegedProblemCode.InternalError, "Windows SMB Server security apply failed");
            }

            using var verified = GetConfiguration(configurationClass);
            var after = Snapshot(verified);
            return after.Compliant ? Output(after) : Fail(PrivilegedProblemCode.InternalError, "Windows SMB Server security verification failed");
        }
        catch (ManagementException) { return Fail(PrivilegedProblemCode.NotFound, "Windows SMB Server configuration API is unavailable"); }
        catch (UnauthorizedAccessException) { return Fail(PrivilegedProblemCode.AccessDenied, "Windows SMB Server configuration access was denied"); }
        catch (InvalidOperationException) { return Fail(PrivilegedProblemCode.NotFound, "Windows SMB Server configuration API is unavailable"); }
    }

    private static ManagementClass OpenConfigurationClass()
    {
        var scope = new ManagementScope(ScopePath);
        scope.Connect();
        return new ManagementClass(scope, new ManagementPath(ClassName), null);
    }

    private static ManagementBaseObject GetConfiguration(ManagementClass configurationClass)
    {
        // MSFT_SmbServerConfiguration is a static provider class: it exposes no enumerable
        // instances. Its sole configuration object is returned from GetConfiguration. Querying
        // the class (as if it had instances) always yields no rows and falsely reports SMB as
        // not installed even when the File Server role and LanmanServer are running.
        using var parameters = configurationClass.GetMethodParameters("GetConfiguration");
        using var result = configurationClass.InvokeMethod("GetConfiguration", parameters, null);
        if (ReturnCode(result) != 0 || result?["Output"] is not ManagementBaseObject configuration)
            throw new InvalidOperationException("SMB server configuration was not found");
        return configuration;
    }

    private static void CopyCurrentMethodValues(ManagementBaseObject configuration, ManagementBaseObject parameters)
    {
        foreach (PropertyData parameter in parameters.Properties)
        {
            try { parameters[parameter.Name] = configuration[parameter.Name]; }
            catch (ManagementException) { /* Output-only method properties are intentionally untouched. */ }
        }
    }

    private static SmbWindowsServerSecuritySnapshot Snapshot(ManagementBaseObject configuration)
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

    private static bool ReadBoolean(ManagementBaseObject configuration, string property) => configuration[property] is bool value && value;
    private static string ReadString(ManagementBaseObject configuration, string property) => configuration[property] switch
    {
        string value => value,
        string[] values => string.Join('\n', values.OrderBy(value => value, StringComparer.Ordinal)),
        Array values => string.Join('\n', values.Cast<object?>().Select(value => value?.ToString() ?? string.Empty).OrderBy(value => value, StringComparer.Ordinal)),
        _ => string.Empty
    };
    private static uint ReturnCode(ManagementBaseObject? result) => result?["ReturnValue"] is null ? 0 : Convert.ToUInt32(result["ReturnValue"]);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static PrivilegedOperationResult Output(SmbWindowsServerSecuritySnapshot snapshot) => new(true, OutputBase64: Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot)));
    private static PrivilegedOperationResult Fail(PrivilegedProblemCode code, string error) => new(false, 1, Error: error, ProblemCode: code);
}
