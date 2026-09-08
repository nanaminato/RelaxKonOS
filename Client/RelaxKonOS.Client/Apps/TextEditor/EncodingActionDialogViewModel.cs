using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.TextEditor;

/// <summary>第一步：选择以指定编码重新打开，或以指定编码保存。</summary>
public sealed partial class EncodingActionDialogViewModel : ObservableObject
{
    private readonly Action<EncodingDialogAction?> _complete;

    public EncodingActionDialogViewModel(Action<EncodingDialogAction?> complete) => _complete = complete;

    [RelayCommand]
    private void Reopen() => _complete(EncodingDialogAction.Reopen);

    [RelayCommand]
    private void Save() => _complete(EncodingDialogAction.Save);

    [RelayCommand]
    private void Cancel() => _complete(null);
}

public enum EncodingDialogAction
{
    Reopen,
    Save,
}
