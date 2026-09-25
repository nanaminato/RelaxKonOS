using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Services;
using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client.Views.ServerCenter;

public partial class ServerCenterWindow : Window
{
    public ServerCenterWindow()
    {
        InitializeComponent();
        var localization = App.Services.GetRequiredService<LoginLocalizationService>();
        void RefreshTitle() => Title = localization.Get("server_center.title", "Server centre");
        localization.LanguageChanged += (_, _) => RefreshTitle();
        RefreshTitle();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
