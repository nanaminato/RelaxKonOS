using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Firewall.Views;

/// <summary>The rule dialog owns the draft and the submit decision; the page only reads it back.</summary>
public partial class FirewallRuleEditorView : UserControl
{
    public FirewallRuleEditorView() => InitializeComponent();

    public FirewallRuleEditorView(FirewallViewModel viewModel, RelaxKonOS.WindowManager.ModalDialog<bool> dialog) : this()
    {
        DataContext = viewModel;
        CancelButton.Click += (_, _) => dialog.Cancel();
        SubmitButton.Click += async (_, _) =>
        {
            if (await viewModel.SubmitDraftAsync()) dialog.Close(true);
        };
    }
}
