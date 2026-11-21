using Avalonia.Controls;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Databases.Views;

public static class DatabaseWorkspaceLanguage
{
    internal const string CultureResource = "DatabaseWorkspaceCulture";

    public static void Bind(Control root, ISystemLanguage language)
    {
        var resources = new ResourceDictionary();
        root.Resources.MergedDictionaries.Add(resources);
        DatabaseDialogLanguage.Bind(root, language, _ =>
        {
            var next = new ResourceDictionary { [CultureResource] = language.CurrentLanguage };
            foreach (var (key, message) in DatabaseLocalization.Labels(language.CurrentLanguage))
                next.Add(key, message);
            var dictionaries = root.Resources.MergedDictionaries;
            dictionaries[dictionaries.IndexOf(resources)] = next;
            resources = next;
        });
    }
}
