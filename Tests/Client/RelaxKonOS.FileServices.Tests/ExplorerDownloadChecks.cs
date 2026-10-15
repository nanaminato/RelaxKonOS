using System.Net;
using System.Reflection;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Identity;

public static class ExplorerDownloadChecks
{
    public static async Task RunAsync()
    {
        var content = new StreamingContent();
        using var http = new HttpClient(new ResponseHandler(content));
        var session = DispatchProxy.Create<IAuthSession, DownloadSession>();
        var client = new ExplorerClient(http, session, null!);
        var result = await client.DownloadAsync("/sample.bin");
        if (result is null || content.Buffered) throw new Exception("Download must return after headers without buffering the response body.");
        var bytes = new byte[3];
        if (await result.Value.Stream.ReadAsync(bytes) != 3 || !bytes.SequenceEqual(new byte[] { 1, 2, 3 }))
            throw new Exception("Download must expose the original response stream.");
        await result.Value.Stream.DisposeAsync();
        if (!content.Disposed) throw new Exception("Disposing the download must release its HTTP response.");
        Console.WriteLine("PASS: Explorer HTTP download streams without whole-body buffering and releases the response.");
    }
    private sealed class ResponseHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
    private sealed class StreamingContent : HttpContent
    {
        public bool Buffered, Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Buffered = true; throw new InvalidOperationException("The HTTP body must not be eagerly buffered."); }
        protected override bool TryComputeLength(out long length) { length = 3; return true; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
public class DownloadSession : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_State" => AuthSessionState.Authenticated,
        "get_EffectiveBaseUrl" => "https://clipboard-test.invalid",
        "get_Tokens" => new AuthTokens("test-token", "test-refresh", DateTimeOffset.MaxValue, DateTimeOffset.MaxValue),
        _ => throw new NotSupportedException(method.Name),
    };
}
