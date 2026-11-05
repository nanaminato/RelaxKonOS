using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.Server.Terminal;

internal static class TerminalOutputStream
{
    internal static async Task<int> ReadAsync(Stream stream, Action<byte[], int> output)
    {
        while (await UserTerminalStreamProtocol.ReadAsync(stream) is { } frame)
        {
            if (frame.Kind == UserTerminalFrameKind.Output) output(frame.Input!, frame.Input!.Length);
            else if (frame.Kind == UserTerminalFrameKind.Exited) return frame.ExitCode;
            else if (frame.Kind == UserTerminalFrameKind.TreeFaulted) throw new IOException("terminal.process_tree_cleanup_unknown");
            else throw new InvalidDataException("Unexpected Helper terminal response frame.");
        }
        throw new IOException("terminal.exit_confirmation_missing");
    }
}
