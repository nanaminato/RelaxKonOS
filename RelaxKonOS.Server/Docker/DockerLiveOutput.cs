using System.Text;

namespace RelaxKonOS.Server.Docker;

/// <summary>Handles both progress carriage returns and lines, without buffering an entire build.</summary>
internal static class DockerLiveOutput
{
    public static async Task<string> ReadAsync(TextReader reader, Action<string> onOutput)
    {
        var tail = new Queue<string>();
        var line = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n') Flush();
                else if (line.Length < 4096) line.Append(c);
            }
        }
        Flush();
        return string.Join('\n', tail);

        void Flush()
        {
            if (line.Length == 0) return;
            var text = line.ToString();
            line.Clear();
            tail.Enqueue(text);
            if (tail.Count > 120) tail.Dequeue();
            onOutput(text);
        }
    }
}
