using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Installations;

internal static class ManagedRuntimeLedgerFixture
{
    internal static async Task<InstallationOperationDto> TerminalAsync(InstallationCoordinator coordinator, Guid operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            var current = coordinator.Get(operation)!.Operation;
            if (!InstallationOperationStore.Active(current)) return current;
            await Task.Delay(10, timeout.Token);
        }
    }
    internal sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "ManagedRuntimeLedgerFixture";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    internal sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => new(true);
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }
}
