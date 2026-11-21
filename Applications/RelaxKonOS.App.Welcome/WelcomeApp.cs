using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.App.Welcome;

public sealed class WelcomeApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.welcome"), "Welcome",
        "1.0.0", "🏠", "Get started with RelaxKonOS");

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = new WelcomeView();
        var language = context.SystemLanguage;
        var window = context.Windows.ShowWindow(view.Localize(language.CurrentLanguage), view,
            bounds: new Rect(120, 80, 720, 480), iconGlyph: Manifest.IconGlyph);
        var closed = window.Closed;
        view.NotebookButton.Click += (_, _) =>
        {
            var opened = context.Activations.Activate(RelaxKonOSActivationUris.LaunchApplication(new AppId("relaxkonos.notepad")));
            if (!opened.IsAccepted)
                context.Activations.Activate(RelaxKonOSActivationUris.LaunchApplication(new AppId("relaxkonos.appinstaller")));
        };

        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args)
        {
            void Refresh()
            {
                if (!closed.IsCancellationRequested)
                    window.Window.Title = view.Localize(args.CurrentLanguage);
            }

            if (Dispatcher.UIThread.CheckAccess()) Refresh();
            else Dispatcher.UIThread.Post(Refresh);
        }

        language.LanguageChanged += LanguageChanged;
        closed.Register(() => language.LanguageChanged -= LanguageChanged);
        return Task.CompletedTask;
    }
}

public sealed partial class WelcomeView : UserControl
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public WelcomeView() => InitializeComponent();

    internal Button NotebookButton => this.FindControl<Button>("OpenNotebookButton")!;

    internal string Localize(string culture)
    {
        var language = culture.Split('-', 2)[0].ToLowerInvariant() switch
        {
            "ja" => "ja-JP",
            "zh" => "zh-CN",
            _ => "en-US"
        };
        using var stream = typeof(WelcomeView).Assembly.GetManifestResourceStream($"Welcome.Localization.{language}.json")!;
        var text = JsonSerializer.Deserialize<LanguageFile>(stream, Json)!.Strings;
        foreach (var control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.Tag is not string key || !text.TryGetValue(key, out var value)) continue;
            if (control is TextBlock label) label.Text = value;
            else if (control is Button button) button.Content = value;
        }
        return text["welcome.window_title"];
    }

    private sealed record LanguageFile(Dictionary<string, string> Strings);
}
