using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.TextEditor;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.Notepad;

/// <summary>RelaxKonOS 的基础文本编辑器。文件内容始终通过远程文件 API 打开和保存。</summary>
public sealed partial class NotepadViewModel : ObservableObject
{
    private readonly IExplorerClient? _files;
    private bool _isLoading;

    public NotepadViewModel(IExplorerClient? files, string defaultEncodingName = "UTF-8")
    {
        _files = files;
        DefaultEncodingName = TextFileEncodings.IsSupported(defaultEncodingName) ? defaultEncodingName : "UTF-8";
        EncodingName = DefaultEncodingName;
    }

    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private string _encodingName = "UTF-8";
    [ObservableProperty] private string _defaultEncodingName = "UTF-8";
    [ObservableProperty] private double _fontSize = 14;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private LocalizedStatus _statusText;

    public int CharCount => Text.Length;
    public int LineCount => string.IsNullOrEmpty(Text) ? 1 : Enumerable.Count<char>(Text, c => c == '\n') + 1;
    public string LineCountText => LocalizedText.Format("common.line_count_format", LineCount);
    public string CharacterCountText => LocalizedText.Format("common.character_count_format", CharCount);
    public string DocumentName => string.IsNullOrWhiteSpace(CurrentPath) ? LocalizedText.Get("notepad.document.untitled") : Path.GetFileName(CurrentPath) ?? LocalizedText.Get("notepad.document.untitled");
    public IReadOnlyList<string> AvailableEncodings => TextFileEncodings.Available;
    public IReadOnlyList<double> FontSizes { get; } = [12, 13, 14, 16, 18, 20];
    public bool HasOpenFile => !string.IsNullOrWhiteSpace(CurrentPath);

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(CharCount));
        OnPropertyChanged(nameof(LineCount));
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(CharacterCountText));
        if (!_isLoading) IsDirty = true;
    }

    partial void OnCurrentPathChanged(string? value)
    {
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(HasOpenFile));
    }

    partial void OnDefaultEncodingNameChanged(string value)
    {
        if (TextFileEncodings.IsSupported(value))
            _ = SaveDefaultEncodingAsync?.Invoke(value);
    }

    [RelayCommand]
    private void NewDocument()
    {
        _isLoading = true;
        Text = string.Empty;
        CurrentPath = null;
        EncodingName = DefaultEncodingName;
        IsDirty = false;
        StatusText = LocalizedText.Ref("notepad.status.new_document");
        _isLoading = false;
    }

    public Func<Task<string?>>? RequestFileAsync { get; set; }
    public Func<string, Task<string?>>? RequestSavePathAsync { get; set; }
    public Func<Task<bool>>? RequestDiscardChangesAsync { get; set; }
    public Func<Task>? RequestSettingsAsync { get; set; }
    public Func<Task<EncodingDialogAction?>>? RequestEncodingActionAsync { get; set; }
    public Func<Task<string?>>? RequestEncodingAsync { get; set; }
    public Action? CloseSettingsAction { get; set; }
    public Func<string, Task>? SaveDefaultEncodingAsync { get; set; }

    [RelayCommand]
    private async Task OpenDocumentAsync()
    {
        var path = await (RequestFileAsync?.Invoke() ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path)) await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var path = CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
            path = await (RequestSavePathAsync?.Invoke("untitled.txt") ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path)) await SaveToPathAsync(path);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var suggestedName = string.IsNullOrWhiteSpace(CurrentPath) ? "untitled.txt" : Path.GetFileName(CurrentPath) ?? "untitled.txt";
        var path = await (RequestSavePathAsync?.Invoke(suggestedName) ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path)) await SaveToPathAsync(path);
    }

    [RelayCommand]
    private async Task ChooseEncodingAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath)) return;
        var action = await (RequestEncodingActionAsync?.Invoke() ?? Task.FromResult<EncodingDialogAction?>(null));
        if (action is null) return;
        var encoding = await (RequestEncodingAsync?.Invoke() ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(encoding)) return;
        if (action == EncodingDialogAction.Reopen)
            await ReopenWithEncodingAsync(encoding);
        else
            await SaveWithEncodingAsync(encoding);
    }

    private async Task ReopenWithEncodingAsync(string encodingName)
    {
        if (string.IsNullOrWhiteSpace(CurrentPath) || !TextFileEncodings.IsSupported(encodingName)) return;
        if (IsDirty && !(await (RequestDiscardChangesAsync?.Invoke() ?? Task.FromResult(false)))) return;
        EncodingName = encodingName;
        await OpenPathAsync(CurrentPath, encodingName);
    }

    private async Task SaveWithEncodingAsync(string encodingName)
    {
        if (string.IsNullOrWhiteSpace(CurrentPath) || !TextFileEncodings.IsSupported(encodingName)) return;
        EncodingName = encodingName;
        await SaveToPathAsync(CurrentPath);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
        => await (RequestSettingsAsync?.Invoke() ?? Task.CompletedTask);

    [RelayCommand]
    private void CloseSettings() => CloseSettingsAction?.Invoke();

    public async Task OpenPathAsync(string path, string? requestedEncoding = null)
    {
        if (_files is null) { StatusText = LocalizedText.Ref("notepad.status.connect_before_open"); return; }
        try
        {
            var bytes = await _files.ReadFileAsync(path);
            if (bytes is null) { StatusText = LocalizedText.Ref("notepad.status.file_missing"); return; }
            var encoding = requestedEncoding ?? DefaultEncodingName;
            _isLoading = true;
            Text = TextFileEncodings.Decode(bytes, encoding);
            EncodingName = encoding;
            CurrentPath = path;
            IsDirty = false;
            StatusText = LocalizedText.Ref("notepad.status.opened", Path.GetFileName(path), encoding);
        }
        catch (Exception ex) { StatusText = LocalizedText.Ref("notepad.status.open_failed", ex.Message); }
        finally { _isLoading = false; }
    }

    private async Task SaveToPathAsync(string path)
    {
        if (_files is null) { StatusText = LocalizedText.Ref("notepad.status.connect_before_save"); return; }
        try
        {
            await _files.WriteFileAsync(path, TextFileEncodings.Encode(Text, EncodingName));
            CurrentPath = path;
            IsDirty = false;
            StatusText = LocalizedText.Ref("notepad.status.saved", Path.GetFileName(path), EncodingName);
        }
        catch (Exception ex) { StatusText = LocalizedText.Ref("notepad.status.save_failed", ex.Message); }
    }

}
