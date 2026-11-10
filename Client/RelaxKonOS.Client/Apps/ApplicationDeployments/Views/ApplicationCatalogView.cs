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
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.Views;

internal sealed class ApplicationCatalogView : UserControl
{
    private const string Prefix = "application_deployments.catalog.";
    private readonly ComboBox releasePicker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel fields = new() { Spacing = 6 };
    private readonly TextBox name = new() { MaxLength = 40 };
    private readonly TextBox port = new() { MaxLength = 5 };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button install = new() { IsEnabled = false };
    private readonly List<(ApplicationCatalogFieldDto Definition, TextBox Editor)> editors = [];
    private InstallCatalogApplicationRequest? lastRequest;
    private string? requestKey;

    public ApplicationCatalogView(IRemoteApplicationDeploymentClient client, IReadOnlyList<ApplicationCatalogTemplateDto> releases,
        ModalDialog<CatalogApplicationInstallDto?> dialog)
    {
        dialog.CanCancelAsync = () => Task.FromResult(IsEnabled);
        releasePicker.ItemsSource = releases.Select(release => new ComboBoxItem
        {
            Content = $"{release.Purpose} · {release.Version}",
            Tag = release,
        }).ToArray();
        releasePicker.SelectionChanged += (_, _) => SelectRelease();
        install.Content = Text("install");
        install.Click += async (_, _) =>
        {
            if (SelectedRelease is not { } release || !install.IsEnabled) return;
            if (!int.TryParse(port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort) || hostPort is < 1 or > 65535
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
                    new TextBlock { Text = Text("port") }, port, fields, error,
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
        if (SelectedRelease is not { } release) return;

        install.IsEnabled = release is { Trusted: true, Withdrawn: false, SchemaVersion: "2", Source: "built-in" }
            && !release.RequiredCapabilities.Contains("server.application-deployments.compose", StringComparer.Ordinal)
            && release.Fields.All(field => field.Type is "text" or "secret");
        var volumes = string.Join(", ", release.Volumes.Select(volume => volume.ContainerPath));
        details.Text = $"{release.Description}\n{release.MaintenanceNotes}\n"
            + LocalizedText.Format(Prefix + "impact", release.ContainerPort, volumes.Length == 0 ? "—" : volumes);
        if (!install.IsEnabled)
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

    private static string Text(string key) => LocalizedText.Get(Prefix + key);
}
