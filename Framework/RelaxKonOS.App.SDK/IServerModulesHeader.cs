using System.Windows.Input;

namespace RelaxKonOS.AppSDK;

public interface IServerModulesHeader
{
    bool HasServiceId { get; }
    string ServiceIdText { get; }
    bool IsReadOnly { get; }
    ICommand RefreshCommand { get; }
    bool Busy { get; }
    bool HasStatus { get; }
    string StatusText { get; }
    bool IsHostRecovery { get; }
    ICommand RestoreAllCommand { get; }
    bool HasModules { get; }
    int InstalledCount { get; }
    int EnabledCount { get; }
    int LoadedCount { get; }
    int PendingRestartCount { get; }
    IReadOnlyList<IServerModulesTabItem> VisibleTabs { get; }
    IServerModulesTabItem? SelectedTab { get; set; }
}

public interface IServerModulesTabItem
{
    string Label { get; }
}
