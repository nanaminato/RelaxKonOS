using System.Net.Http;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.ApplicationDeployments;
using RelaxKonOS.Client.Apps.ApplicationDeployments.Views;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.WindowManager;

internal static class ApplicationCatalogLiveChecks
{
    internal static void Run(string serverUrl, string packageId, string phase)
    {
        var uri = new Uri(serverUrl, UriKind.Absolute);
        Check(uri.Scheme == "http" && uri.Host == "127.0.0.1", "The live fixture requires an isolated loopback Server.");
        var user = Environment.GetEnvironmentVariable("RK_CA_DESKTOP_USER");
        var password = Environment.GetEnvironmentVariable("RK_CA_DESKTOP_PASSWORD");
        Check(!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password), "Provide the dedicated PAM fixture credentials.");
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var session = new AuthSession(new RelaxKonOSClient(http), null!, null!);
        Wait(session.LoginAsync(ServerConnectionIdentityRules.Direct(serverUrl),
            new(user!, password!, ClientPlatformKind.Linux, "ca-desktop-live", "0.1.0"), false, false));
        var client = new RemoteApplicationDeploymentClient(http, session);
        var catalog = Wait(client.ListCatalogAsync());
        Console.WriteLine($"desktop_sha256={Hash(typeof(ApplicationCatalogView))} runner_sha256={Hash(typeof(ApplicationCatalogLiveChecks))}");
        var release = catalog.SingleOrDefault(item => item.Id == packageId && item.Version == "1.0.0");
        if (phase == "absent")
        {
            Check(release is null, "The added package must be absent before its manifest is installed.");
            Console.WriteLine($"absent package={packageId}");
            return;
        }

        Check(release is not null && ApplicationCatalogView.CanInstall(release, session.CurrentServer), "The same client must admit the newly added package.");
        var manager = new RelaxKonOS.WindowManager.WindowManager();
        manager.Attach(new Canvas { Width = 1000, Height = 800 });
        var owner = manager.Create(new WindowCreateOptions(new("test.catalog.live"), "Owner", new TextBlock(),
            new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 600)));
        ApplicationCatalogView? view = null;
        ModalDialog<CatalogApplicationInstallDto?>? dialog = null;
        _ = manager.ShowDialogAsync<CatalogApplicationInstallDto?>(owner, "Catalog", handle =>
        {
            dialog = handle;
            return view = new ApplicationCatalogView(client, [release!], session.CurrentServer, handle);
        });
        var form = (StackPanel)((ScrollViewer)view!.Content!).Content!;
        var port = form.Children.OfType<TextBox>().Single(text => text.Name == "CatalogHostPort");
        form.Children.OfType<TextBox>().Single(text => text.Name == "CatalogInstanceName").Text = "rk-ca-desktop-" + Guid.NewGuid().ToString("N")[..8];
        var secret = form.Children.OfType<StackPanel>().First().Children.OfType<TextBox>().Single();
        Check(secret.PasswordChar != '\0' && release!.Fields is [{ Id: "password", Type: "secret" }], "The package-defined field must render as a masked editor.");
        secret.Text = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        const int hostPort = 18086;
        port.Text = hostPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Dispatcher.UIThread.RunJobs();
        var preview = form.Children.OfType<Button>().Single(button => button.Name == "CatalogPreviewTopology");
        var install = form.Children.OfType<StackPanel>().Last().Children.OfType<Button>().Single(button => button.Name == "CatalogInstall");
        Check(!install.IsEnabled, "Install must wait for the real Server topology.");
        preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => view.IsEnabled);
        var topology = form.Children.OfType<TextBlock>().Single(text => text.Name == "CatalogTopology");
        Check(install.IsEnabled && topology.Text?.Contains("127.0.0.1:18086", StringComparison.Ordinal) == true
            && topology.Text.Contains("/var/lib/postgresql/data", StringComparison.Ordinal),
            "The real topology must render the Web loopback and the database volume.");
        install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var receipt = Wait(dialog!.Result);
        Check(receipt is not null && receipt.TemplateId == packageId, "The client must receive the added package's installation receipt.");
        var operationId = receipt!.Operation.OperationId;
        DeploymentOperationDto? operation = null;
        PumpUntil(() =>
        {
            operation = Wait(client.GetOperationAsync(operationId));
            if (operation?.State is not (DeploymentOperationState.Queued or DeploymentOperationState.Running)) return true;
            Thread.Sleep(250);
            return false;
        });
        Check(operation is { State: DeploymentOperationState.Succeeded } && operation.OperationId == operationId,
            $"The added package must activate under its original operation. State={operation?.State} Problem={operation?.ProblemCode}");
        var marker = Guid.NewGuid().ToString("N");
        using var put = Wait(http.PutAsync($"http://127.0.0.1:{hostPort}/marker", new StringContent(marker)));
        Check(put.IsSuccessStatusCode && Wait(http.GetStringAsync($"http://127.0.0.1:{hostPort}/marker")) == marker,
            "The package installed through the unchanged desktop UI must support actual business I/O.");
        Console.WriteLine($"installed package={packageId} app={receipt.Application.Id:D} operation={operationId:D} revision={operation!.TargetRevisionId:D} business_marker_read=true");
    }

    private static string Hash(Type type)
    {
        using var file = File.OpenRead(type.Assembly.Location);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private static T Wait<T>(Task<T> task)
    {
        PumpUntil(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!ready())
        {
            Dispatcher.UIThread.RunJobs();
            Check(DateTime.UtcNow < deadline, "The live desktop fixture timed out.");
            Thread.Sleep(10);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
