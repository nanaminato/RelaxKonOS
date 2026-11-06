using System.IO.Pipes;
using RelaxKonOS.Protocol.UserExecution;
internal static class TerminalStreamFramesChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine($"PASS TERMINAL FRAMES {++count}: {text}"); }
        using var stream = new MemoryStream(); var bytes = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
        UserTerminalStreamProtocol.WriteOutput(stream, bytes); UserTerminalStreamProtocol.WriteExited(stream, -17); stream.Position = 0;
        var output = await UserTerminalStreamProtocol.ReadAsync(stream); var exited = await UserTerminalStreamProtocol.ReadAsync(stream);
        Check(output is { Kind: UserTerminalFrameKind.Output } && output.Input!.SequenceEqual(bytes), "Current output frames preserve arbitrary VT bytes at the 64KiB frame bound");
        Check(exited is { Kind: UserTerminalFrameKind.Exited, ExitCode: -17 } && await UserTerminalStreamProtocol.ReadAsync(stream) is null, "Explicit signed exit confirmation follows output before clean EOF");
        using var asyncOutput = new MemoryStream();
        await UserTerminalStreamProtocol.WriteOutputAsync(asyncOutput, bytes, CancellationToken.None);
        await UserTerminalStreamProtocol.WriteExitedAsync(asyncOutput, 7, CancellationToken.None);
        asyncOutput.Position = 0;
        var asyncFrame = await UserTerminalStreamProtocol.ReadAsync(asyncOutput);
        Check(asyncFrame is { Kind: UserTerminalFrameKind.Output } && asyncFrame.Input!.SequenceEqual(bytes)
            && await UserTerminalStreamProtocol.ReadAsync(asyncOutput) is { Kind: UserTerminalFrameKind.Exited, ExitCode: 7 },
            "Cancellable Helper output and exit use the same current wire frames");
        await CheckStalledPipeAsync(bytes);
        Check(true, "Cancellation completes an actual blocked output write to an open named pipe");
        try { UserTerminalStreamProtocol.WriteOutput(stream, new byte[65537]); throw new Exception("Oversized output accepted"); } catch (ArgumentOutOfRangeException) { }
        Check(true, "Oversized output is refused before frame construction");
        using var truncated = new MemoryStream(new byte[] { 4, 0, 0, 0, 2, 1 });
        try { await UserTerminalStreamProtocol.ReadAsync(truncated); throw new Exception("Truncated output accepted"); } catch (EndOfStreamException) { }
        Check(true, "Truncated framed output cannot be mistaken for successful process completion");
        using var raw = new MemoryStream("legacy raw output"u8.ToArray());
        try { await UserTerminalStreamProtocol.ReadAsync(raw); throw new Exception("Legacy raw output accepted"); } catch (InvalidDataException) { }
        Check(UserExecutionProtocol.Version == "1.11", "Current 1.11 protocol rejects old unframed output without dual-format parsing");
        stream.Position = 0; byte[]? forwarded = null;
        var code = await RelaxKonOS.Server.Terminal.TerminalOutputStream.ReadAsync(stream, (payload, _) => forwarded = payload);
        Check(code == -17 && forwarded!.SequenceEqual(bytes), "The actual Helper response reader forwards output before accepting explicit cleanup confirmation");
        using var missingExit = new MemoryStream(); UserTerminalStreamProtocol.WriteOutput(missingExit, [1]); missingExit.Position = 0;
        try { await RelaxKonOS.Server.Terminal.TerminalOutputStream.ReadAsync(missingExit, (_, _) => { }); throw new Exception("EOF accepted as cleanup confirmation"); } catch (IOException) { }
        Check(true, "Helper EOF without an Exited frame cannot confirm cleanup even after valid output");
        using var wrongDirection = new MemoryStream(); UserTerminalStreamProtocol.WriteClose(wrongDirection); wrongDirection.Position = 0;
        try { await RelaxKonOS.Server.Terminal.TerminalOutputStream.ReadAsync(wrongDirection, (_, _) => { }); throw new Exception("Control frame accepted as output"); } catch (InvalidDataException) { }
        Check(true, "The actual response reader refuses input-direction control frames");
        using var fault = new MemoryStream(); UserTerminalStreamProtocol.WriteTreeFaulted(fault); fault.Position = 0;
        try { await RelaxKonOS.Server.Terminal.TerminalOutputStream.ReadAsync(fault, (_, _) => { }); throw new Exception("Tree fault accepted as exit"); }
        catch (IOException error) when (error.Message == "terminal.process_tree_cleanup_unknown") { }
        Check(true, "Explicit tree failure reports unknown cleanup and cannot confirm exit");
        Console.WriteLine($"Terminal frame checks passed: {count}");
    }

    private static async Task CheckStalledPipeAsync(byte[] bytes)
    {
        var name = "rk-terminal-frame-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 4096);
        using var peer = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        await Task.WhenAll(pipe.WaitForConnectionAsync(), peer.ConnectAsync(5000));
        using var abort = new CancellationTokenSource();
        var blocked = FloodAsync();
        if (blocked.IsCompleted) throw new IOException("The nonreading pipe did not stall output.");
        abort.Cancel();
        try
        {
            await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            throw new IOException("The stalled write completed without observing cancellation.");
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { }

        async Task FloodAsync()
        {
            for (var i = 0; i < 256; i++) await UserTerminalStreamProtocol.WriteOutputAsync(pipe, bytes, abort.Token);
        }
    }
}
