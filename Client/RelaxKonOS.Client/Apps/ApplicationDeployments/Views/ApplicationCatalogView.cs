using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.Views;

internal sealed class ApplicationCatalogView : UserControl
{
    private const string Prefix = "application_deployments.catalog.";
    private readonly ComboBox releasePicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel fields = new() { Spacing = 6 };
    private readonly TextBox name = new() { Name = "CatalogInstanceName", MaxLength = 40 };
    private readonly TextBox port = new() { Name = "CatalogHostPort", MaxLength = 5 };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock topology = new() { Name = "CatalogTopology", TextWrapping = TextWrapping.Wrap };
    private readonly Button preview = new() { Name = "CatalogPreviewTopology", IsVisible = false };
    private readonly Button install = new() { Name = "CatalogInstall", IsEnabled = false };
    private readonly List<(ApplicationCatalogFieldDto Definition, TextBox Editor)> editors = [];
    private InstallCatalogApplicationRequest? lastRequest;
    private string? requestKey;
    private readonly ServerDescriptorDto? server;
    private int? previewedPort;

    public ApplicationCatalogView(IRemoteApplicationDeploymentClient client, IReadOnlyList<ApplicationCatalogTemplateDto> releases,
        ServerDescriptorDto? server, ModalDialog<CatalogApplicationInstallDto?> dialog)
    {
        this.server = server;
        dialog.CanCancelAsync = () => Task.FromResult(IsEnabled);
        releasePicker.ItemsSource = releases.Select(release => new ComboBoxItem
        {
            Content = $"{release.Purpose} · {release.Version}",
            Tag = release,
        }).ToArray();
        releasePicker.SelectionChanged += (_, _) => SelectRelease();
        port.TextChanged += (_, _) => ResetTopology();
        preview.Content = Text("preview_topology");
        preview.Click += async (_, _) =>
        {
            if (SelectedRelease is not { } release || !preview.IsVisible || !TryPort(out var hostPort))
            {
                error.Text = Text("invalid");
                return;
            }
            ResetTopology();
            error.Text = "";
            IsEnabled = false;
            try
            {
                var result = await client.PreviewCatalogTopologyAsync(release.Id, release.Version, hostPort);
                if (SelectedRelease != release || !TryPort(out var currentPort) || currentPort != hostPort) return;
                if (result.TemplateId != release.Id || result.TemplateVersion != release.Version || result.HostPort != hostPort
                    || result.BindAddress != "127.0.0.1" || result.Services.Count(service => service.Id == result.WebService) != 1)
                {
                    error.Text = Text("preview_failed");
                    return;
                }
                topology.Text = FormatTopology(result);
                previewedPort = hostPort;
                install.IsEnabled = true;
            }
            catch (ApplicationDeploymentClientException failure) { error.Text = DeploymentText.Problem(failure.ProblemCode).Resolve(); }
            catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException or JsonException)
            { error.Text = Text("preview_failed"); }
            finally { IsEnabled = true; }
        };
        install.Content = Text("install");
        install.Click += async (_, _) =>
        {
            if (SelectedRelease is not { } release || !install.IsEnabled) return;
            if (!TryPort(out var hostPort) || (RequiresTopology(release) && previewedPort != hostPort)
                || string.IsNullOrWhiteSpace(name.Text) || editors.Any(entry => entry.Definition.Required && string.IsNullOrWhiteSpace(entry.Editor.Text)))
            {
                error.Text = Text("invalid");
                return;
            }

            var request = new InstallCatalogApplicationRequest(release.Id, release.Version, name.Text.Trim(), hostPort,
                [.. editors.Where(entry => entry.Editor.Text is { Length: > 0 }).Select(entry => new ApplicationCatalogFieldValueDto(entry.Definition.Id, entry.Editor.Text!))],
                Confirmed: true);
            if (lastRequest is null || (lastRequest with { Fields = request.Fields }) != request || !lastRequest.Fields.SequenceEqual(request.Fields))
                requestKey = Guid.NewGuid().ToString("N");
            lastRequest = request;
            error.Text = "";
            IsEnabled = false;
            try { dialog.Close(await client.InstallCatalogAsync(request, requestKey!)); }
            catch (ApplicationDeploymentClientException failure) { error.Text = DeploymentText.Problem(failure.ProblemCode).Resolve(); }
            catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException or JsonException)
            { error.Text = Text("submit_failed"); }
            finally { IsEnabled = true; }
        };

        var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
        cancel.Click += (_, _) => dialog.Cancel();
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(20),
                Children =
                {
                    releasePicker, details, new TextBlock { Text = Text("name") }, name,
                    new TextBlock { Text = Text("port") }, port, fields, preview, topology, error,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { install, cancel } },
                },
            },
        };
        if (releases.Count > 0) releasePicker.SelectedIndex = 0;
        else details.Text = Text("empty");
    }

    private ApplicationCatalogTemplateDto? SelectedRelease => (releasePicker.SelectedItem as ComboBoxItem)?.Tag as ApplicationCatalogTemplateDto;

    private void SelectRelease()
    {
        fields.Children.Clear();
        editors.Clear();
        error.Text = "";
        ResetTopology();
        if (SelectedRelease is not { } release) return;

        var canInstall = CanInstall(release, server);
        var volumes = string.Join(", ", release.Volumes.Select(volume => volume.ContainerPath));
        details.Text = $"{release.Description}\n{release.MaintenanceNotes}\n"
            + LocalizedText.Format(Prefix + "impact", release.ContainerPort, volumes.Length == 0 ? "—" : volumes);
        if (!canInstall)
        {
            error.Text = Text("unsupported");
            return;
        }

        var language = App.Services.GetRequiredService<LocalizationService>().CurrentLanguage;
        foreach (var field in release.Fields)
        {
            var label = language switch
            {
                "zh-CN" => field.Labels.Chinese,
                "ja-JP" => field.Labels.Japanese,
                _ => field.Labels.English,
            };
            var editor = new TextBox { MaxLength = 4096, Text = field.DefaultValue };
            if (field.Type == "secret") editor.PasswordChar = '●';
            fields.Children.Add(new TextBlock { Text = field.Required ? label + " *" : label });
            fields.Children.Add(editor);
            editors.Add((field, editor));
        }
    }

    internal static bool CanInstall(ApplicationCatalogTemplateDto release, ServerDescriptorDto? server) =>
        release is { Trusted: true, Withdrawn: false, SchemaVersion: "2", Source: "built-in" }
        && server is { Platform: HostPlatformKind.Linux }
        && release.SupportedPlatforms.Contains("linux/amd64", StringComparer.Ordinal)
        && release.RequiredCapabilities.All(capability => server.Capabilities.Contains(capability, StringComparer.Ordinal))
        && release.Fields.All(field => field.Type is "text" or "secret");

    private static bool RequiresTopology(ApplicationCatalogTemplateDto release) =>
        release.RequiredCapabilities.Contains(ServerCapabilities.ApplicationDeploymentsCompose, StringComparer.Ordinal);

    private void ResetTopology()
    {
        topology.Text = "";
        previewedPort = null;
        var release = SelectedRelease;
        var canInstall = release is not null && CanInstall(release, server);
        preview.IsVisible = canInstall && RequiresTopology(release!);
        install.IsEnabled = canInstall && !preview.IsVisible;
    }

    private bool TryPort(out int hostPort) =>
        int.TryParse(port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out hostPort) && hostPort is >= 1 and <= 65535;

    private static string FormatTopology(CatalogApplicationTopologyDto value)
    {
        var lines = new List<string> { LocalizedText.Format(Prefix + "topology_publish", value.BindAddress, value.HostPort, value.WebService) };
        lines.AddRange(value.Services.Select(service => LocalizedText.Format(Prefix + "topology_service",
            service.Id, service.ContainerPort, Join(service.DependsOn), Join(service.Volumes.Select(volume => volume.ContainerPath)))));
        return string.Join("\n", lines);

        static string Join(IEnumerable<string> values) => string.Join(", ", values.DefaultIfEmpty("—"));
    }

    private static string Text(string key) => LocalizedText.Get(Prefix + key);
}
