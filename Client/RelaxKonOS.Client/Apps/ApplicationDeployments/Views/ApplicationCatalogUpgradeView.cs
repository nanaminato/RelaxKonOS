using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.Views;

internal sealed class ApplicationCatalogUpgradeView : UserControl
{
    private const string Prefix = "application_deployments.catalog.";

    public ApplicationCatalogUpgradeView(IRemoteApplicationDeploymentClient client, ApplicationDto application,
        IReadOnlyList<ApplicationCatalogTemplateDto> releases, ModalDialog<DeploymentOperationDto?> dialog)
    {
        var version = new ComboBox
        {
            ItemsSource = releases.Select(release => release.Version).ToArray(),
            SelectedIndex = releases.Count == 0 ? -1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var details = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = releases.Count == 0 ? Text("no_upgrade") : LocalizedText.Format(Prefix + "current_version", application.CatalogTemplateVersion),
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var review = new Button { Content = Text("review_upgrade"), IsEnabled = releases.Count > 0 };
        var upgrade = new Button { Content = Text("confirm_upgrade"), IsEnabled = false };
        var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
        var requestKeys = new Dictionary<UpdateCatalogApplicationRequest, string>();
        UpdateCatalogApplicationRequest? confirmedRequest = null;

        dialog.CanCancelAsync = () => Task.FromResult(IsEnabled);
        cancel.Click += (_, _) => dialog.Cancel();
        version.SelectionChanged += (_, _) =>
        {
            confirmedRequest = null;
            upgrade.IsEnabled = false;
            error.Text = "";
            details.Text = LocalizedText.Format(Prefix + "current_version", application.CatalogTemplateVersion);
        };
        review.Click += async (_, _) =>
        {
            if (version.SelectedItem is not string target) return;
            confirmedRequest = null;
            upgrade.IsEnabled = false;
            await ExecuteAsync(async () =>
            {
                var plan = await client.PreviewCatalogUpdateAsync(application.Id, target);
                details.Text = LocalizedText.Format(Prefix + "upgrade_impact",
                    plan.CurrentTemplateVersion, plan.CurrentImageReference, plan.Target.Version, plan.TargetImageReference)
                    + "\n" + plan.UpdateNotes;
                if (plan.Blockers.Count != 0)
                {
                    error.Text = string.Join("\n", plan.Blockers.Select(problem => DeploymentText.Problem(problem).Resolve()));
                    return;
                }
                confirmedRequest = new(plan.Target.Id, plan.Target.Version, plan.ExpectedUpdatedAt,
                    plan.ExpectedRevisionId, plan.CurrentTemplateVersion, plan.TargetImageReference, Confirmed: true);
                upgrade.IsEnabled = true;
            });
        };
        upgrade.Click += async (_, _) =>
        {
            if (confirmedRequest is not { } request) return;
            if (!requestKeys.TryGetValue(request, out var key))
            {
                key = Guid.NewGuid().ToString("N");
                requestKeys.Add(request, key);
            }
            await ExecuteAsync(async () => dialog.Close(await client.UpdateCatalogAsync(application.Id, request, key)));
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(20),
                Children =
                {
                    version, details, error,
                    new WrapPanel { ItemSpacing = 8, LineSpacing = 6, Children = { review, upgrade, cancel } },
                },
            },
        };

        async Task ExecuteAsync(Func<Task> action)
        {
            IsEnabled = false;
            error.Text = "";
            try { await action(); }
            catch (ApplicationDeploymentClientException failure) { error.Text = DeploymentText.Problem(failure.ProblemCode).Resolve(); }
            catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException or JsonException)
            { error.Text = Text("upgrade_request_failed"); }
            finally { IsEnabled = true; }
        }
    }

    private static string Text(string key) => LocalizedText.Get(Prefix + key);
}
