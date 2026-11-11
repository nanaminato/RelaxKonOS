using System.Diagnostics;
using System.Reflection;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Terminal;

internal static class TerminalControlInputChecks
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        foreach (var partial in new[] { false, true })
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            info.ArgumentList.Add("--terminal-control-input-worker"); info.ArgumentList.Add(partial ? "partial" : "empty");
            using var child = Process.Start(info)!;
            var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
            try
            {
                if (partial)
                {
                    // A valid Input header promises four bytes, but exactly one is supplied.
                    // Keep stdin open so only cancellation, never EOF, can end the owned read.
                    await child.StandardInput.BaseStream.WriteAsync(new byte[] { 1, 0, 0, 0, 4, 42 });
                    await child.StandardInput.BaseStream.FlushAsync();
                }
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var text = await output; var error = await errors;
                if (child.ExitCode != 0 || !text.Contains("ACTIVE_NATIVE_READS=0", StringComparison.Ordinal))
                    throw new IOException($"Native stdin cancellation failed ({partial}): {text} {error}");
                Console.Write(text);
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        }
        Console.WriteLine("Terminal control input checks passed: 2");
    }
    internal static async Task RunWorkerAsync(bool partial)
    {
        using var input = new LinuxTerminalControlInput();
        using var cancellation = new CancellationTokenSource();
        using var counted = new CountingStream(input, partial ? 6 : int.MaxValue, cancellation);
        if (!partial) cancellation.CancelAfter(100);
        var read = UserTerminalStreamProtocol.ReadAsync(counted, cancellation.Token).AsTask();
        try { await read; throw new Exception("An incomplete live pipe frame unexpectedly completed."); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        if (!read.IsCompleted || input.ActiveReads != 0 || partial && counted.BytesRead != 6)
            throw new Exception("Cancellation returned while an actual native frame read remained owned.");
        Console.WriteLine($"PASS TERMINAL CONTROL INPUT {(partial ? "partial-payload" : "empty-header")}: actual open-pipe frame cancellation drained; ACTIVE_NATIVE_READS={input.ActiveReads}");
    }
    private sealed class CountingStream(Stream inner, int threshold, CancellationTokenSource cancellation) : Stream
    {
        internal int BytesRead;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var count = await inner.ReadAsync(buffer, ct);
            BytesRead += count;
            if (BytesRead == threshold) cancellation.CancelAfter(100);
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
