using System.Collections;
using System.Text;
using RelaxKonOS.Server.Terminal;

internal static class WindowsTerminalChecks
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string)entry.Value!;
        var marker = "terminal_" + Guid.NewGuid().ToString("N");
        using var pty = new ConPty();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        pty.DataReceived += (bytes, count) =>
        {
            output.Append(Encoding.UTF8.GetString(bytes, 0, count));
            if (output.ToString().Contains(marker)) received.TrySetResult();
        };
        pty.ProcessExited += _ => exited.TrySetResult();
        pty.Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), 80, 24,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), environment, ["/Q"]);
        pty.Resize(100, 30);
        // Construct the marker in the shell so echoed input cannot satisfy the output assertion.
        var half = marker.Length / 2;
        pty.Write($"set a={marker[..half]}\r\nset b={marker[half..]}\r\necho %a%%b%\r\n");
        await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        pty.Write("exit\r\n");
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
        TestAssert.Assert(!pty.IsRunning, "Windows terminal remained running after shell exit.");
        using var failed = new ConPty();
        try
        {
            failed.Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), 80, 24,
                null, environment, null);
            throw new Exception("Missing shell unexpectedly started.");
        }
        catch (System.ComponentModel.Win32Exception) { }
        TestAssert.Assert(!failed.IsRunning, "Failed Windows terminal left a running session.");
        using var powershell = new ConPty();
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var powershellOutput = new StringBuilder();
        var powershellMarker = "powershell_" + Guid.NewGuid().ToString("N");
        powershell.DataReceived += (bytes, count) =>
        {
            powershellOutput.Append(Encoding.UTF8.GetString(bytes, 0, count));
            if (powershellOutput.ToString().Contains(powershellMarker)) initialized.TrySetResult();
        };
        // Personal administrator terminals pass no environment. Exercise managed PowerShell
        // initialization and inherited SystemRoot without letting echoed input satisfy the check.
        powershell.Start(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            80, 24, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                $"if ($env:SystemRoot -eq '{Environment.GetEnvironmentVariable("SystemRoot")?.Replace("'", "''")}') {{ [Console]::WriteLine('{powershellMarker}') }} else {{ exit 2 }}"]);
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("Windows terminal input/output, resize, exit and startup cleanup checks passed.");
    }
}
