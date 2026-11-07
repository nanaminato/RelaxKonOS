using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>Holds an actual Mihomo response until the Server cancels its controller request.</summary>
internal sealed class MihomoObservationCancellationRelay : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly HttpClient upstream = new(new SocketsHttpHandler { UseProxy = false });
    private readonly Uri controller;
    private HeldResponse? next;
    internal string Address => app.Urls.Single() + "/";

    private MihomoObservationCancellationRelay(WebApplication app, Uri controller)
    {
        this.app = app;
        this.controller = controller;
        app.MapGet("/{**path}", ForwardAsync);
    }

    internal static async Task<MihomoObservationCancellationRelay> StartAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var relay = new MihomoObservationCancellationRelay(builder.Build(), new($"http://127.0.0.1:{port}/"));
        try { await relay.app.StartAsync(ct); return relay; }
        catch { await relay.DisposeAsync(); throw; }
    }

    internal HeldResponse HoldNext(string path)
    {
        var response = new HeldResponse(path);
        if (Interlocked.CompareExchange(ref next, response, null) is not null)
            throw new InvalidOperationException("A controller response is already armed.");
        return response;
    }

    private async Task ForwardAsync(HttpContext context)
    {
        var hold = Volatile.Read(ref next);
        if (hold is not null && (context.Request.Path != hold.Path || Interlocked.CompareExchange(ref next, null, hold) != hold))
            hold = null;
        var ct = context.RequestAborted;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(controller, context.Request.Path.Value + context.Request.QueryString.Value));
            request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToString());
            using var response = await upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (hold is not null)
            {
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                if (await reader.ReadLineAsync(ct) is not { Length: > 0 }) throw new InvalidOperationException("Mihomo returned no observation.");
                hold.Received.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return;
            }
            context.Response.StatusCode = (int)response.StatusCode;
            await context.Response.StartAsync(ct);
            await response.Content.CopyToAsync(context.Response.Body, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { if (hold is not null && ct.IsCancellationRequested) hold.Drained.TrySetResult(); }
    }

    internal sealed class HeldResponse(string path)
    {
        internal string Path { get; } = path;
        internal TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async ValueTask DisposeAsync()
    {
        try { await app.DisposeAsync(); }
        finally { upstream.Dispose(); }
    }
}
