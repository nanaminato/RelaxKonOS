using System.ComponentModel;
using System.Windows.Input;

namespace RelaxKonOS.App.Settings.Views;

public interface ISystemPage : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    IHostIdentityEditor HostIdentity { get; }
    IReadOnlyList<ServerModuleRow> ServerModules { get; }
    object? UsageMemoryStatus { get; }
    Task RefreshServerModulesAsync();
    string AppVersion { get; }
    string ConnectionGuidance { get; }
    string ConnectionState { get; }
    string ExecutionIdentity { get; }
    string ListenerScope { get; }
    string Platform { get; }
    string ServerMode { get; }
    string ServerModulesStatus { get; }
    string ServerUrl { get; }
    string UserName { get; }
    string WorkspaceName { get; }
    bool HasServerModulesStatus { get; }
    ICommand ClearUsageMemoryCommand { get; }
    ICommand OpenEnvironmentVariablesCommand { get; }
    ICommand OpenPerformanceOptionsCommand { get; }
    ICommand OpenServerHttpsCommand { get; }
    ICommand OpenServerModulesCommand { get; }
    ICommand OpenWorkspaceEnvironmentCommand { get; }
    ICommand RefreshServerModulesCommand { get; }
}

public interface IHostIdentityEditor : INotifyPropertyChanged
{
    string CurrentHostName { get; }
    string NameLengthHint { get; }
    string NameProblem { get; }
    string PendingHostName { get; }
    string ProblemCode { get; }
    string StatusText { get; }
    string TargetText { get; }
    string DraftName { get; set; }
    bool CanEdit { get; }
    bool HasDraft { get; }
    bool HasNameLengthHint { get; }
    bool HasNameProblem { get; }
    bool HasProblem { get; }
    bool IsBusy { get; }
    bool IsCompleted { get; }
    bool RestartPending { get; }
    bool ShowApplyAction { get; }
    bool ShowQueryAction { get; }
    ICommand ApplyCommand { get; }
    ICommand QueryCommand { get; }
    ICommand ReloadCommand { get; }
    ICommand ResetDraftCommand { get; }
}

public sealed record ServerModuleRow(string Id, bool Enabled, string StateText, string CapabilitiesText, string NotesText)
{
    public bool HasNotes => NotesText.Length > 0;
    public bool HasCapabilities => CapabilitiesText.Length > 0;
}
