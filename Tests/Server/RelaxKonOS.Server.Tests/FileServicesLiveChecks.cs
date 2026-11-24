using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
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

    internal static Task RunAsync(string helper, string desktopRunner, string package)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This check uses Windows SMB.");
        return RunAsync(new OneShotHelper(Path.GetFullPath(helper)), desktopRunner, package);
    }

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

    internal static Task RunSocketAsync(string socketPath, string desktopRunner, string package, string expectedShare,
        string? action = null, string? sharePath = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This check uses Linux Samba.");
        var options = new PrivilegedHelperOptions { SocketPath = Path.GetFullPath(socketPath) };
        return RunAsync(new UnixSocketPrivilegedOperationTransport(options,
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance), desktopRunner, package, expectedShare, action, sharePath);
    }

    private static async Task RunAsync(IPrivilegedOperationTransport transport, string desktopRunner, string package,
        string? expectedShare = null, string? action = null, string? sharePath = null)
    {
        var manage = action is "create" or "delete";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Storage:Provider"] = "memory";
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ProgressTestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("FileServicesRead", policy => policy.RequireRole("reader", "manager"));
            options.AddPolicy("FileServicesManage", policy => policy.RequireAssertion(context => manage && context.User.IsInRole("manager")));
        });
        builder.Services.AddSingleton<IServerModeResolver, ProgressTestMode>();
        var audit = new ManagementAudit();
        var grant = new ManagementGrant(manage);
        builder.Services.AddSingleton<IHostElevationSessionStore>(grant);
        builder.Services.AddSingleton<IFileServiceAudit>(audit);
        var observedHelper = new ObservedHelper(transport, manage);
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(observedHelper);
        builder.Services.AddSingleton<IPrivilegedSmbOperations, PrivilegedSmbOperations>();
        if (OperatingSystem.IsLinux())
        {
            builder.Services.AddSingleton<ISambaPlatformAdapter, LinuxSambaPlatformAdapter>();
            builder.Services.AddSingleton<IFileServiceProvider, LinuxSambaFileServiceProvider>();
        }
        else
        {
            builder.Services.AddSingleton<IWindowsSmbPlatformAdapter, WindowsSmbPlatformAdapter>();
            builder.Services.AddSingleton<IWindowsSmbOwnershipLedger, WindowsSmbOwnershipLedger>();
            builder.Services.AddSingleton<IFileServiceProvider, WindowsSmbFileServiceProvider>();
        }
        builder.Services.AddSingleton<IFileServiceProviderResolver, FileServiceProviderResolver>();
        builder.Services.AddSingleton<IFileServiceManager, FileServiceManager>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapFileServiceEndpoints();
        if (manage)
        {
            app.MapPost(PrivilegedApiRoutes.Elevation, (ClaimsPrincipal principal, HostElevationRequest request) =>
            {
                if (request.Password != "isolated-test-credential" || request.AdministratorUsername != "isolated-fixture"
                    || request.Capability != HostElevationCapability.SmbManage || request.Target != "smb:managed")
                    return Results.BadRequest();
                return Results.Ok(new HostElevationResult(true, grant.Grant(principal, request.Capability, request.Target, false, "fixture")));
            }).RequireAuthorization("FileServicesManage");
        }
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var runner = Path.GetFullPath(desktopRunner);
        var managedRunner = Path.GetExtension(runner).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(managedRunner ? "dotnet" : runner) { UseShellExecute = false };
        if (managedRunner) start.ArgumentList.Add(runner);
        start.ArgumentList.Add("--official-roapp-only");
        start.ArgumentList.Add(Path.GetFullPath(package));
        start.Environment["RK_TEST_SMB_PORT"] = new Uri(address).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (expectedShare is not null) start.Environment["RK_TEST_SMB_SHARE"] = expectedShare;
        start.Environment.Remove("RK_TEST_SMB_ACTION");
        start.Environment.Remove("RK_TEST_SMB_SHARE_PATH");
        if (manage)
        {
            start.Environment["RK_TEST_SMB_ACTION"] = action!;
            start.Environment["RK_TEST_SMB_SHARE_PATH"] = sharePath!;
        }
        using var desktop = Process.Start(start) ?? throw new InvalidOperationException("Desktop test did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await desktop.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!desktop.HasExited) desktop.Kill();
            await desktop.WaitForExitAsync();
        }
        if (desktop.ExitCode != 0) throw new InvalidOperationException($"Desktop test exited with code {desktop.ExitCode}.");
        observedHelper.VerifyObservedReads();
        if (manage && audit.CompletedAction != "share-" + action)
            throw new InvalidOperationException("The installed File Services package did not complete the expected audited share mutation.");
    }

    private sealed class ManagementGrant(bool enabled) : IHostElevationSessionStore
    {
        private DateTimeOffset _expires;
        public bool IsGranted(ClaimsPrincipal principal, HostElevationCapability capability, string target) =>
            enabled && principal.IsInRole("manager") && capability == HostElevationCapability.SmbManage
            && target == "smb:managed" && _expires > DateTimeOffset.UtcNow;
        public DateTimeOffset Grant(ClaimsPrincipal principal, HostElevationCapability capability, string target,
            bool includeDescendants, string authenticationMethod, string? correlationId = null) => _expires = DateTimeOffset.UtcNow.AddMinutes(1);
        public void Revoke(ClaimsPrincipal principal) => _expires = default;
    }

    private sealed class ManagementAudit : IFileServiceAudit
    {
        public string? CompletedAction { get; private set; }
        public Task WriteAsync(ClaimsPrincipal actor, string action, string resource, bool succeeded, string? problemCode,
            Guid operationId, CancellationToken ct)
        {
            if (succeeded && operationId != Guid.Empty) CompletedAction = action;
            return Task.CompletedTask;
        }
    }

    private sealed class ObservedHelper(IPrivilegedOperationTransport transport, bool manage) : IPrivilegedOperationTransport
    {
        private int _successfulDetect;
        private int _successfulConfigurationRead;

        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Operation is not (PrivilegedOperationKind.SmbDetect or PrivilegedOperationKind.SmbReadManagedConfiguration
                or PrivilegedOperationKind.SmbReadUsers) && !(manage && request.Operation == PrivilegedOperationKind.SmbApplyManagedConfiguration))
                throw new InvalidOperationException("Unexpected Helper operation in the live SMB check.");
            var result = await transport.ExecuteAsync(request, cancellationToken);
            if (result.Success)
            {
                if (request.Operation == PrivilegedOperationKind.SmbDetect)
                    Interlocked.Increment(ref _successfulDetect);
                else if (request.Operation == PrivilegedOperationKind.SmbReadManagedConfiguration)
                    Interlocked.Increment(ref _successfulConfigurationRead);
            }
            return result;
        }

        internal void VerifyObservedReads()
        {
            if (Volatile.Read(ref _successfulDetect) == 0 || Volatile.Read(ref _successfulConfigurationRead) == 0)
                throw new InvalidOperationException("The installed desktop must complete both SMB Helper read operations.");
            Console.WriteLine("The installed File Services package completed both SMB Helper reads.");
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
