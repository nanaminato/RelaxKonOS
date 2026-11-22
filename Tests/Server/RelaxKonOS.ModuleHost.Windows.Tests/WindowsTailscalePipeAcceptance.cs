extern alias modulehelper;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Tailscale;
using modulehelper::RelaxKonOS.PrivilegedHelper;

internal static class WindowsTailscalePipeAcceptance
{
    private const string PipePrefix = "RelaxKonOS.Tailscale.Acceptance.";

    internal static Task RunAsync() => WithHelperAsync(async transport =>
    {
        var status = await new HelperTailscaleManager(transport).GetStatusAsync(default);
        Console.WriteLine($"Tailscale cross-process authenticated pipe status: {status.State}");
        if (status is not { State: "observed", BackendState.Length: > 0 })
            throw new InvalidOperationException("The production pipe did not return a live Tailscale backend state.");
    });

    internal static async Task WithHelperAsync(Func<IPrivilegedOperationTransport, Task> probe)
    {
        var pipe = PipePrefix + Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(WindowsTailscalePipeAcceptance).Assembly.Location);
        start.ArgumentList.Add("--tailscale-live-pipe-child");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Helper test host.");
        try
        {
            await child.StandardInput.WriteLineAsync(pipe);
            await child.StandardInput.WriteLineAsync(secret);
            await child.StandardInput.FlushAsync();
            if (await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) != "ready")
                throw new InvalidOperationException("The Helper test process did not become ready.");

            var transport = new WindowsNamedPipePrivilegedOperationTransport(new PrivilegedHelperOptions
            { PipeName = pipe, SharedSecret = secret }, NullLogger<WindowsNamedPipePrivilegedOperationTransport>.Instance);
            await probe(transport);
        }
        finally
        {
            child.StandardInput.Close();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                throw;
            }
        }
        if (child.ExitCode != 0) throw new InvalidOperationException("The Helper test process failed during shutdown.");
    }

    internal static async Task RunHelperAsync()
    {
        var pipe = await Console.In.ReadLineAsync();
        var secret = await Console.In.ReadLineAsync();
        if (pipe is null || !pipe.StartsWith(PipePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(pipe[PipePrefix.Length..], "N", out _)
            || secret is null || Convert.FromBase64String(secret).Length != 32)
            throw new InvalidDataException("The Helper test pipe configuration is invalid.");
        using var caller = WindowsIdentity.GetCurrent();
        var sid = caller.User?.Value ?? throw new InvalidOperationException("The test caller SID is required.");
        await using var helper = new WindowsPrivilegedPipeServer(new(pipe, secret, [], [], sid),
            error => Console.Error.WriteLine($"Helper pipe failure: {error.GetType().Name}"));
        helper.Start();
        Console.WriteLine("ready");
        await Console.In.ReadLineAsync();
    }
}
