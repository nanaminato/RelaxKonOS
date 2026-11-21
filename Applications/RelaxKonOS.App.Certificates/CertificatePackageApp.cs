using RelaxKonOS.App.Certificates.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Certificates;

public sealed class CertificatePackageApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.certificates"),
        "Certificate Manager", "1.0.0", "🔐", "Manage TLS certificates on the RelaxKonOS Server",
        [AppPermissions.ServerCertificatesRead, AppPermissions.ServerCertificatesManage],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Certificates]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var title = Text("certificates.title");
        if (!context.HasAuthenticatedServer)
        {
            context.Windows.ShowWindow(title, new CertificateLoginRequiredView(context.SystemLanguage),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var model = new CertificateManagerViewModel(context.Certificates, context.Permissions, context.SystemLanguage);
        IExternalAppWindowHandle window = null!;
        var workspace = CertificateManagerWorkspace.Create(model,
            () => window.ShowDialogAsync<bool>(Text("certificates.request.title"),
                dialog => new CertificateRequestDialogView(model, dialog, AttachDraftGuard), new Size(620, 620)),
            () => window.ShowDialogAsync<bool>(Text("certificates.self_signed.title"),
                dialog => new SelfSignedCertificateDialogView(model, dialog, AttachDraftGuard), new Size(560, 430)),
            () =>
            {
                if (model.SelectedCertificate is { } certificate)
                    context.Windows.ShowWindow($"{Text("certificates.renewal.title")} · {certificate.PrimaryDomain}",
                        new CertificateRenewalHistoryView(model, certificate.Id), new Rect(180, 140, 760, 540), Manifest.IconGlyph);
            });
        window = context.Windows.ShowWindow(title, workspace, new Rect(60, 50, 1180, 780), Manifest.IconGlyph);
        workspace.AttachWindowHeader(window.Window);
        await model.StartAsync();

        string Text(string key) => CertificateLocalization.Resolve(context.SystemLanguage, key);

        void AttachDraftGuard(ModalDialog<bool> dialog, Func<bool> isBusy, Func<bool> hasChanges)
        {
            dialog.CanCancelAsync = async () =>
            {
                if (isBusy()) return false;
                if (!hasChanges()) return true;
                var discard = await window.ConfirmNestedAsync(dialog, Text("certificates.discard_title"),
                    Text("certificates.discard_message"), Text("certificates.discard"));
                return discard && !isBusy();
            };
        }
    }
}
