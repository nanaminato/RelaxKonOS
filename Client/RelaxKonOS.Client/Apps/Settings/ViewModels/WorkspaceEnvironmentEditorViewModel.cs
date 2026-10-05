using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Explicit batch editing, retaining the baseline on failure and never silently replaying writes.</summary>
public sealed partial class WorkspaceEnvironmentEditorViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceEnvironmentClient _client;
    private readonly IAuthSession _session;
    private readonly Dictionary<string, EnvironmentMutation> _draft;
    private readonly bool _windows;
    private WorkspaceEnvironmentConnection? _connection;
    private WorkspaceEnvironmentSnapshot? _snapshot;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private bool _uncertain;

    public WorkspaceEnvironmentEditorViewModel(IWorkspaceEnvironmentClient client, IAuthSession session)
    {
        _client = client; _session = session;
        _windows = session.CurrentServer?.Platform == RelaxKonOS.Protocol.Common.HostPlatformKind.Windows;
        _draft = new(_windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        session.StateChanged += SessionChanged;
    }

    public Action? RequestClose { get; set; }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private IReadOnlyList<EnvironmentVariable> _variables = [];
    [ObservableProperty] private EnvironmentVariable? _selectedVariable;
    [ObservableProperty] private string _variableName = "";
    [ObservableProperty] private string _variableValue = "";
    [ObservableProperty] private bool _appendPath = true;
    [ObservableProperty] private bool _confirmHighImpact;
    [ObservableProperty] private LocalizedStatus _status = "";
    public bool CanEdit => !_disposed && !IsBusy && !_uncertain && _snapshot is not null;
    public bool HasDraft => _draft.Count > 0 || _snapshot is not null && PathMode != _snapshot.PathMode;
    public bool NeedsHighImpactConfirmation => _draft.Keys.Any(name => EnvironmentValidation.IsHighImpact(name, _windows))
        || _snapshot is not null && PathMode != _snapshot.PathMode && _snapshot.Variables.Any(value => value.Name.Equals("PATH", _windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    public string DraftSummary => string.Join(", ", _draft.Values.Select(value => (value.Operation == EnvironmentMutationKind.Delete ? "− " : "+ ") + value.Name));
    public EnvironmentPathMode PathMode => AppendPath ? EnvironmentPathMode.Append : EnvironmentPathMode.Replace;
    private bool CanApply() => CanEdit && HasDraft && (!NeedsHighImpactConfirmation || ConfirmHighImpact);
    private bool CanReload() => !IsBusy && !_disposed;
    partial void OnIsBusyChanged(bool value) => Update();
    partial void OnAppendPathChanged(bool value) { ConfirmHighImpact = false; Update(); }
    partial void OnConfirmHighImpactChanged(bool value) => Update();
    partial void OnSelectedVariableChanged(EnvironmentVariable? value)
    {
        VariableName = value?.Name ?? "";
        VariableValue = value?.RawValue ?? "";
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => RunAsync(async ct =>
    {
        var connection = _client.CaptureConnection();
        _connection = connection;
        var snapshot = await _client.ReadAsync(connection, ct);
        Check(connection, ct);
        Load(snapshot);
        Status = LocalizedText.Ref("settings.workspace_environment.loaded");
    });

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void StageSet() => Stage(new(VariableName, EnvironmentMutationKind.Set, VariableValue,
        _snapshot?.Variables.FirstOrDefault(value => value.Name.Equals(VariableName, _windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))?.ValueKind ?? EnvironmentValueKind.String));
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void StageDelete() => Stage(new(VariableName, EnvironmentMutationKind.Delete));
    private void Stage(EnvironmentMutation mutation)
    {
        if (EnvironmentValidation.Validate(new([mutation], true), _windows) is { } error)
        { Status = LocalizedText.Ref(error); return; }
        _draft[mutation.Name] = mutation;
        ConfirmHighImpact = false;
        Update();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync() => RunAsync(async ct =>
    {
        var connection = _connection!;
        Check(connection, ct);
        var changes = _draft.Values.ToArray();
        _uncertain = true;
        var saved = await _client.SaveAsync(connection, new(_snapshot!.Revision, new(changes, ConfirmHighImpact), PathMode), ct);
        Check(connection, ct);
        Load(saved);
        Status = LocalizedText.Ref("settings.workspace_environment.saved");
    });

    private void Load(WorkspaceEnvironmentSnapshot snapshot)
    {
        _snapshot = snapshot; _draft.Clear(); _uncertain = false;
        Variables = snapshot.Variables; SelectedVariable = null;
        VariableName = VariableValue = ""; AppendPath = snapshot.PathMode == EnvironmentPathMode.Append;
        ConfirmHighImpact = false; Update();
    }
    private void Check(WorkspaceEnvironmentConnection connection, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); if (_disposed || !_client.IsCurrent(connection)) throw new OperationCanceledException(ct); }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _disposed) return;
        IsBusy = true;
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_disposed)
            {
                // Structured HTTP rejection proves no commit. Transport failure leaves the outcome uncertain.
                if (error is RelaxKonOSAuthException auth && auth.Status is >= 400 and < 500) _uncertain = false;
                Status = LocalizedText.Ref(_uncertain ? "settings.workspace_environment.unknown"
                    : error is RelaxKonOSAuthException { Status: 409 } ? "settings.workspace_environment.conflict" : "settings.workspace_environment.failed");
            }
        }
        finally { IsBusy = false; }
    }
    private void Update()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(HasDraft));
        OnPropertyChanged(nameof(DraftSummary)); OnPropertyChanged(nameof(NeedsHighImpactConfirmation));
        StageSetCommand.NotifyCanExecuteChanged(); StageDeleteCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged(); ReloadCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
    private void SessionChanged(object? sender, AuthSessionStateChangedEventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || _connection is null || _client.IsCurrent(_connection)) return;
        Dispose(); RequestClose?.Invoke();
    });
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _lifetime.Dispose();
        _session.StateChanged -= SessionChanged;
        _snapshot = null; _connection = null; _draft.Clear(); Variables = []; SelectedVariable = null;
        VariableName = VariableValue = ""; Update();
    }
}
