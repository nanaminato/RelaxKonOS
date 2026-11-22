using RelaxKonOS.AppSDK;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RelaxKonOS.UI.SshFiles;

internal partial class SshFileNameDialog : Window
{
    public SshLabels Labels { get; }

    public SshFileNameDialog(string title, string initial, ISystemLanguage language)
    {
        Labels = new SshLabels(language);
        InitializeComponent();
        Title = title;
        NameEditor.Text = initial;
        Opened += (_, _) => { NameEditor.Focus(); NameEditor.SelectAll(); };
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => Close(NameEditor.Text);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void NameEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Close(NameEditor.Text); }
        else if (e.Key == Key.Escape) { e.Handled = true; Close(null); }
    }
}
