using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.App.Settings.Views;

/// <summary>Explicit selection of the data categories that Settings may clear for one app.</summary>
public sealed partial class AppDataClearDialogViewModel : ObservableObject
{
    private readonly Action<AppDataClearSelection?> _complete;

    public AppDataClearDialogViewModel(string title, Func<string, string> labels, Action<AppDataClearSelection?> complete)
    {
        Title = title;
        Labels = labels;
        _complete = complete;
    }

    public string Title { get; }
    public Func<string, string> Labels { get; }
    [ObservableProperty] private bool _clearPermissionDecisions;
    [ObservableProperty] private bool _clearServerData;

    [RelayCommand]
    private void Clear() => _complete(new AppDataClearSelection(ClearPermissionDecisions, ClearServerData));

    [RelayCommand]
    private void Cancel() => _complete(null);
}
