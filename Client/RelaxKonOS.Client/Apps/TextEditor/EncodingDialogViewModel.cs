using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.TextEditor;

/// <summary>显示可用编码并返回用户确认的选择。</summary>
public sealed partial class EncodingDialogViewModel : ObservableObject
{
    private readonly Action<string?> _complete;

    public EncodingDialogViewModel(string currentEncoding, Action<string?> complete)
    {
        _complete = complete;
        SelectedEncoding = TextFileEncodings.IsSupported(currentEncoding)
            ? currentEncoding
            : TextFileEncodings.Available[0];
    }

    public IReadOnlyList<string> AvailableEncodings => TextFileEncodings.Available;

    [ObservableProperty] private string _selectedEncoding = "UTF-8";

    [RelayCommand]
    private void Select() => _complete(SelectedEncoding);

    [RelayCommand]
    private void Cancel() => _complete(null);
}
