using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Git;

public sealed partial class GitClientDialogs
{
    public async Task ShowCredentialAccountsAsync(ManagedWindow owner, GitClientViewModel vm)
    {
        await presenter.ShowDialogAsync<bool>(owner, localize("git.accounts.title"),
            _ => new Views.GitCredentialAccountsView { DataContext = vm },
            new RelaxKonOS.Core.Primitives.Size(760, 600));
    }
}
