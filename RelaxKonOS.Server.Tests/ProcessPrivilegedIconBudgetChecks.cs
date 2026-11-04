using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.SystemPerformance;

internal static class ProcessPrivilegedIconBudgetChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS PRIVILEGED ICON BUDGET {++count}: {text}"); }
        await using (var f = new Fixture())
        {
            var bytes = Enumerable.Repeat((byte)0x5a, ProcessSampler.MaximumPrivilegedIconBytes).ToArray();
            var encoded = Convert.ToBase64String(bytes);
            var png = await f.Read(encoded);
            Check(png is { ContentType: "image/png" } && png.Data.SequenceEqual(bytes),
                "Administrator PNG at the decoded byte budget returns the exact Helper payload");
            var svg = await f.Read(encoded, "image/svg+xml");
            Check(svg is { ContentType: "image/svg+xml" } && svg.Data.SequenceEqual(bytes),
                "Administrator SVG uses the same encoded and decoded budget without changing its MIME");
            var oneOver = Convert.ToBase64String(new byte[bytes.Length + 1]);
            var twoOver = Convert.ToBase64String(new byte[bytes.Length + 2]);
            Check(oneOver.Length == encoded.Length && twoOver.Length == encoded.Length
                && await f.Read(oneOver) is null && await f.Read(twoOver) is null,
                "Decoded overflow is still rejected when Base64 padding gives it the same encoded length as the boundary");
            Check(await f.Read(encoded + " ") is null,
                "Encoded input above its raw character budget is rejected even when the extra character is legal whitespace");
            Check(await f.Read(new string(' ', ProcessSampler.MaximumPrivilegedIconBase64Chars) + "AQ==") is null,
                "Whitespace inflation cannot bypass the raw encoded input budget");
            Check(await f.Read(" A Q = = \r\n") is { Data: [1] },
                "Existing valid whitespace handling remains supported within the bounded input");
            Check(await f.Read("%%%%") is null, "Malformed bounded Base64 retains optional missing-icon behavior");
            Check(await f.Read("") is null && await f.Read("    ") is null,
                "Empty encoded and decoded responses do not become successful icons");
            Check(await f.Read("AQ==", "application/octet-stream") is null,
                "Unsupported Helper content types are refused without a fallback MIME");
            f.Transport.Result = new(false, OutputBase64: "AQ==", ProcessIconContentType: "image/png");
            Check(await f.GetIcon() is null, "A failed Helper result cannot expose its attached icon payload");

            // Keep payload creation and runtime warmup outside the measured interval. This is a decode allocation regression,
            // not a process-memory or GC measurement. Immediate fake transport keeps this actual facade call synchronous.
            var oversized = new string('A', 4 * 1024 * 1024);
            await f.Read("AQ==");
            f.Transport.Result = new(true, OutputBase64: oversized, ProcessIconContentType: "image/png");
            var before = GC.GetAllocatedBytesForCurrentThread();
            var request = f.GetIcon();
            var synchronous = request.IsCompletedSuccessfully;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var result = await request;
            Check(synchronous && result is null && allocated < ProcessSampler.MaximumPrivilegedIconBytes,
                "Rejecting prebuilt 4 MiB Base64 completes synchronously without allocating its 3 MiB decoded array");
            Check(f.Active == 0 && f.Transport.Calls == 14,
                "Every completed accepted Helper read releases its real work lease and is executed only once");
            await f.Sampler.StopAsync(default);
            var calls = f.Transport.Calls;
            Check(await f.GetIcon() is null && f.Transport.Calls == calls && f.Active == 0,
                "Stopped icon owner rejects work before Helper access or a new lease");
        }
        await using (var f = new Fixture())
        {
            await f.Coordinator.DisposeAsync();
            try { await f.Read("AQ=="); throw new Exception("Closed coordinator accepted icon"); }
            catch (ModuleUnavailableException) { }
            Check(f.Transport.Calls == 0 && f.Active == 0,
                "Closed coordinator cannot be bypassed by the new payload budget path");
        }
        Console.WriteLine($"Privileged process icon budget checks passed: {count}");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        internal readonly Transport Transport = new();
        internal ModuleResidencyCoordinator Coordinator => _app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal ProcessSampler Sampler => _app.Services.GetRequiredService<ProcessSampler>();
        internal int Active => Coordinator.Describe(ServerModules.SystemPerformanceId)?.ActiveLeases ?? 0;
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.SystemPerformanceId });
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            builder.Services.AddSingleton(services => new ProcessSampler(null!, null!, Transport, services.GetRequiredService<ISystemPerformanceWorkLeaseSource>()));
            _app = builder.Build();
        }
        internal Task<ProcessIconContent?> Read(string encoded, string contentType = "image/png")
        { Transport.Result = new(true, OutputBase64: encoded, ProcessIconContentType: contentType); return GetIcon(); }
        internal Task<ProcessIconContent?> GetIcon() => Sampler.GetIconAsync(123, DateTimeOffset.UtcNow, elevated: true);
        public async ValueTask DisposeAsync() { await Sampler.StopAsync(default); await _app.DisposeAsync(); }
    }
    private sealed class Transport : IPrivilegedOperationTransport
    {
        internal int Calls;
        internal PrivilegedOperationResult Result = new(true, OutputBase64: "AQ==", ProcessIconContentType: "image/png");
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            if (request.Operation != PrivilegedOperationKind.ProcessIcon) throw new Exception("Unexpected Helper operation");
            Interlocked.Increment(ref Calls); return Task.FromResult(Result);
        }
    }
}
