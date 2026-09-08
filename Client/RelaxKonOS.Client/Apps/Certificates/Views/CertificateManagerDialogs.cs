using RelaxKonOS.AppSDK;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Certificates.Views;

/// <summary>Opens Certificate Manager modal workflows at their intended sizes.</summary>
internal static class CertificateManagerDialogs
{
    public static Task ShowRequestCertificateAsync(AppContext context, ManagedWindow owner, CertificateManagerViewModel viewModel) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("certificates.request.title"),
            dialog => new CertificateRequestDialogView(viewModel, dialog),
            new RelaxKonOS.Core.Primitives.Size(620, 620));

    public static Task ShowCreateSelfSignedCertificateAsync(AppContext context, ManagedWindow owner, CertificateManagerViewModel viewModel) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("certificates.self_signed.title"),
            dialog => new SelfSignedCertificateDialogView(viewModel, dialog),
            new RelaxKonOS.Core.Primitives.Size(560, 430));
}
