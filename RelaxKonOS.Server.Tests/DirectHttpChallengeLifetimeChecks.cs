using System.Net;
using System.Net.Sockets;
using RelaxKonOS.Server.Certificate;

internal static class DirectHttpChallengeLifetimeChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS DIRECT HTTP LIFETIME {++count}: {text}"); }

        await using (var cancelled = new DirectHttp01ChallengeStore("not-an-http-prefix"))
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await cancelled.PutAsync("valid", "valid.key", cancellation.Token); throw new Exception("Cancelled Put accepted"); }
            catch (OperationCanceledException) { Check(true, "Precancelled Direct HTTP Put cannot create a listener or token"); }
            try { await cancelled.PutAsync("invalid/token", "valid.key", default); throw new Exception("Invalid token accepted"); }
            catch (CertificateOperationException error) when (error.ProblemCode == "certificate.challenge_invalid")
            { Check(true, "Challenge validation runs before listener construction"); }
        }

        var prefix = Prefix();
        await using (var store = new DirectHttp01ChallengeStore(prefix))
        {
            try { await store.PutAsync("first", "first.key", default); }
            catch (CertificateOperationException error) when (OperatingSystem.IsWindows() && error.ProblemCode == "certificate.port80_unavailable")
            {
                Console.WriteLine("SKIP DIRECT HTTP REAL LISTENER: Windows URL ACL or local port registration unavailable.");
                Console.WriteLine($"Direct HTTP challenge lifetime checks passed: {count}; real listener checks skipped");
                return;
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            Check(await http.GetStringAsync(prefix + "first") == "first.key", "Real Direct HTTP listener serves the exact active challenge bytes");
            await store.PutAsync("second", "second.key", default); await store.RemoveAsync("first", default);
            Check(await http.GetStringAsync(prefix + "second") == "second.key" && (await http.GetAsync(prefix + "first")).StatusCode == HttpStatusCode.NotFound,
                "Removing one token preserves another order's listener and withdraws only its own challenge");
            using var post = await http.PostAsync(prefix + "second", null);
            Check(post.StatusCode == HttpStatusCode.NotFound, "Direct HTTP challenge listener exposes only the GET token surface");
            await store.RemoveAsync("second", default).WaitAsync(TimeSpan.FromSeconds(5));
            await store.PutAsync("next", "next.key", default);
            Check(await http.GetStringAsync(prefix + "next") == "next.key" && (await http.GetAsync(prefix + "second")).StatusCode == HttpStatusCode.NotFound,
                "Completed last-token removal allows a new listener generation without reviving old tokens");
            await store.RemoveAsync("next", default).WaitAsync(TimeSpan.FromSeconds(5));
            await store.DisposeAsync();
            try { await store.PutAsync("closed", "closed.key", default); throw new Exception("Disposed store reopened"); }
            catch (ObjectDisposedException) { Check(true, "Async disposal closes admission instead of silently restarting a listener"); }
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        prefix = Prefix();
        await using (var store = new DirectHttp01ChallengeStore(prefix, async (_, _) =>
        { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; }))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await store.PutAsync("blocked", "blocked.key", default);
            var request = http.GetAsync(prefix + "blocked"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
            using var budget = new CancellationTokenSource();
            var remove = store.RemoveAsync("blocked", budget.Token);
            Check(!remove.IsCompleted, "Last-token removal waits for the actual non-cooperative response Task after listener close");
            var next = store.PutAsync("replacement", "replacement.key", default);
            Check(!next.IsCompleted && calls == 1, "A new Put waits for the old response generation rather than racing its listener close");
            budget.Cancel();
            try { await remove; throw new Exception("Remove wait budget ignored"); } catch (OperationCanceledException) { }
            Check(!next.IsCompleted && calls == 1, "Cancelling a Remove wait cannot detach its live response or permit premature replacement");
            release.TrySetResult(); await next.WaitAsync(TimeSpan.FromSeconds(5));
            await ObserveClosedRequest(request);
            await store.RemoveAsync("replacement", default).WaitAsync(TimeSpan.FromSeconds(5));
            Check(calls == 1, "Actual response completion drains the old generation once before a replacement is admitted");
            }
            finally { release.TrySetResult(); await ObserveClosedRequest(request); }
        }

        entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        prefix = Prefix();
        var disposingStore = new DirectHttp01ChallengeStore(prefix, async (_, _) => { entered.TrySetResult(); await release.Task; });
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            await disposingStore.PutAsync("disposing", "disposing.key", default);
            var request = http.GetAsync(prefix + "disposing"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
            disposingStore.Dispose(); var disposal = disposingStore.DisposeAsync().AsTask();
            Check(!disposal.IsCompleted, "Synchronous disposal only begins close and async disposal retains the actual response drain");
            release.TrySetResult(); await disposal.WaitAsync(TimeSpan.FromSeconds(5)); await ObserveClosedRequest(request);
            Check(disposal.IsCompletedSuccessfully, "Async disposal completes only after the response Task has actually ended");
            }
            finally { release.TrySetResult(); await ObserveClosedRequest(request); await disposingStore.DisposeAsync(); }
        }

        var failure = new InvalidOperationException("controlled response failure");
        prefix = Prefix(); calls = 0;
        var failedStore = new DirectHttp01ChallengeStore(prefix, (_, _) => { Interlocked.Increment(ref calls); return Task.FromException(failure); });
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            await failedStore.PutAsync("failing", "failing.key", default); await ObserveClosedRequest(http.GetAsync(prefix + "failing"));
            try { await failedStore.RemoveAsync("failing", default); throw new Exception("Response failure hidden"); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure))
            { Check(true, "Unexpected serving failure remains visible to challenge cleanup"); }
            try { await failedStore.PutAsync("retry", "retry.key", default); throw new Exception("Failed generation replayed"); }
            catch (CertificateOperationException error) when (error.ProblemCode == "certificate.port80_unavailable")
            { Check(calls == 1, "A faulted challenge generation rejects replacement instead of replaying its serving work"); }
            try { await failedStore.DisposeAsync(); throw new Exception("Disposal hid failure"); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure))
            { Check(calls == 1, "Async disposal reports the retained failure without a second response or cleanup attempt"); }
        }
        Console.WriteLine($"Direct HTTP challenge lifetime checks passed: {count}");
    }

    private static string Prefix()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        return $"http://localhost:{port}/.well-known/acme-challenge/";
    }
    private static async Task ObserveClosedRequest(Task<HttpResponseMessage> request)
    {
        try { using var response = await request.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }
}
