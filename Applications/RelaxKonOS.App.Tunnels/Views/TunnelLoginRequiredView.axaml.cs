using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Tunnels.Views;

public partial class TunnelLoginRequiredView : UserControl
{
    public TunnelLoginRequiredView() => InitializeComponent();

    public TunnelLoginRequiredView(Func<string, string> localize, ISystemLanguage language) : this()
    {
        void Refresh() => Notice.Text = localize("tunnels.login_required");

        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args)
        {
            if (Dispatcher.UIThread.CheckAccess()) Refresh();
            else Dispatcher.UIThread.Post(Refresh);
        }

        Refresh();
        AttachedToVisualTree += (_, _) =>
        {
            language.LanguageChanged += LanguageChanged;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) => language.LanguageChanged -= LanguageChanged;
    }
}
