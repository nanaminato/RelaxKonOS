using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.FileServices;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;

internal static class FileServicesLiveChecks
{
    private sealed record PipeConfiguration(string? PipeName, string? SharedSecret);

    internal static Task RunAsync(string helper, string desktopRunner, string package) =>
        RunAsync(new OneShotHelper(Path.GetFullPath(helper)), desktopRunner, package);

    internal static Task RunPipeAsync(string configPath, string desktopRunner, string package)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This check uses Windows SMB.");
        PipeConfiguration? configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<PipeConfiguration>(File.ReadAllText(configPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The Helper pipe configuration is not valid JSON.");
        }
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.PipeName)
            || string.IsNullOrWhiteSpace(configuration.SharedSecret))
            throw new InvalidOperationException("The Helper pipe configuration must specify a pipe and shared secret.");
        var options = new PrivilegedHelperOptions { PipeName = configuration.PipeName, SharedSecret = configuration.SharedSecret };
        return RunAsync(new WindowsNamedPipePrivilegedOperationTransport(options,
            NullLogger<WindowsNamedPipePrivilegedOperationTransport>.Instance), desktopRunner, package);
    }

    private static async Task RunAsync(IPrivilegedOperationTransport transport, string desktopRunner, string package)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This check uses Windows SMB.");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Storage:Provider"] = "memory";
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ProgressTestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("FileServicesRead", policy => policy.RequireRole("reader"));
            options.AddPolicy("FileServicesManage", policy => policy.RequireAssertion(_ => false));
        });
        builder.Services.AddSingleton<IServerModeResolver, ProgressTestMode>();
        builder.Services.AddSingleton<IHostElevationSessionStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IFileServiceAudit>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(new ReadOnlyHelper(transport));
        builder.Services.AddSingleton<IPrivilegedSmbOperations, PrivilegedSmbOperations>();
        builder.Services.AddSingleton<IWindowsSmbPlatformAdapter, WindowsSmbPlatformAdapter>();
        builder.Services.AddSingleton<IWindowsSmbOwnershipLedger, WindowsSmbOwnershipLedger>();
        builder.Services.AddSingleton<IFileServiceProvider, WindowsSmbFileServiceProvider>();
        builder.Services.AddSingleton<IFileServiceProviderResolver, FileServiceProviderResolver>();
        builder.Services.AddSingleton<IFileServiceManager, FileServiceManager>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapFileServiceEndpoints();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.ArgumentList.Add(Path.GetFullPath(desktopRunner));
        start.ArgumentList.Add("--official-roapp-only");
        start.ArgumentList.Add(Path.GetFullPath(package));
        start.Environment["RK_TEST_SMB_PORT"] = new Uri(address).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var desktop = Process.Start(start) ?? throw new InvalidOperationException("Desktop test did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await desktop.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!desktop.HasExited) desktop.Kill();
            await desktop.WaitForExitAsync();
        }
        if (desktop.ExitCode != 0) throw new InvalidOperationException($"Desktop test exited with code {desktop.ExitCode}.");
    }

    private sealed class ReadOnlyHelper(IPrivilegedOperationTransport transport) : IPrivilegedOperationTransport
    {
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Operation is not (PrivilegedOperationKind.SmbDetect or PrivilegedOperationKind.SmbReadManagedConfiguration))
                throw new InvalidOperationException("The live SMB check permits only read operations.");
            return transport.ExecuteAsync(request, cancellationToken);
        }
    }

    private sealed class OneShotHelper(string executable) : IPrivilegedOperationTransport
    {
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("Helper did not start.");
            try
            {
                await helper.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request with
                {
                    Correlation = CorrelationContext.Create(request.OperationId, "privileged.operation"),
                }));
                helper.StandardInput.Close();
                var output = await helper.StandardOutput.ReadToEndAsync(cancellationToken);
                return JsonSerializer.Deserialize<PrivilegedOperationFrame>(output)?.Result
                    ?? throw new InvalidOperationException("Helper did not return a result.");
            }
            finally
            {
                if (!helper.HasExited) helper.Kill();
                await helper.WaitForExitAsync();
            }
        }
    }
}
