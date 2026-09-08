using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Client.Apps.Explorer.Dialogs;

public sealed record OpenWithChoice(string ApplicationId, bool SetAsDefault);

/// <summary>选择支持文件打开的应用，并可将选择保存为此扩展名的默认程序。</summary>
public sealed partial class OpenWithDialogViewModel : ObservableObject
{
    private readonly Action<OpenWithChoice?> _complete;

    public OpenWithDialogViewModel(IReadOnlyList<ApplicationInfo> applications, string extension,
        Action<OpenWithChoice?> complete)
    {
        Applications = applications;
        Extension = extension;
        _complete = complete;
        SelectedApplication = applications.FirstOrDefault();
    }

    public IReadOnlyList<ApplicationInfo> Applications { get; }
    public string Extension { get; }
    [ObservableProperty] private ApplicationInfo? _selectedApplication;
    [ObservableProperty] private bool _setAsDefault;

    [RelayCommand]
    private void Open()
    {
        if (SelectedApplication is not null)
            _complete(new OpenWithChoice(SelectedApplication.Id.Value, SetAsDefault));
    }

    [RelayCommand]
    private void Cancel() => _complete(null);
}
