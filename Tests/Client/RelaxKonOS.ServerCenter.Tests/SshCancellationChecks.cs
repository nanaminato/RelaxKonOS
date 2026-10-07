using RelaxKonOS.Client.Services.ServerCenter;

internal static class SshCancellationChecks
{
    public static async Task RunAsync()
    {
        await using var transport = new SshNetServerCenterTransport();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var source = new MemoryStream(new byte[100]);
        using var destination = new MemoryStream();
        foreach (var operation in new Func<Task>[]
        {
            () => transport.RunAsync("test-command", cancellation.Token),
            () => transport.RunWithInputAsync("test-command", "test-input", cancellation.Token),
            () => transport.UploadAsync(source, "/test-upload", null, cancellation.Token),
            () => transport.DownloadAsync("/test-download", destination, cancellation.Token)
        })
        {
            try { await operation(); throw new InvalidOperationException("Canceled transport operation started unexpectedly."); }
            catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token) { }
        }
        if (source.Position != 0 || destination.Length != 0)
            throw new InvalidOperationException("Canceled transfer accessed its stream.");
        Console.WriteLine("PASS: Pre-canceled SSH commands and SFTP transfers preserve cancellation and do not access streams.");
    }
}
