using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.ApplicationDeployments.Views;

public partial class ApplicationDeploymentsLoginRequiredView : UserControl
{
    public ApplicationDeploymentsLoginRequiredView() => InitializeComponent();

    public ApplicationDeploymentsLoginRequiredView(ISystemLanguage language) : this()
    {
        void Refresh() => Notice.Text = ApplicationDeploymentsLocalization.Resolve(
            language, "application_deployments.login_required", "application_deployments.login_required");

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
