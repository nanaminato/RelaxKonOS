using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Apps.Settings.Views.Pages;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;

internal static class LanguageSwitchChecks
{
    public static void Run(ShellSettings settings, LocalizationService localization)
    {
        var original = settings.Language;
        var session = DispatchProxy.Create<IAuthSession, LanguageSessionStub>();
        var saves = 0;
        using var vm = new TimeLanguagePageViewModel(settings, localization, () => saves++,
            new HostTimeEditorViewModel(null!, session, localization));
        var view = new TimeLanguagePageView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var combo = view.GetVisualDescendants().OfType<ComboBox>()
            .Single(control => ReferenceEquals(control.ItemsSource, vm.LanguageOptions));
        foreach (var culture in new[] { "en-US", "ja-JP", "zh-CN", "ja-JP", "en-US" })
        {
            combo.SelectedItem = vm.LanguageOptions.Single(option => option.Culture == culture);
            Dispatcher.UIThread.RunJobs();
            if (settings.Language != culture || localization.CurrentLanguage != culture
                || (combo.SelectedItem as SystemLanguageOption)?.Culture != culture)
                throw new Exception($"Language selection reset: requested={culture}, actual={settings.Language}, effective={localization.CurrentLanguage}");
            var expected = localization.Get("settings.language_region.section", "Language & region");
            if (!view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == expected))
                throw new Exception("Open settings view did not refresh translated text.");
            Console.WriteLine($"PASS: Settings language {culture} applies immediately and survives option refresh.");
        }
        if (saves < 4) throw new Exception("Language edits did not schedule saves.");
        window.Close();
        settings.Language = original;
        Dispatcher.UIThread.RunJobs();
    }
}

public class LanguageSessionStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
}
