using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RelaxKonOS.Client.Services;
using static RelaxKonOS.Client.Services.AppPackages.BundledApplicationProvisioner;

namespace RelaxKonOS.Client.Views.Login;

internal sealed class InstallationProfileWindow : Window
{
    private readonly TaskCompletionSource<ProfileSelection> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal InstallationProfileWindow(ProfileDocument document, LoginLocalizationService language)
    {
        string Text(string key, string fallback) => language.Get("installation_profile." + key, fallback);
        Title = Text("title", "Choose desktop apps");
        Width = 520;
        Height = 560;
        MinWidth = 360;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var choices = document.Profiles.Keys.ToArray();
        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var profile in choices)
            picker.Items.Add(new ComboBoxItem { Content = Text(profile, profile) });
        var entries = new StackPanel { Spacing = 8 };
        var exclusions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Refresh()
        {
            if (picker.SelectedIndex < 0) return;
            var profile = choices[picker.SelectedIndex];
            if (!exclusions.TryGetValue(profile, out var excluded))
                exclusions[profile] = excluded = new(StringComparer.Ordinal);
            entries.Children.Clear();
            foreach (var entry in document.Profiles[profile])
            {
                var name = entry.AppId["relaxkonos.".Length..].Replace('-', ' ');
                var label = entry.InstallPolicy == "interactive"
                    ? string.Format(Text("in_store", "{0} · available in the app store"), name) : name;
                var checkbox = new CheckBox
                {
                    Content = new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    IsChecked = !excluded.Contains(entry.AppId),
                };
                checkbox.IsCheckedChanged += (_, _) =>
                {
                    if (checkbox.IsChecked == true) excluded.Remove(entry.AppId);
                    else excluded.Add(entry.AppId);
                };
                entries.Children.Add(checkbox);
            }
            if (entries.Children.Count == 0)
                entries.Children.Add(new TextBlock { Text = Text("empty", "You can install apps later from the app store.") });
        }
        picker.SelectionChanged += (_, _) => Refresh();
        picker.SelectedIndex = Array.IndexOf(choices, document.DefaultProfile);

        var apply = new Button { Content = Text("continue", "Continue"), IsDefault = true };
        apply.Click += (_, _) =>
        {
            if (picker.SelectedIndex < 0) return;
            var profile = choices[picker.SelectedIndex];
            completion.TrySetResult(new(profile, exclusions[profile].ToArray()));
            Close();
        };
        var skip = new Button { Content = Text("skip", "Skip preinstallation"), IsCancel = true };
        skip.Click += (_, _) => Close();
        Closed += (_, _) => completion.TrySetResult(new("minimal", []));
        var heading = new StackPanel
        {
            Spacing = 12,
            Children = { new TextBlock { Text = Title, FontSize = 24 }, picker },
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { skip, apply },
        };
        var layout = new DockPanel { Margin = new Thickness(24) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(actions);
        layout.Children.Add(new ScrollViewer { Content = entries, Margin = new Thickness(0, 12) });
        Content = layout;
    }

    internal Task<ProfileSelection> ShowSelectionAsync()
    {
        Show();
        return completion.Task;
    }
}
