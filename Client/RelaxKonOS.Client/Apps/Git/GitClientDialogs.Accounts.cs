using RelaxKonOS.Client.Localization;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Git;

internal static partial class GitClientDialogs
{
    public static async Task ShowCredentialAccountsAsync(AppContext context, ManagedWindow owner, GitClientViewModel vm)
    {
        await context.ShowDialogAsync<bool>(owner, LocalizedText.Get("git.accounts.title"),
            _ => new Views.GitCredentialAccountsView { DataContext = vm },
            new RelaxKonOS.Core.Primitives.Size(760, 600));
    }
}
