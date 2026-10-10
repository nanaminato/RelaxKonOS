using Avalonia.Controls;

using RelaxKonOS.Client.Services;
using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client.Views.Login;

/// <summary>登录顶层窗口（mstsc 风格独立窗口）。登录成功后由 App 关闭并打开 MainWindow 桌面。</summary>
public partial class LoginWindow : Window
{
    private bool _desktopHandoff;

    public void CloseForDesktop()
    {
        _desktopHandoff = true;
        Close();
    }

    public LoginWindow()
    {
        InitializeComponent();
        var localization = App.Services.GetRequiredService<LoginLocalizationService>();
        void RefreshTitle() => Title = localization.Get("login.title", "RelaxKonOS");
        void LanguageChanged(object? sender, EventArgs args) => RefreshTitle();
        localization.LanguageChanged += LanguageChanged;
        Closed += (_, _) =>
        {
            localization.LanguageChanged -= LanguageChanged;
            if (!_desktopHandoff && DataContext is RelaxKonOS.Client.ViewModels.Login.LoginViewModel login)
                login.CancelWindowOperations();
        };
        RefreshTitle();
    }
}
