using Avalonia.Controls;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Certificates.Views;

public partial class CertificateLoginRequiredView : UserControl
{
    public CertificateLoginRequiredView(ISystemLanguage language)
    {
        InitializeComponent();
        DataContext = language;
    }
}
