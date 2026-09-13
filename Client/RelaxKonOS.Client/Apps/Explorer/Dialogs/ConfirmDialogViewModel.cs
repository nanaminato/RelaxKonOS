using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Explorer.Dialogs;

/// <summary>确认对话框 VM。Yes 返回 true；No 返回 false。用于删除等危险操作确认。</summary>
public partial class ConfirmDialogViewModel : ObservableObject
{
    private readonly Action<bool> _complete;

    public ConfirmDialogViewModel(string message, Action<bool> complete, string? confirmLabel = null, string? cancelLabel = null)
    {
        _complete = complete;
        Message = message;
        ConfirmLabel = confirmLabel ?? LocalizedText.Get("common.ok");
        CancelLabel = cancelLabel ?? LocalizedText.Get("common.cancel");
    }

    [ObservableProperty] private string _message = string.Empty;
    public string ConfirmLabel { get; }
    public string CancelLabel { get; }

    [RelayCommand]
    private void Yes() => _complete(true);

    [RelayCommand]
    private void No() => _complete(false);
}
