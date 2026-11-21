using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.UI.TextEditing;

namespace RelaxKonOS.App.Notepad;

public sealed partial class NotepadViewModel : ObservableObject, IDisposable
{
    private readonly ISystemLanguage _language;
    private readonly Func<string, CancellationToken, Task<byte[]?>>? _read;
    private readonly Func<string, byte[], CancellationToken, Task>? _write;
    private readonly CancellationToken _closed;
    private bool _applyingDocument;
    private int _documentRevision;
    private string _statusKey = "notepad.status.ready";
    private object?[] _statusArguments = [];

    public NotepadViewModel(NotepadText labels, Func<string, CancellationToken, Task<byte[]?>>? read,
        Func<string, byte[], CancellationToken, Task>? write, string defaultEncoding = "UTF-8", CancellationToken closed = default)
    {
        _language = labels.Language;
        _read = read;
        _write = write;
        _closed = closed;
        DefaultEncodingName = TextFileEncodings.IsSupported(defaultEncoding) ? defaultEncoding : "UTF-8";
        EncodingName = DefaultEncodingName;
        Labels = labels;
        _language.LanguageChanged += OnLanguageChanged;
    }

    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private string _encodingName = "UTF-8";
    [ObservableProperty] private string _defaultEncodingName = "UTF-8";
    [ObservableProperty] private double _fontSize = 14;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDocument))]
    private bool _isSaving;

    public NotepadText Labels { get; private set; }
    public string Title => Labels["notepad.title"];
    public bool CanEditDocument => !IsSaving;
    public string StatusText => string.Format(Labels[_statusKey], _statusArguments);
    public string LineCountText => string.Format(Labels["common.line_count_format"], Text.Count(character => character == '\n') + 1);
    public string CharacterCountText => string.Format(Labels["common.character_count_format"], Text.Length);
    public string DocumentName => CurrentPath is null ? Labels["notepad.document.untitled"] : FileName(CurrentPath);
    public IReadOnlyList<string> AvailableEncodings => TextFileEncodings.Available;
    public IReadOnlyList<double> FontSizes { get; } = [12, 13, 14, 16, 18, 20];
    public bool HasOpenFile => CurrentPath is not null;

    public Func<Task<string?>>? RequestFileAsync { get; set; }
    public Func<string, Task<string?>>? RequestSavePathAsync { get; set; }
    public Func<Task<bool>>? RequestDiscardChangesAsync { get; set; }
    public Func<Task>? RequestSettingsAsync { get; set; }
    public Func<Task<EncodingDialogAction?>>? RequestEncodingActionAsync { get; set; }
    public Func<Task<string?>>? RequestEncodingAsync { get; set; }
    public Action? CloseSettingsAction { get; set; }
    public Func<string, Task>? SaveDefaultEncodingAsync { get; set; }

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(CharacterCountText));
        if (_applyingDocument) return;
        _documentRevision++;
        IsDirty = true;
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
    private async Task NewDocumentAsync()
    {
        if (!await CanReplaceAsync()) return;
        _documentRevision++;
        ApplyDocument(null, string.Empty, DefaultEncodingName);
        SetStatus("notepad.status.new_document");
    }

    [RelayCommand]
    private async Task OpenDocumentAsync()
    {
        if (IsSaving || RequestFileAsync is null) return;
        var revision = _documentRevision;
        try
        {
            if (await RequestFileAsync() is { } path && revision == _documentRevision)
                await OpenPathAsync(path);
        }
        catch (UnauthorizedAccessException) { SetStatus("notepad.permission_denied"); }
        catch (Exception error) { SetStatus("notepad.status.open_failed", error.Message); }
    }

    [RelayCommand]
    private Task SaveAsync() => SaveDocumentAsync(saveAs: false);

    [RelayCommand]
    private Task SaveAsAsync() => SaveDocumentAsync(saveAs: true);

    private async Task SaveDocumentAsync(bool saveAs)
    {
        if (IsSaving) return;
        if (_write is null) { SetStatus("notepad.status.connect_before_save"); return; }
        var revision = _documentRevision;
        var path = CurrentPath;
        try
        {
            if (saveAs || path is null)
                path = RequestSavePathAsync is null ? null : await RequestSavePathAsync(path is null ? "untitled.txt" : FileName(path));
            if (path is not null && revision == _documentRevision)
                await SaveToPathAsync(path, EncodingName);
        }
        catch (UnauthorizedAccessException) { SetStatus("notepad.permission_denied"); }
        catch (Exception error) { SetStatus("notepad.status.save_failed", error.Message); }
    }

    [RelayCommand]
    private async Task ChooseEncodingAsync()
    {
        if (IsSaving || CurrentPath is not { } path || RequestEncodingActionAsync is null || RequestEncodingAsync is null) return;
        var revision = _documentRevision;
        var action = await RequestEncodingActionAsync();
        if (action is null || revision != _documentRevision || IsSaving) return;
        var encoding = await RequestEncodingAsync();
        if (encoding is null || !TextFileEncodings.IsSupported(encoding) || revision != _documentRevision || IsSaving) return;
        if (action == EncodingDialogAction.Reopen)
            await OpenPathAsync(path, encoding);
        else
            await SaveToPathAsync(path, encoding);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (RequestSettingsAsync is not null) await RequestSettingsAsync();
    }

    [RelayCommand]
    private void CloseSettings() => CloseSettingsAction?.Invoke();

    public async Task OpenPathAsync(string path, string? requestedEncoding = null)
    {
        if (_read is null) { SetStatus("notepad.status.connect_before_open"); return; }
        if (!await CanReplaceAsync()) return;
        var revision = ++_documentRevision;
        try
        {
            var bytes = await _read(path, _closed);
            if (revision != _documentRevision || _closed.IsCancellationRequested || IsSaving) return;
            if (bytes is null) { SetStatus("notepad.status.file_missing"); return; }
            var encoding = requestedEncoding ?? DefaultEncodingName;
            ApplyDocument(path, TextFileEncodings.Decode(bytes, encoding), encoding);
            SetStatus("notepad.status.opened", FileName(path), encoding);
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) when (revision == _documentRevision) { SetStatus("notepad.permission_denied"); }
        catch (Exception error)
        {
            if (revision == _documentRevision) SetStatus("notepad.status.open_failed", error.Message);
        }
    }

    private async Task SaveToPathAsync(string path, string encoding)
    {
        if (_write is null) { SetStatus("notepad.status.connect_before_save"); return; }
        if (IsSaving || _closed.IsCancellationRequested) return;
        var revision = ++_documentRevision;
        IsSaving = true;
        try
        {
            await _write(path, TextFileEncodings.Encode(Text, encoding), _closed);
            if (revision == _documentRevision && !_closed.IsCancellationRequested)
            {
                CurrentPath = path;
                EncodingName = encoding;
                IsDirty = false;
                SetStatus("notepad.status.saved", FileName(path), encoding);
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { SetStatus("notepad.permission_denied"); }
        catch (Exception error) { SetStatus("notepad.status.save_failed", error.Message); }
        finally { IsSaving = false; }
    }

    public async Task<bool> CanReplaceAsync()
    {
        if (IsSaving || _closed.IsCancellationRequested) return false;
        if (!IsDirty) return true;
        var revision = _documentRevision;
        return RequestDiscardChangesAsync is not null && await RequestDiscardChangesAsync()
            && revision == _documentRevision && !IsSaving && !_closed.IsCancellationRequested;
    }

    private void ApplyDocument(string? path, string text, string encoding)
    {
        _applyingDocument = true;
        try
        {
            Text = text;
            CurrentPath = path;
            EncodingName = encoding;
            IsDirty = false;
        }
        finally { _applyingDocument = false; }
    }

    private static string FileName(string path) => Path.GetFileName(path.Replace('\\', '/'));

    private void SetStatus(string key, params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshText();
        else Dispatcher.UIThread.Post(RefreshText);
    }

    private void RefreshText()
    {
        Labels = new NotepadText(_language);
        OnPropertyChanged(nameof(Labels));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(CharacterCountText));
    }

    public void Dispose() => _language.LanguageChanged -= OnLanguageChanged;
}
