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
        try { UserTerminalStreamProtocol.WriteOutput(stream, new byte[65537]); throw new Exception("Oversized output accepted"); } catch (ArgumentOutOfRangeException) { }
        Check(true, "Oversized output is refused before frame construction");
        using var truncated = new MemoryStream(new byte[] { 4, 0, 0, 0, 2, 1 });
        try { await UserTerminalStreamProtocol.ReadAsync(truncated); throw new Exception("Truncated output accepted"); } catch (EndOfStreamException) { }
        Check(true, "Truncated framed output cannot be mistaken for successful process completion");
        using var raw = new MemoryStream("legacy raw output"u8.ToArray());
        try { await UserTerminalStreamProtocol.ReadAsync(raw); throw new Exception("Legacy raw output accepted"); } catch (InvalidDataException) { }
        Check(UserExecutionProtocol.Version == "1.10", "Current 1.10 protocol rejects old unframed output without dual-format parsing");
        stream.Position = 0; byte[]? forwarded = null;
        var code = await RelaxKonOS.Server.Terminal.TerminalOutputStream.ReadAsync(stream, (payload, _) => forwarded = payload);
        Check(code == -17 && forwarded!.SequenceEqual(bytes), "The actual Helper response reader forwards output before accepting explicit cleanup confirmation");
        using var missingExit = new MemoryStream(); UserTerminalStreamProtocol.WriteOutput(missingExit, [1]); missingExit.Position = 0;
        try { await RelaxKonOS.Server.Terminal.TerminalOutputStream.ReadAsync(missingExit, (_, _) => { }); throw new Exception("EOF accepted as cleanup confirmation"); } catch (IOException) { }
        Check(true, "Helper EOF without an Exited frame cannot confirm cleanup even after valid output");
        using var wrongDirection = new MemoryStream(); UserTerminalStreamProtocol.WriteClose(wrongDirection); wrongDirection.Position = 0;
        try { await RelaxKonOS.Server.Terminal.TerminalOutputStream.ReadAsync(wrongDirection, (_, _) => { }); throw new Exception("Control frame accepted as output"); } catch (InvalidDataException) { }
        Check(true, "The actual response reader refuses input-direction control frames");
        Console.WriteLine($"Terminal frame checks passed: {count}");
    }
}
