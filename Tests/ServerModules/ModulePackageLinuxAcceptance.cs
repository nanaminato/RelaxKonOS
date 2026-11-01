extern alias modulehelper;

using System.Diagnostics;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;

/// <summary>
/// Real Linux acceptance for the module package adapter. It runs only as root on Linux and is expected to
/// execute inside a disposable Ubuntu container: it writes the Host-owned source policy, resolves a real
/// package through apt, installs it, replays the operation, proves drift and source revocation refusal,
/// then removes it. The Windows host this repository is usually edited on skips it explicitly.
/// </summary>
internal static class ModulePackageLinuxAcceptance
{
    private const string PolicyDirectory = "/etc/relaxkonos";
    private const string PolicyPath = "/etc/relaxkonos/module-packages.json";

    internal static async Task RunAsync(string[] arguments, Action<bool, string> check)
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
        {
            Console.WriteLine("SKIP: module package Linux acceptance requires root on Linux (use a disposable Ubuntu container).");
            return;
        }
        foreach (var binary in new[] { "/usr/bin/apt-get", "/usr/bin/apt-cache", "/usr/bin/dpkg-query" })
            if (!File.Exists(binary)) throw new InvalidOperationException("module package acceptance requires " + binary);
        var sourceRef = Single(arguments, "--source-ref");
        var baseUris = Many(arguments, "--base-uri");
        var packages = Many(arguments, "--package");
        if (sourceRef is null || baseUris.Length == 0 || packages.Length == 0)
            throw new ArgumentException("--source-ref, at least one --base-uri and at least one --package are required.");
        // The operator names the approved base explicitly: the Helper must never trust a URL a module supplied.
        WriteSourcePolicy(sourceRef, baseUris, packages);
        check(true, "root-owned module package source policy is written for the acceptance source");
        try
        {
            foreach (var package in packages)
            {
                // Each iteration owns the policy file, so a revocation rewrite never leaks into the next package.
                WriteSourcePolicy(sourceRef, baseUris, packages);
                await AcceptAsync(sourceRef, baseUris, packages, package, check);
            }
        }
        finally
        {
            File.Delete(PolicyPath);
        }
    }

    private static async Task AcceptAsync(string sourceRef, string[] baseUris, string[] packages, string package,
        Action<bool, string> check)
    {
        var resolved = await ExecuteAsync(PrivilegedOperationKind.ModulePackageQuery,
            ModulePackageQuery: new(sourceRef, package));
        check(resolved.Success && resolved.ModulePackage is { } resolution && resolution.Package == package,
            $"Helper resolves the declared {package} to one exact artifact from the approved source");
        var target = resolved.ModulePackage!;
        check(target.Sha256.Length == 64 && target.Bytes > 0 && target.Dependencies.All(item => item.Sha256.Length == 64),
            $"{package} resolution freezes a SHA-256 artifact identity with {target.Dependencies.Count} dependencies");

        var installId = Guid.NewGuid();
        var installed = await ExecuteAsync(PrivilegedOperationKind.ModulePackageApply, installId,
            ModulePackageApply: new(ModulePackageTransactionAction.Install, sourceRef, target));
        check(installed.Success && Installed(package, target.Version, target.Architecture),
            $"real apt install applies exactly {package} {target.Version} {target.Architecture}");

        var replay = await ExecuteAsync(PrivilegedOperationKind.ModulePackageApply, installId,
            ModulePackageApply: new(ModulePackageTransactionAction.Install, sourceRef, target));
        check(!replay.Success && replay.ProblemCode == PrivilegedProblemCode.Conflict,
            "reusing a completed operation id is refused as recovery, never replayed");

        var drift = await ExecuteAsync(PrivilegedOperationKind.ModulePackageApply,
            ModulePackageApply: new(ModulePackageTransactionAction.Install, sourceRef,
                target with { Sha256 = new string('0', 64) }));
        check(!drift.Success && drift.ProblemCode == PrivilegedProblemCode.PlanChanged,
            "a frozen identity whose digest moved is refused as a plan change before apt runs");

        WriteSourcePolicy("revoked-" + sourceRef, ["http://invalid.example/"], [package]);
        var revoked = await ExecuteAsync(PrivilegedOperationKind.ModulePackageApply,
            ModulePackageApply: new(ModulePackageTransactionAction.Remove, sourceRef, target));
        check(!revoked.Success && revoked.ProblemCode == PrivilegedProblemCode.PlanChanged,
            "a source removed from the protected policy invalidates the old plan instead of executing it");
        WriteSourcePolicy(sourceRef, baseUris, packages);

        var removed = await ExecuteAsync(PrivilegedOperationKind.ModulePackageApply,
            ModulePackageApply: new(ModulePackageTransactionAction.Remove, sourceRef, target));
        check(removed.Success && !Installed(package, target.Version, target.Architecture),
            $"real apt removal leaves no installed {package}");
    }

    private static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationKind operation,
        Guid? operationId = null, ModulePackageQuery? ModulePackageQuery = null,
        ModulePackageTransaction? ModulePackageApply = null)
    {
        var id = operationId ?? Guid.NewGuid();
        return await modulehelper::PrivilegedOperationExecutor.ExecuteAsync(new PrivilegedOperationRequest(operation,
            OperationId: id, Correlation: CorrelationContext.Create(id),
            ModulePackageQuery: ModulePackageQuery, ModulePackageApply: ModulePackageApply),
            new modulehelper::PrivilegedOperationPolicy([], [], [], []));
    }

    private static bool Installed(string package, string version, string architecture)
    {
        var start = new ProcessStartInfo("/usr/bin/dpkg-query")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-W");
        start.ArgumentList.Add("-f=${Status}\t${Version}\t${Architecture}\n");
        start.ArgumentList.Add(package);
        using var process = Process.Start(start);
        if (process is null) return false;
        var text = process.StandardOutput.ReadToEnd().TrimEnd('\r', '\n');
        process.WaitForExit();
        var fields = text.Split('\t');
        return process.ExitCode == 0 && fields.Length == 3 && fields[0] == "install ok installed"
            && fields[1] == version && fields[2] == architecture;
    }

    private static void WriteSourcePolicy(string sourceRef, string[] baseUris, string[] packages)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The module package policy is Linux-only.");
        Directory.CreateDirectory(PolicyDirectory);
        File.SetUnixFileMode(PolicyDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = PolicyPath + ".acceptance";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(
            new modulehelper::ModulePackageSourcePolicy(1, [new modulehelper::ModulePackageSourceEntry(sourceRef, baseUris, packages)]),
            RelaxKonOSJsonOptions.Default));
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temporary, PolicyPath, true);
    }

    private static string? Single(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }
    private static string[] Many(string[] arguments, string name) => arguments
        .Select((value, index) => (value, index))
        .Where(item => item.value == name && item.index + 1 < arguments.Length)
        .Select(item => arguments[item.index + 1]).ToArray();
}

