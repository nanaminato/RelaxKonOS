using System.Diagnostics;
using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Server.ProcessGuardian;

/// <summary>Explicit systemd/SCM facade. Native services remain native services, not Guardian workloads.</summary>
public interface INativeServiceAdapter
{
    Task<IReadOnlyList<NativeServiceDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<GuardianOperationResult> ApplyActionAsync(string id, string action, NativeServiceActionRequest request, CancellationToken cancellationToken = default);
}

public sealed class NativeServiceAdapter(NativeServiceAdapterOptions options, IPrivilegedNativeServiceOperations privileged) : INativeServiceAdapter
{
    public async Task<IReadOnlyList<NativeServiceDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<NativeServiceDto>();
        var names = OperatingSystem.IsWindows()
            ? options.AllowedServiceNames.Append(NativeServiceTargets.IisWebPublishing)
            : options.AllowedServiceNames;
        foreach (var id in names.Where(IsServiceName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var output = OperatingSystem.IsWindows()
                ? await RunAsync("sc.exe", ["query", id], cancellationToken)
                : await RunAsync("systemctl", ["show", id, "--property=Description,ActiveState,UnitFileState"], cancellationToken);
            items.Add(OperatingSystem.IsWindows() ? ParseWindows(id, output) : ParseSystemd(id, output));
        }
        return items;
    }

    public async Task<GuardianOperationResult> ApplyActionAsync(string id, string action, NativeServiceActionRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Confirmed) return new GuardianOperationResult(false, "guardian.confirmation_required");
        if (!IsServiceName(id) || (!options.AllowedServiceNames.Contains(id, StringComparer.OrdinalIgnoreCase) && !IsIisStop(id, action)) || action is not ("start" or "stop" or "restart"))
            return new GuardianOperationResult(false, "guardian.validation_failed");
        var privilegedAction = action switch
        {
            "start" => RelaxKonOS.Protocol.Privileged.PrivilegedServiceAction.Start,
            "stop" => RelaxKonOS.Protocol.Privileged.PrivilegedServiceAction.Stop,
            "restart" => RelaxKonOS.Protocol.Privileged.PrivilegedServiceAction.Restart,
            _ => throw new InvalidOperationException("Validated service action was not mapped."),
        };
        var result = await privileged.ApplyAsync(id, privilegedAction, cancellationToken);
        return new GuardianOperationResult(result.Success, result.Success ? string.Empty
            : result.ProblemCode == RelaxKonOS.Protocol.Privileged.PrivilegedProblemCode.HelperUnavailable
                ? "guardian.privileged_helper_unavailable"
                : "guardian.service_action_failed");
    }

    private static async Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return "!start";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 ? await output : "!failed";
        }
        catch (Exception) { return "!unavailable"; }
    }

    private static NativeServiceDto ParseSystemd(string id, string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? Value(string key) => lines.FirstOrDefault(line => line.StartsWith(key, StringComparison.Ordinal))?[key.Length..];
        return new NativeServiceDto(id, Value("Description=") ?? id, Value("ActiveState=") ?? "unknown",
            Value("UnitFileState=") ?? "unknown", "systemd");
    }

    private static NativeServiceDto ParseWindows(string id, string output)
    {
        var status = output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ? "running" : output.StartsWith("!", StringComparison.Ordinal) ? "unknown" : "stopped";
        return new NativeServiceDto(id, id, status, "scm-managed", "scm");
    }

    internal static bool IsIisStop(string id, string action) => OperatingSystem.IsWindows() && action == "stop"
        && string.Equals(id, NativeServiceTargets.IisWebPublishing, StringComparison.OrdinalIgnoreCase);

    private static bool IsServiceName(string value) => value.Length is > 0 and <= 256 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@');
}

public sealed class NativeServiceAdapterOptions
{
    /// <summary>Only explicitly approved native units/services can be viewed or controlled.</summary>
    public IReadOnlyList<string> AllowedServiceNames { get; init; } = [];
}
