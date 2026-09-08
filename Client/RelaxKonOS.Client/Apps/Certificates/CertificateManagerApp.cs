using RelaxKonOS.Client.Apps.Certificates.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Certificates;

/// <summary>Built-in TLS certificate manager. Host-global ACME issuance and Kestrel deployment.</summary>
public sealed class CertificateManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("remoteos.certificates"), "Certificate Manager", "0.1.0", "🔐", "Manage TLS certificates on the RelaxKonOS Server",
        [AppPermissions.ServerCertificatesRead, AppPermissions.ServerCertificatesManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteCertificateClient)) as IRemoteCertificateClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.certificates.display_name"),
                new CertificateLoginRequiredView(),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var viewModel = new CertificateManagerViewModel(client, session, context.Permissions);
        ManagedWindow? window = null;
        var view = CertificateManagerWorkspace.Create(
            viewModel,
            () => CertificateManagerDialogs.ShowRequestCertificateAsync(context, window!, viewModel),
            () => CertificateManagerDialogs.ShowCreateSelfSignedCertificateAsync(context, window!, viewModel));
        window = context.ShowWindow(LocalizedText.Get("application.remoteos.certificates.display_name"),
            view, new Rect(60, 50, 1180, 780), Manifest.IconGlyph);
        _ = viewModel.StartAsync();
    }
}
