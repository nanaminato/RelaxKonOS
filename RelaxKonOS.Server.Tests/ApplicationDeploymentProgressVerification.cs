using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RelaxKonOS.Client.Apps.ApplicationDeployments;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Docker;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;

internal static class ApplicationDeploymentProgressVerification
{
    public static async Task RunAsync(string root)
    {
        await VerifyIncrementalOutputAsync();
        await VerifyUploadCancellationAsync();
        await VerifyDotNetPublishContextAsync(root);
        var directory = Path.Combine(root, "deployment-progress");
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ProgressTestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(ApplicationDeploymentEndpoints.ReadPolicy, p => p.RequireRole("controller", "reader"));
            options.AddPolicy(ApplicationDeploymentEndpoints.ManagePolicy, p => p.RequireRole("controller"));
        });
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<IServerModeResolver, ProgressTestMode>();
        builder.Services.AddSingleton(new ApplicationDeploymentOptions { MaximumArchiveBytes = 40L * 1024 * 1024 });
        builder.Services.AddSingleton<ApplicationDeploymentStagingStore>();
        builder.Services.AddSingleton<ApplicationDeploymentOperationStore>();
        builder.Services.AddSingleton<ApplicationDeploymentLiveLogs>();
        builder.Services.AddSingleton<ApplicationDeploymentLogSubscriptions>();
        builder.Services.AddHostedService<ApplicationDeploymentLogBroadcastService>();
        // Map the production routes, but do not execute any Docker operation.
        builder.Services.AddSingleton<ApplicationDeploymentManager>();
        builder.Services.AddSingleton<ApplicationDeploymentCoordinator>();
        builder.Services.AddSingleton<ApplicationDeploymentDefinitionMutationStore>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapApplicationDeploymentEndpoints();
        app.MapHub<ApplicationDeploymentLogsHub>(RelaxKonOSEndpoints.ApplicationDeploymentLogsHubPath);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
        using var denied = await http.PostAsync(ApplicationDeploymentApiRoutes.Uploads, new ByteArrayContent([]));
        Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Anonymous uploads must remain unauthorized.");
        http.DefaultRequestHeaders.Add("X-Test-Role", "controller");
        http.DefaultRequestHeaders.ExpectContinue = true;

        // Larger than Kestrel's default 30 MB limit. The production content counts streamed bytes.
        var bytes = new byte[32 * 1024 * 1024 + 123];
        bytes[0] = 0x50; bytes[^1] = 0x4b;
        using var source = new MemoryStream(bytes);
        var progress = new List<DeploymentUploadProgress>();
        using var form = new MultipartFormDataContent();
        form.Add(new DeploymentUploadContent(source, new ImmediateProgress(progress.Add)), "file", "large.zip");
        using var response = await http.PostAsync(ApplicationDeploymentApiRoutes.Uploads, form);
        Check(response.IsSuccessStatusCode, "A >30 MB upload must reach streaming staging: " + await response.Content.ReadAsStringAsync());
        var staged = (await response.Content.ReadFromJsonAsync<DeploymentStagedFileDto>())!;
        Check(staged.Length == bytes.Length && progress.Count >= 2 && progress[^1].Bytes == bytes.Length,
            "Upload must report actual bytes and receive the server's full length.");
        Check(progress.Zip(progress.Skip(1)).All(pair => pair.First.Bytes <= pair.Second.Bytes), "Upload progress must be monotonic.");
        var staging = app.Services.GetRequiredService<ApplicationDeploymentStagingStore>();
        using (var archive = staging.Open(staged.ReferenceId, "progress-test"))
        {
            Check(archive.Stream.ReadByte() == bytes[0], "First uploaded byte was lost.");
            archive.Stream.Position = bytes.Length - 1;
            Check(archive.Stream.ReadByte() == bytes[^1], "Last uploaded byte was lost.");
        }
        var existingArchivePath = Path.Combine(directory, "operator-owned.zip");
        await File.WriteAllBytesAsync(existingArchivePath, [0x50, 0x4b]);
        var registered = staging.Register(existingArchivePath, "progress-test");
        staging.Discard(registered.ReferenceId, "progress-test");
        Check(File.Exists(existingArchivePath), "Discarding a server-file reference must not delete the operator-owned archive.");
        using var tooLarge = new MultipartFormDataContent();
        tooLarge.Add(new ByteArrayContent(new byte[41 * 1024 * 1024]), "file", "too-large.zip");
        using var oversized = await http.PostAsync(ApplicationDeploymentApiRoutes.Uploads, tooLarge);
        Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Configured upload limit must still reject oversize requests.");
        using var malformedRequest = new HttpRequestMessage(HttpMethod.Post, ApplicationDeploymentApiRoutes.Uploads)
        { Content = new ByteArrayContent([]) };
        malformedRequest.Content.Headers.ContentType = new("multipart/form-data");
        using var malformed = await http.SendAsync(malformedRequest);
        Check(malformed.StatusCode == HttpStatusCode.BadRequest, "Malformed boundary must produce a controlled error.");

        using var forbiddenRequest = new HttpRequestMessage(HttpMethod.Post,
            RelaxKonOSEndpoints.ApplicationDeploymentLogsHubPath + "/negotiate?negotiateVersion=1");
        forbiddenRequest.Headers.Add("X-Test-Role", "unauthorized-role");
        using var forbidden = await http.SendAsync(forbiddenRequest);
        Check(forbidden.StatusCode == HttpStatusCode.Forbidden, "Hub must reject users without the deployment read policy.");

        var operations = app.Services.GetRequiredService<ApplicationDeploymentOperationStore>();
        var operation = operations.Create(Guid.NewGuid(), "progress-app", DeploymentOperationKind.Deploy,
            "progress-test", "key", ApplicationDeploymentValidation.Reference("request"), [], out _).Operation;
        var logs = app.Services.GetRequiredService<ApplicationDeploymentLiveLogs>();
        logs.Append(operation.OperationId, "password=do-not-send", DeploymentStage.Building);
        var received = new TaskCompletionSource<DeploymentLiveLogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder().WithUrl(address + RelaxKonOSEndpoints.ApplicationDeploymentLogsHubPath,
            options => options.Headers["X-Test-Role"] = "reader").Build();
        hub.On<DeploymentLiveLogSnapshot>(nameof(IApplicationDeploymentLogsClient.OnDeploymentLogs), snapshot =>
        {
            if (snapshot.Lines.Any(x => x.Message.Contains("layer downloading"))) received.TrySetResult(snapshot);
        });
        await hub.StartAsync();
        var initial = await hub.InvokeAsync<DeploymentLiveLogSnapshot>(ApplicationDeploymentLogsHubMethods.Subscribe, operation.OperationId);
        Check(initial.Lines.Count == 1 && !initial.Lines[0].Message.Contains("do-not-send"), "Subscription must return a sanitized tail.");
        logs.Append(operation.OperationId, "layer downloading 1MB/2MB");
        var live = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(live.Version > initial.Version, "SignalR must deliver output before the operation completes.");
        await hub.StopAsync();
        for (var i = 0; i < 500; i++) logs.Append(operation.OperationId, "line " + i);
        await hub.StartAsync();
        var restored = await hub.InvokeAsync<DeploymentLiveLogSnapshot>(ApplicationDeploymentLogsHubMethods.Subscribe, operation.OperationId);
        Check(restored.Truncated && restored.Lines.Count == ApplicationDeploymentLiveLogs.MaximumLines
            && restored.Lines[^1].Message == "line 499", "Reconnect must restore the bounded latest tail.");
        try
        {
            await hub.InvokeAsync<DeploymentLiveLogSnapshot>(ApplicationDeploymentLogsHubMethods.Subscribe, Guid.NewGuid());
            throw new InvalidOperationException("A nonexistent operation was accepted.");
        }
        catch (Microsoft.AspNetCore.SignalR.HubException) { }
        await hub.StopAsync();
        await app.StopAsync();
        Console.WriteLine("Deployment progress passed: real HTTP >30 MB streaming upload, byte counts, cancellation, incremental Docker output, authorized SignalR delivery, sanitized replay and bounded reconnect tail.");
    }

    private static async Task VerifyIncrementalOutputAsync()
    {
        var pipe = new Pipe();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reader = new StreamReader(pipe.Reader.AsStream());
        var reading = DockerLiveOutput.ReadAsync(reader, line => { if (line == "first") first.TrySetResult(); });
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("first\r"));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!reading.IsCompleted, "Output must arrive before EOF.");
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 100000) + "\nlast"));
        await pipe.Writer.CompleteAsync();
        var output = await reading;
        Check(output.Length < 5000 && output.EndsWith("last"), "Unterminated and oversized lines must stay bounded.");
    }

    private static async Task VerifyUploadCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var input = new MemoryStream(new byte[1024 * 1024]);
        using var content = new DeploymentUploadContent(input, new ImmediateProgress(p =>
        {
            if (p.Bytes > 0) cancellation.Cancel();
        }));
        using var destination = new SlowWriteStream();
        try
        {
            await content.CopyToAsync(destination, cancellation.Token);
            throw new InvalidOperationException("Cancelled upload continued.");
        }
        catch (OperationCanceledException) { }
        Check(input.Position > 0 && input.Position < input.Length, "Mid-transfer cancellation must stop reading the archive before EOF.");
    }

    private static async Task VerifyDotNetPublishContextAsync(string root)
    {
        var context = Path.Combine(root, "dotnet-publish-context");
        Directory.CreateDirectory(context);
        var wrapper = Path.Combine(context, "published-application");
        Directory.CreateDirectory(wrapper);
        await File.WriteAllTextAsync(Path.Combine(wrapper, ".dockerignore"), "*\n!Dockerfile\n");
        await File.WriteAllTextAsync(Path.Combine(wrapper, "Demo.dll"), "published-assembly");
        await File.WriteAllTextAsync(Path.Combine(wrapper, "Demo.runtimeconfig.json"),
            "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"frameworks\":[{\"name\":\"Microsoft.AspNetCore.App\",\"version\":\"10.0.0\"}]}}");

        var application = new ApplicationRecord(Guid.NewGuid(), "dotnet-context-test", "owner",
            ApplicationSourceKind.DotNetPublish, ApplicationWorkloadKind.Web, ApplicationDesiredState.Stopped,
            ApplicationReadinessLevel.Process, null, 8080, null, "127.0.0.1",
            new ApplicationResourceLimitsDto(), [], [], null, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        var plan = new DeploymentPlan(ApplicationSourceKind.DotNetPublish, "1.0", "test:1", null,
            "archive", null, null, [], false);
        var template = ApplicationTemplateCatalog.Require(ApplicationSourceKind.DotNetPublish);
        ApplicationTemplateCatalog.UnwrapPublishRoot(context);
        await template.PrepareBuildContextAsync(plan, context, application, new ApplicationDeploymentOptions(), CancellationToken.None);

        var ignore = await File.ReadAllTextAsync(Path.Combine(context, ".dockerignore"));
        var dockerfile = await File.ReadAllTextAsync(Path.Combine(context, "Dockerfile"));
        Check(!Directory.Exists(wrapper) && !ignore.Contains('*') && File.Exists(Path.Combine(context, "Demo.dll")),
            "A wrapped publish must become the build root, and its .dockerignore must not control the generated context.");
        Check(dockerfile.Contains("FROM mcr.microsoft.com/dotnet/aspnet:10.0", StringComparison.Ordinal)
            && dockerfile.Contains("RUN test -f /app/Demo.dll", StringComparison.Ordinal),
            "Framework-dependent web publishes must use the matching runtime and verify the copied entry assembly.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ImmediateProgress(Action<DeploymentUploadProgress> report) : IProgress<DeploymentUploadProgress>
    { public void Report(DeploymentUploadProgress value) => report(value); }

    private sealed class SlowWriteStream : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(110, cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}

internal sealed class ProgressTestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "progress-test"), new Claim(ClaimTypes.Role, role.ToString())], "test"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "test")));
    }
}

internal sealed class ProgressTestMode : IServerModeResolver
{
    public ServerMode Mode => ServerMode.System;
    public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    public bool Supports(ServerHostFeature feature) => true;
}
