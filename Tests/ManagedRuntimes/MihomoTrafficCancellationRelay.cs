using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>Holds one actual Mihomo traffic sample until the Server cancels its controller request.</summary>
internal sealed class MihomoTrafficCancellationRelay : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly HttpClient upstream = new(new SocketsHttpHandler { UseProxy = false });
    private readonly Uri controller;
    private int armed;
    internal TaskCompletionSource SampleHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource RequestDrained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Address => app.Urls.Single() + "/";

    private MihomoTrafficCancellationRelay(WebApplication app, Uri controller)
    {
        this.app = app;
        this.controller = controller;
        app.MapGet("/{**path}", ForwardAsync);
    }

    internal static async Task<MihomoTrafficCancellationRelay> StartAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var relay = new MihomoTrafficCancellationRelay(builder.Build(), new($"http://127.0.0.1:{port}/"));
        try { await relay.app.StartAsync(ct); return relay; }
        catch { await relay.DisposeAsync(); throw; }
    }

    internal void HoldNextTrafficSample() => Volatile.Write(ref armed, 1);

    private async Task ForwardAsync(HttpContext context)
    {
        var hold = context.Request.Path == "/traffic" && Interlocked.Exchange(ref armed, 0) == 1;
        var ct = context.RequestAborted;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(controller, context.Request.Path.Value + context.Request.QueryString.Value));
            request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToString());
            using var response = await upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (hold)
            {
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                if (await reader.ReadLineAsync(ct) is not { Length: > 0 }) throw new InvalidOperationException("Mihomo returned no traffic sample.");
                SampleHeld.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return;
            }
            context.Response.StatusCode = (int)response.StatusCode;
            await context.Response.StartAsync(ct);
            await response.Content.CopyToAsync(context.Response.Body, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { if (hold && ct.IsCancellationRequested) RequestDrained.TrySetResult(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await app.DisposeAsync(); }
        finally { upstream.Dispose(); }
    }
}
