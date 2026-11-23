using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Server.ApplicationDeployments;

internal static class DeploymentRuntimeLiveChecks
{
    public static async Task RunAsync(string imageId)
    {
        if (!Regex.IsMatch(imageId, @"\Asha256:[0-9a-f]{64}\z"))
            throw new ArgumentException("Expected the immutable ID of a locally cached BusyBox image.", nameof(imageId));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var engine = new DockerCliEngineService(new(), new DisabledDockerProxyResolver(), NullLogger<DockerCliEngineService>.Instance);
        var runtime = new ApplicationDeploymentRuntime(engine, new(), new HealthClientFactory(http), NullLogger<ApplicationDeploymentRuntime>.Instance);
        var platform = await runtime.PreflightEngineAsync(ct);
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var applicationId = Guid.NewGuid();
        var name = ApplicationDeploymentValidation.ContainerName(applicationId);
        var marker = applicationId.ToString("N");
        var now = DateTimeOffset.UtcNow;
        var application = new ApplicationRecord(applicationId, name, "deployment-runtime-live", ApplicationSourceKind.Image,
            ApplicationWorkloadKind.Web, ApplicationDesiredState.Stopped, ApplicationReadinessLevel.Http,
            "/ready", 8080, port, "127.0.0.1", new(), [], [], null, null, null, null, null, null, now, now, null);
        var revision = new RevisionRecord(Guid.NewGuid(), applicationId, 1, application.SourceKind, "1.0.0",
            ApplicationDeploymentValidation.Reference(imageId), imageId, imageId, platform, null,
            application.WorkloadKind, application.ReadinessLevel, application.HealthCheckPath, "sh",
            ["sh", "-ec", $"mkdir -p /tmp/site; echo {marker} > /tmp/site/ready; exec httpd -f -p 8080 -h /tmp/site"],
            application.ContainerPort, application.HostPort, application.BindAddress, application.Limits, [], [], null,
            application.OwnerReference, DateTimeOffset.UtcNow);

        try
        {
            var created = await runtime.CreateContainerAsync(application, revision, name, [], Guid.NewGuid(), "candidate", null, ct);
            Require(created.Success, $"Create failed: {created.ProblemCode}");
            var owned = await runtime.FindOwnedContainerAsync(applicationId, name, ct)
                ?? throw new InvalidOperationException("The created container did not have the expected ownership labels.");
            var started = await runtime.StartAsync(owned.Id, ct);
            Require(started.Success, $"Start failed: {started.ProblemCode}");
            while (!await runtime.IsReadyAsync(application, owned.Id, ct, requireSuccessStatus: true))
                await Task.Delay(250, ct);
            var body = await http.GetStringAsync($"http://127.0.0.1:{port}/ready", ct);
            Require(body.Trim() == marker, "The published health endpoint returned the wrong fixture marker.");
            var stopped = await runtime.StopAsync(owned.Id, force: true, ct);
            Require(stopped.Success && !await runtime.IsReadyAsync(application, owned.Id, ct), "The stopped container remained ready.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var owned = await runtime.FindOwnedContainerAsync(applicationId, name, cleanup.Token);
            if (owned is not null)
            {
                var removed = await runtime.RemoveAsync(owned.Id, cleanup.Token);
                Require(removed.Success, $"Cleanup failed for {name}: {removed.ProblemCode}");
            }
            Require(!(await runtime.ListContainersAsync(cleanup.Token)).Any(container => container.Names == name),
                $"The fixture container {name} remains in Docker.");
        }
        Console.WriteLine("Live Application Deployments runtime create, HTTP readiness, stop and cleanup passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class HealthClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
