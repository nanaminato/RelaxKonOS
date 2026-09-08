using System.Globalization;
using Avalonia.Data.Converters;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.Explorer.Dialogs;

/// <summary>文本输入对话框 VM。Confirm 用 <see cref="Confirm"/> 回调返回输入文本；Cancel 返回 null。
/// 用于：新建文件夹（输入名称）、重命名（输入新名称）、复制/移动（输入目标路径）。</summary>
public partial class TextInputDialogViewModel : ObservableObject
{
    private readonly Action<string?> _confirm;
    private readonly Func<string?, bool>? _validate;

    public TextInputDialogViewModel(string prompt, string defaultValue, Action<string?> confirm,
        string? confirmLabel = null, Func<string?, bool>? validate = null)
    {
        _confirm = confirm;
        _validate = validate;
        Prompt = prompt;
        _input = defaultValue;
        ConfirmLabel = confirmLabel ?? LocalizedText.Get("common.ok");
    }

    [ObservableProperty] private string _input = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    public string Prompt { get; }
    public string ConfirmLabel { get; }

    [RelayCommand]
    private void Confirm()
    {
        if (_validate is not null && !_validate(Input))
        {
            ErrorMessage = LocalizedText.Get("explorer.input_invalid");
            return;
        }
        _confirm(Input);
    }

    [RelayCommand]
    private void Cancel() => _confirm(null);
}

/// <summary>非空字符串→可见；空/null→不可见。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public static readonly StringToVisibilityConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrEmpty(value as string);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
