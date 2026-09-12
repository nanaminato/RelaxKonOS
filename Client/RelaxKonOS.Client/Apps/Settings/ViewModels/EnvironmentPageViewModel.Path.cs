using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed partial class EnvironmentPageViewModel
{
    [ObservableProperty] private IReadOnlyList<string> _pathEntries = Array.Empty<string>();
    [ObservableProperty] private int _selectedPathIndex = -1;
    [ObservableProperty] private string _pathEntryValue = "";
    [ObservableProperty] private IReadOnlyList<string> _draftNames = Array.Empty<string>();
    [ObservableProperty] private string? _selectedDraftName;
    public bool IsPath => _snapshot is not null && VariableName.Equals("PATH",
        _snapshot.CaseSensitiveNames ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    public bool CanEditPath => CanEdit && IsPath;
    public bool CanReplacePathEntry => CanEditPath && SelectedPathIndex >= 0 && SelectedPathIndex < PathEntries.Count;
    public bool CanMovePathUp => CanReplacePathEntry && SelectedPathIndex > 0;
    public bool CanMovePathDown => CanReplacePathEntry && SelectedPathIndex < PathEntries.Count - 1;
    public bool CanEditDraft => CanEdit && SelectedDraftName is not null;
    public string PathWarnings => !IsPath ? "" : string.Join(Environment.NewLine,
        EnvironmentExpansion.PathWarnings(VariableValue, !_snapshot!.CaseSensitiveNames).Select(key => T(key, key)));
    partial void OnVariableNameChanged(string value) => RefreshPath();
    partial void OnVariableValueChanged(string value) => RefreshPath();
    partial void OnSelectedPathIndexChanged(int value)
    {
        PathEntryValue = value >= 0 && value < PathEntries.Count ? PathEntries[value] : "";
        UpdatePathCommands();
    }
    partial void OnSelectedDraftNameChanged(string? value) => UpdatePathCommands();
    private void RefreshPath()
    {
        PathEntries = IsPath ? EnvironmentExpansion.SplitPath(VariableValue, !_snapshot!.CaseSensitiveNames) : Array.Empty<string>();
        SelectedPathIndex = -1; PathEntryValue = "";
        OnPropertyChanged(nameof(IsPath)); OnPropertyChanged(nameof(PathWarnings)); UpdatePathCommands();
    }
    private void SetPath(IReadOnlyList<string> entries, int selected)
    {
        var raw = string.Join(_snapshot!.PathSeparator, entries);
        if (raw.Length > EnvironmentValidation.MaximumValueLength) { ProblemCode = "settings.environment.invalid_value"; return; }
        VariableValue = raw;
        // The last removed segment leaves an empty string, which is an empty PATH, not variable deletion.
        RefreshPath();
        SelectedPathIndex = Math.Min(selected, PathEntries.Count - 1);
    }
    private bool ValidPathEntry()
    {
        if (!PathEntryValue.Contains(_snapshot!.PathSeparator, StringComparison.Ordinal)) return true;
        ProblemCode = T("settings.environment.path_separator", "An entry cannot contain the remote PATH separator.");
        return false;
    }
    [RelayCommand(CanExecute = nameof(CanEditPath))]
    private void AddPathEntry()
    {
        if (!ValidPathEntry()) return;
        var entries = PathEntries.Append(PathEntryValue).ToArray(); SetPath(entries, entries.Length - 1);
    }
    [RelayCommand(CanExecute = nameof(CanReplacePathEntry))]
    private void ReplacePathEntry()
    {
        if (!ValidPathEntry()) return;
        var index = SelectedPathIndex; var entries = PathEntries.ToArray(); entries[index] = PathEntryValue; SetPath(entries, index);
    }
    [RelayCommand(CanExecute = nameof(CanReplacePathEntry))]
    private void RemovePathEntry()
    {
        var index = SelectedPathIndex; var entries = PathEntries.ToList(); entries.RemoveAt(index); SetPath(entries, index);
    }
    [RelayCommand(CanExecute = nameof(CanMovePathUp))]
    private void MovePathUp() => MovePath(-1);
    [RelayCommand(CanExecute = nameof(CanMovePathDown))]
    private void MovePathDown() => MovePath(1);
    private void MovePath(int delta)
    {
        var index = SelectedPathIndex; var entries = PathEntries.ToArray();
        (entries[index], entries[index + delta]) = (entries[index + delta], entries[index]); SetPath(entries, index + delta);
    }
    private void RefreshDraft()
    {
        DraftNames = _draft.Select(item => item.Name).ToArray(); SelectedDraftName = null;
        DraftText = string.Join(Environment.NewLine, _draft.Select(item => item.Name + " · " +
            T(item.Operation == EnvironmentMutationKind.Delete ? "settings.environment.delete" : "settings.environment.set", item.Operation.ToString())));
    }
    [RelayCommand(CanExecute = nameof(CanEditDraft))]
    private void EditDraft()
    {
        var item = _draft.Single(item => item.Name == SelectedDraftName);
        VariableName = item.Name; VariableValue = item.Value ?? "";
        ExpandString = item.ValueKind == EnvironmentValueKind.ExpandString;
    }
    [RelayCommand(CanExecute = nameof(CanEditDraft))]
    private void RemoveDraft()
    {
        _draft.RemoveAll(item => item.Name == SelectedDraftName);
        _plan = null; PreviewText = ""; RefreshDraft(); Update();
    }
    private void UpdatePathCommands()
    {
        OnPropertyChanged(nameof(CanEditPath));
        AddPathEntryCommand.NotifyCanExecuteChanged(); ReplacePathEntryCommand.NotifyCanExecuteChanged();
        RemovePathEntryCommand.NotifyCanExecuteChanged(); MovePathUpCommand.NotifyCanExecuteChanged(); MovePathDownCommand.NotifyCanExecuteChanged();
        EditDraftCommand.NotifyCanExecuteChanged(); RemoveDraftCommand.NotifyCanExecuteChanged();
    }
}
