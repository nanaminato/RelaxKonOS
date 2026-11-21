using Avalonia.Controls;
using Avalonia.Data;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Controls;

internal static class CompactWorkspaceNavigation
{
    public static void Install(UserControl view, Grid workspace) =>
        WindowManager.CompactWorkspaceNavigation.Install(view, workspace,
            (Binding)new LocExtension("common.view").ProvideValue(null!));
}
